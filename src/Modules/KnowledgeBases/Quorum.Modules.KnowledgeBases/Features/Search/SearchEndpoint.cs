using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBases.Features.Search;

/// <param name="Filters">The parameters of the generated tool schema (D-11): <c>department</c>, <c>year_min</c>...</param>
/// <param name="Limit">How many results, up to 20; five when omitted.</param>
internal sealed record TestSearchRequest(string? Query, Dictionary<string, JsonElement>? Filters, int? Limit);

internal sealed record TestSearchResponse(IReadOnlyList<TestSearchHit> Results, IReadOnlyList<string> Notes);

internal sealed record TestSearchHit(double Score, string Text, TestSearchSource Source);

internal sealed record TestSearchSource(string KnowledgeBase, string? DocumentId, string? Title, string? Position, string? Url);

/// <summary>
/// The administrator's test search on one knowledge base (D-22): to see what a registration finds before a tool exposes it. It is
/// not a tool call, so it is not audited; the scores are shown because that is what the administrator is judging.
/// </summary>
internal sealed class SearchEndpoint : IEndpointGroup
{
    private const int DefaultLimit = 5;

    public void Map(IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/knowledge-bases")
            .WithTags("Knowledge bases")
            .RequireAuthorization(QuorumPolicies.KnowledgeBaseManage)
            .MapPost("/{id:guid}/search", SearchAsync)
            .WithName("SearchKnowledgeBase");
    }

    private static async Task<Results<Ok<TestSearchResponse>, ProblemHttpResult>> SearchAsync(
        Guid id,
        TestSearchRequest request,
        KnowledgeBasesDbContext db,
        KnowledgeBaseSearchEngine engine,
        CancellationToken cancellationToken)
    {
        var limit = request.Limit ?? DefaultLimit;
        if (SearchRules.Validate(request.Query, limit, scoreThreshold: null, maxResultCharacters: null) is { } invalid)
        {
            return invalid.ToProblem();
        }

        // The tenant filter keeps this to the administrator's own knowledge bases: what others share is searched through tools.
        if (await db.KnowledgeBases.AsNoTracking().SingleOrDefaultAsync(k => k.Id == id, cancellationToken) is not { } registration)
        {
            return Error.NotFound("knowledge_base.not_found", "The knowledge base does not exist.").ToProblem();
        }

        var result = await engine.SearchAsync(
            [registration],
            new SearchInput(request.Query!.Trim(), request.Filters ?? [], FixedFilters: new Dictionary<string, JsonElement>(), limit, ScoreThreshold: null, MaxResultCharacters: null),
            cancellationToken);
        if (!result.IsSuccess)
        {
            return result.Error.ToProblem();
        }

        var found = result.Value;
        var notes = new List<string>();
        if (found.UnusableChunks > 0)
        {
            notes.Add($"{found.UnusableChunks} result(s) were left out because their payload has no text at the mapped path.");
        }

        return TypedResults.Ok(new TestSearchResponse(
            [.. found.Chunks.Select(c => new TestSearchHit(c.Score, c.Text, new TestSearchSource(c.KnowledgeBase, c.DocumentId, c.Title, c.Position, c.Url)))],
            notes));
    }
}
