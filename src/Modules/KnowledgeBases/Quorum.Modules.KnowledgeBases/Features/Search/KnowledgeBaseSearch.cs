using Microsoft.EntityFrameworkCore;
using Quorum.Modules.KnowledgeBases.Contracts;
using Quorum.Modules.KnowledgeBases.Features.Registry;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.Shared.Results;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.KnowledgeBases.Features.Search;

/// <summary>
/// <see cref="IKnowledgeBaseSearch"/>: the entry for the rag tool. It takes tenant and assistant as arguments so that a search never
/// depends on who happens to be the ambient caller, and refuses a tenant that is not the one of the request.
/// </summary>
internal sealed class KnowledgeBaseSearch(KnowledgeBasesDbContext db, ITenantContext tenant, KnowledgeBaseSearchEngine engine) : IKnowledgeBaseSearch
{
    public const int MaxQueryLength = 2000;

    public const int MaxTopK = 20;

    public async Task<Result<KnowledgeBaseSearchResponse>> SearchAsync(KnowledgeBaseSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (SearchRules.Validate(request.Query, request.TopK, request.ScoreThreshold, request.MaxResultCharacters) is { } invalid)
        {
            return invalid;
        }

        var none = Error.NotFound(KnowledgeBaseSearchErrors.NotFound, "None of the knowledge bases can be searched.");
        if (tenant.TenantId is not { } tenantId || tenantId != request.TenantId || request.KnowledgeBaseIds.Count == 0)
        {
            return none;
        }

        var ids = request.KnowledgeBaseIds.Distinct().ToList();
        var bases = await KnowledgeBaseVisibility.ForAssistant(db, request.AssistantId).AsNoTracking().Where(k => ids.Contains(k.Id)).ToListAsync(cancellationToken);
        if (bases.Count == 0)
        {
            return none;
        }

        return await engine.SearchAsync(
            bases,
            new SearchInput(request.Query.Trim(), request.Filters, request.FixedFilters, request.TopK, request.ScoreThreshold, request.MaxResultCharacters, request.FilterFields),
            cancellationToken);
    }
}

/// <summary>What every way into a search checks about the query and the limits.</summary>
internal static class SearchRules
{
    public static Error? Validate(string? query, int topK, double? scoreThreshold, int? maxResultCharacters)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Error.Validation(KnowledgeBaseSearchErrors.InvalidRequest, "The query is required.");
        }

        if (query.Trim().Length > KnowledgeBaseSearch.MaxQueryLength)
        {
            return Error.Validation(KnowledgeBaseSearchErrors.InvalidRequest, $"The query can have up to {KnowledgeBaseSearch.MaxQueryLength} characters.");
        }

        if (topK is < 1 or > KnowledgeBaseSearch.MaxTopK)
        {
            return Error.Validation(KnowledgeBaseSearchErrors.InvalidRequest, $"The number of results must be between 1 and {KnowledgeBaseSearch.MaxTopK}.");
        }

        if (scoreThreshold is { } threshold && !double.IsFinite(threshold))
        {
            return Error.Validation(KnowledgeBaseSearchErrors.InvalidRequest, "The score threshold must be a number.");
        }

        if (maxResultCharacters is <= 0)
        {
            return Error.Validation(KnowledgeBaseSearchErrors.InvalidRequest, "The result size limit must be positive.");
        }

        return null;
    }
}
