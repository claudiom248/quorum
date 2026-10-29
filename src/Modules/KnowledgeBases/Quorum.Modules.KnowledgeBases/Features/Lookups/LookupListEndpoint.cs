using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Features.Registry;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBases.Features.Lookups;

/// <summary>
/// Every lookup the administrator can read in one list (spec 010): the manual ones and the ones derived from a knowledge base of the
/// tenant. The lookups of a knowledge base another tenant shares are not here: they reach a tenant only through a tool.
/// </summary>
internal sealed class LookupListEndpoint : IEndpointGroup
{
    private static readonly Error InvalidKind = Error.Validation("list.invalid_filter", "The kind must be Manual or Derived.");

    private static readonly Error InvalidKnowledgeBase = Error.Validation("list.invalid_filter", "The knowledgeBase filter takes ids.");

    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/knowledge-bases/lookups", ListAsync)
            .WithTags("Knowledge bases")
            .WithName("ListLookups")
            .RequireAuthorization(QuorumPolicies.KnowledgeBaseManage);

    private static async Task<Results<Ok<Page<LookupListItem>>, ProblemHttpResult>> ListAsync(HttpRequest request, KnowledgeBasesDbContext db, CancellationToken cancellationToken)
    {
        var query = ListQuery.Parse(request.Query);
        if (!query.IsSuccess)
        {
            return query.Error.ToProblem();
        }

        var kinds = new List<LookupListKind>();
        foreach (var text in request.Query["kind"].SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            if (!Enum.TryParse<LookupListKind>(text, ignoreCase: true, out var kind) || !Enum.IsDefined(kind))
            {
                return InvalidKind.ToProblem();
            }

            kinds.Add(kind);
        }

        var knowledgeBases = new List<Guid>();
        foreach (var text in request.Query["knowledgeBase"].SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            if (!Guid.TryParse(text, out var knowledgeBaseId))
            {
                return InvalidKnowledgeBase.ToProblem();
            }

            knowledgeBases.Add(knowledgeBaseId);
        }

        var rows = Rows(db);
        if (knowledgeBases.Count > 0)
        {
            // A manual lookup comes from no knowledge base: it is left out when the list is narrowed to some.
            rows = rows.Where(r => r.KnowledgeBaseId != null && knowledgeBases.Contains(r.KnowledgeBaseId.Value));
        }

        if (kinds.Count > 0)
        {
            rows = rows.Where(r => kinds.Contains(r.Kind));
        }

        var filtered = ScopeFilter.Apply(rows, query.Value);
        if (!filtered.IsSuccess)
        {
            return filtered.Error.ToProblem();
        }

        var page = await filtered.Value.ToPageAsync(query.Value, "lookups", r => r.Name, r => r.Id, r => r, cancellationToken);
        if (!page.IsSuccess)
        {
            return page.Error.ToProblem();
        }

        // The count of values is the one of the whole knowledge base, which every lookup has; read for the page only.
        var ids = page.Value.Items.Select(r => r.Id).ToList();
        var wholeKnowledgeBase = LookupSliceKey.WholeKnowledgeBase.Hash;
        var counts = await db.LookupSlices.IgnoreQueryFilters().AsNoTracking()
            .Where(s => ids.Contains(s.LookupId) && s.FilterHash == wholeKnowledgeBase)
            .ToDictionaryAsync(s => s.LookupId, s => s, cancellationToken);
        var tenantId = db.CurrentTenantId;

        return TypedResults.Ok(new Page<LookupListItem>(
            [.. page.Value.Items.Select(r => LookupListItem.From(r, counts.GetValueOrDefault(r.Id) is { } slice ? slice.ValueCount : 0, tenantId, LookupListItem.FreshnessOf(counts.GetValueOrDefault(r.Id))))],
            page.Value.Cursor));
    }

