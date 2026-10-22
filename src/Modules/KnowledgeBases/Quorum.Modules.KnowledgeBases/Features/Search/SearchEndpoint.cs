using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Features.Dictionary;
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

        var mappedIds = registration.Mapping.Fields.Select(f => f.DictionaryFieldId).ToList();
        var visible = await DictionaryVisibility.Fields(db).AsNoTracking().ToListAsync(cancellationToken);

        var result = await engine.SearchAsync(
            [registration],
            new SearchInput(request.Query!.Trim(), request.Filters ?? [], FixedFilters: new Dictionary<string, JsonElement>(), limit, ScoreThreshold: null, MaxResultCharacters: null, TestSearchNames.For(visible, mappedIds)),
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

/// <summary>
/// The filter names of a test search on one knowledge base: the field name, or, when two of the fields it maps share a name in
/// different clusters, the cluster and the name joined by an underscore (a field with no cluster keeps its name). This is the one
/// place the rule lives: the knowledge base detail hands the names to the admin pages.
/// </summary>
internal static class TestSearchNames
{
    /// <summary>The filter name of each field a knowledge base maps, by field id.</summary>
    public static Dictionary<Guid, string> ForMapped(IReadOnlyList<DictionaryField> mapped)
    {
        ArgumentNullException.ThrowIfNull(mapped);

        var names = new Dictionary<Guid, string>();
        foreach (var group in mapped.GroupBy(f => f.Name, StringComparer.Ordinal))
        {
            foreach (var field in group)
            {
                names[field.Id] = group.Count() > 1 && field.Cluster is not null ? $"{field.Cluster}_{field.Name}" : field.Name;
            }
        }

        return names;
    }

    public static Dictionary<string, Guid> For(IReadOnlyList<DictionaryField> visible, IReadOnlyCollection<Guid> mappedIds)
    {
        ArgumentNullException.ThrowIfNull(visible);
        ArgumentNullException.ThrowIfNull(mappedIds);

        var names = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var (id, name) in ForMapped([.. visible.Where(f => mappedIds.Contains(f.Id))]))
        {
            names.TryAdd(name, id);
        }

        // A filter on a field the knowledge base did not map is still a valid filter: the knowledge base is skipped for it (D-12).
        foreach (var field in visible.Where(f => !mappedIds.Contains(f.Id)).OrderBy(f => f.Cluster ?? string.Empty, StringComparer.Ordinal))
        {
            names.TryAdd(field.Name, field.Id);
        }

        return names;
    }
}
