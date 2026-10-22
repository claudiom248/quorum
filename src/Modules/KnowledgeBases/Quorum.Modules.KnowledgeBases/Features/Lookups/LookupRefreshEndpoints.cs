using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBases.Features.Lookups;

/// <summary>The lookups of a knowledge base as the administrator manages them: their state, and a refresh on request (spec 007 D-10).</summary>
internal sealed class LookupRefreshEndpoints : IEndpointGroup
{
    private static readonly Error KnowledgeBaseNotFound = Error.NotFound("knowledge_base.not_found", "The knowledge base does not exist.");

    private static readonly Error LookupNotFound = Error.NotFound("lookup.not_found", "The lookup does not exist or has nothing to refresh.");

    private static readonly Error RefreshRunning = Error.Conflict("lookup.refresh_running", "The lookups of this knowledge base are being computed. Try again when it ends.");

    public void Map(IEndpointRouteBuilder app)
    {
        var knowledgeBases = app.MapGroup("/api/knowledge-bases")
            .WithTags("Knowledge bases")
            .RequireAuthorization(QuorumPolicies.KnowledgeBaseManage);

        knowledgeBases.MapGet("/{id:guid}/lookups", ListAsync).WithName("ListKnowledgeBaseLookups");
        knowledgeBases.MapPost("/{id:guid}/lookups/refresh", RefreshKnowledgeBaseAsync).WithName("RefreshKnowledgeBaseLookups");
        knowledgeBases.MapPost("/lookups/{lookupId:guid}/refresh", RefreshLookupAsync).WithName("RefreshLookup");
    }

    private static async Task<Results<Ok<List<KnowledgeBaseLookupDto>>, ProblemHttpResult>> ListAsync(Guid id, KnowledgeBasesDbContext db, CancellationToken cancellationToken)
    {
        // The tenant filter is on: another tenant's knowledge base reads as missing, shared or not (its lookups reach a tenant only through a tool).
        var knowledgeBase = await db.KnowledgeBases.AsNoTracking().SingleOrDefaultAsync(k => k.Id == id, cancellationToken);
        if (knowledgeBase is null)
        {
            return KnowledgeBaseNotFound.ToProblem();
        }

        var lookups = await db.Lookups.AsNoTracking().Where(l => l.KnowledgeBaseId == id).ToListAsync(cancellationToken);
        var lookupIds = lookups.Select(l => l.Id).ToList();
        var slices = await db.LookupSlices.AsNoTracking().Where(s => lookupIds.Contains(s.LookupId)).ToListAsync(cancellationToken);
        var fieldIds = lookups.Where(l => l.DictionaryFieldId != null).Select(l => l.DictionaryFieldId!.Value).ToList();
        var fieldNames = await db.DictionaryFields.IgnoreQueryFilters().AsNoTracking()
            .Where(f => fieldIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, f => f.Name, cancellationToken);

        var items = lookups
            .Select(l =>
            {
                var field = l.DictionaryFieldId is { } fieldId ? fieldNames.GetValueOrDefault(fieldId) : null;
                return KnowledgeBaseLookupDto.From(l, field ?? knowledgeBase.Name, field, slices.Where(s => s.LookupId == l.Id));
            })
            .OrderBy(l => l.Name, StringComparer.Ordinal)
            .ToList();
        return TypedResults.Ok(items);
    }

    private static async Task<Results<Accepted, ProblemHttpResult>> RefreshKnowledgeBaseAsync(
        Guid id,
        KnowledgeBasesDbContext db,
        LookupRefreshSignal signal,
        TimeProvider clock,
        IOptions<LookupOptions> options,
        CancellationToken cancellationToken)
    {
        if (!await db.KnowledgeBases.AnyAsync(k => k.Id == id, cancellationToken))
        {
            return KnowledgeBaseNotFound.ToProblem();
        }

        return await RequestRefreshAsync(db, id, lookupId: null, signal, clock, options.Value, cancellationToken);
    }

    private static async Task<Results<Accepted, ProblemHttpResult>> RefreshLookupAsync(
        Guid lookupId,
        KnowledgeBasesDbContext db,
        LookupRefreshSignal signal,
        TimeProvider clock,
        IOptions<LookupOptions> options,
        CancellationToken cancellationToken)
    {
        var lookup = await db.Lookups.AsNoTracking().SingleOrDefaultAsync(l => l.Id == lookupId, cancellationToken);

        // A manual lookup has nothing to compute.
        if (lookup?.KnowledgeBaseId is not { } knowledgeBaseId)
        {
            return LookupNotFound.ToProblem();
        }

        return await RequestRefreshAsync(db, knowledgeBaseId, lookupId, signal, clock, options.Value, cancellationToken);
    }

    /// <summary>
    /// One computation per knowledge base at a time: while any of its slices is being computed the request is refused. Otherwise its
    /// slices become due now and the service is woken; the computation runs there, not here.
    /// </summary>
    private static async Task<Results<Accepted, ProblemHttpResult>> RequestRefreshAsync(
        KnowledgeBasesDbContext db,
        Guid knowledgeBaseId,
        Guid? lookupId,
        LookupRefreshSignal signal,
        TimeProvider clock,
        LookupOptions options,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var running = await LookupRefresher.RunningKnowledgeBasesAsync(db, now - options.Lease, cancellationToken);
        if (running.Contains(knowledgeBaseId))
        {
            return RefreshRunning.ToProblem();
        }

        var slices = db.LookupSlices.IgnoreQueryFilters().Where(s => db.Lookups.IgnoreQueryFilters().Any(l =>
            l.Id == s.LookupId && l.KnowledgeBaseId == knowledgeBaseId && (lookupId == null || l.Id == lookupId)));
        _ = await slices.ExecuteUpdateAsync(set => set.SetProperty(s => s.NextRefreshAt, now), cancellationToken);
        signal.Wake();
        return TypedResults.Accepted((string?)null);
    }
}

/// <param name="Field">The field a computed lookup lists.</param>
internal sealed record KnowledgeBaseLookupDto(Guid Id, string Name, string Kind, string? Field, string? Unit, List<LookupSliceDto> Slices)
{
    public static KnowledgeBaseLookupDto From(Lookup lookup, string name, string? field, IEnumerable<LookupSlice> slices) => new(
        lookup.Id,
        name,
        lookup.Kind.ToString(),
        field,
        lookup.Unit?.ToString().ToLowerInvariant(),
        [.. slices.Select(LookupSliceDto.From)]);
}

/// <param name="Hash">Names the slice in <c>GET /knowledge-bases/lookups/{lookupId}/values?slice=</c>.</param>
/// <param name="Running">A computation of the slice is in progress.</param>
internal sealed record LookupSliceDto(
    string Hash,
    JsonElement Filters,
    string Status,
    bool Stale,
    int ValueCount,
    bool Truncated,
    DateTimeOffset? ComputedAt,
    DateTimeOffset? NextRefreshAt,
    string? Error,
    bool Running)
{
    public static LookupSliceDto From(LookupSlice slice) => new(
        slice.FilterHash,
        JsonSerializer.Deserialize<JsonElement>(slice.Filters),
        slice.Status.ToString(),
        slice.Stale,
        slice.ValueCount,
        slice.Truncated,
        slice.ComputedAt,
        slice.NextRefreshAt,
        slice.LastError,
        slice.ComputingSince is not null);
}