    // A derived lookup is named after its field, or after its knowledge base when it lists entities, and has the scope of that knowledge base.
    private static IQueryable<LookupRow> Rows(KnowledgeBasesDbContext db) =>
        from lookup in LookupVisibility.Readable(db).AsNoTracking()
        join knowledgeBase in db.KnowledgeBases.IgnoreQueryFilters() on lookup.KnowledgeBaseId equals knowledgeBase.Id into knowledgeBases
        from knowledgeBase in knowledgeBases.DefaultIfEmpty()
        join field in db.DictionaryFields.IgnoreQueryFilters() on lookup.DictionaryFieldId equals field.Id into fields
        from field in fields.DefaultIfEmpty()
        let manual = lookup.Kind == LookupKind.Manual
        select new LookupRow
        {
            Id = lookup.Id,
            TenantId = lookup.TenantId,
            Name = lookup.Name,
            Storage = lookup.Storage,
            Kind = manual ? LookupListKind.Manual : LookupListKind.Derived,
            KnowledgeBaseId = lookup.KnowledgeBaseId,
            KnowledgeBaseName = knowledgeBase.Name,
            Field = field.Name,
            Unit = lookup.Unit,
            Scope = manual
                ? (lookup.Scope == LookupScope.Global ? KnowledgeBaseScope.Global : KnowledgeBaseScope.Tenant)
                : knowledgeBase.Scope,
            ScopeTenantIds = manual ? lookup.ScopeTenantIds : knowledgeBase.ScopeTenantIds,
            ScopeAssistantIds = knowledgeBase.ScopeAssistantIds,
        };
}

internal enum LookupListKind
{
    Manual,
    Derived,
}

internal sealed class LookupRow
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public string Name { get; init; } = null!;

    public LookupStorage Storage { get; init; }

    public LookupListKind Kind { get; init; }

    public Guid? KnowledgeBaseId { get; init; }

    public string? KnowledgeBaseName { get; init; }

    public string? Field { get; init; }

    public LookupUnit? Unit { get; init; }

    public KnowledgeBaseScope Scope { get; init; }

    public List<Guid> ScopeTenantIds { get; init; } = [];

    /// <summary>Null for a manual lookup, which has no knowledge base to take them from.</summary>
    public List<Guid>? ScopeAssistantIds { get; init; }
}

internal sealed record LookupKnowledgeBase(Guid Id, string Name);

/// <param name="KnowledgeBase">Only for a derived lookup.</param>
/// <param name="Field">The dictionary field a derived lookup lists; none for a manual lookup or an entity lookup.</param>
/// <param name="Unit">What the counts count, when the lookup has counts.</param>
/// <param name="Shared"><see langword="true"/> for a manual lookup of another tenant: it can be read, not changed.</param>
internal sealed record LookupListItem(
    Guid Id,
    string Name,
    string Kind,
    LookupKnowledgeBase? KnowledgeBase,
    string? Field,
    string? Unit,
    ScopeResponse Scope,
    int ValueCount,
    bool Shared,
    string Storage,
    string Freshness)
{
    /// <summary>What the administrator reads of a list: <c>Ready</c>, <c>Stale</c> (the last computation failed), <c>Preparing</c> or <c>Failed</c> (spec 012 D-12).</summary>
    public static string FreshnessOf(LookupSlice? slice) => slice switch
    {
        null => "Preparing",
        { ComputingSince: not null } => "Preparing",
        { Status: LookupStatus.Failed } => "Failed",
        { Stale: true } => "Stale",
        { Status: LookupStatus.Pending } => "Preparing",
        _ => "Ready",
    };

    public static LookupListItem From(LookupRow row, int valueCount, Guid? currentTenantId, string freshness)
    {
        var shared = row.TenantId != currentTenantId;
        return new LookupListItem(
            row.Id,
            row.Name,
            row.Kind.ToString(),
            row.KnowledgeBaseId is { } knowledgeBaseId ? new LookupKnowledgeBase(knowledgeBaseId, row.KnowledgeBaseName ?? string.Empty) : null,
            row.Field,
            row.Unit?.ToString(),
            new ScopeResponse(row.Scope.ToString(), shared ? [] : row.ScopeTenantIds, shared ? [] : row.ScopeAssistantIds ?? []),
            valueCount,
            shared,
            row.Storage.ToString(),
            freshness);
    }
}
