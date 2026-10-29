using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.KnowledgeBases.Contracts;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Features.Dictionary;
using Quorum.Modules.KnowledgeBases.Features.Registry;
using Quorum.Modules.KnowledgeBases.Features.Search;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.KnowledgeBases.Features.Lookups;

/// <summary>
/// <see cref="IKnowledgeBaseLookups"/>. Like the catalog and the search, it answers for the tenant of the request only: for any other
/// it behaves as if that tenant could see nothing, which also tells a caller nothing about what exists.
/// </summary>
internal sealed class KnowledgeBaseLookups(KnowledgeBasesDbContext db, ITenantContext tenant, LookupCache cache, LiveLookupOperations live, TimeProvider clock) : IKnowledgeBaseLookups
{
    /// <summary>The most slices a knowledge base has, counting the one of the whole knowledge base (spec 007 D-6).</summary>
    public const int MaxSlicesPerKnowledgeBase = 50;

    private const long DeclareLockKey = 7_007_002;

    public async Task<IReadOnlyList<LookupInfo>> ListAsync(LookupListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!IsCurrentTenant(query.TenantId))
        {
            return [];
        }

        var visible = await LoadVisibleAsync(query.AssistantId, query.LookupIds, cancellationToken);
        return [.. visible.Select(v => v.Info).OrderBy(i => i.Name, StringComparer.Ordinal).ThenBy(i => i.KnowledgeBaseName, StringComparer.Ordinal)];
    }

    public async Task<IReadOnlyList<Guid>> HiddenAsync(LookupScopeQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var wanted = query.LookupIds.Distinct().ToList();
        if (!IsCurrentTenant(query.TenantId))
        {
            return wanted;
        }

        // What the owner cannot see itself is hidden whatever the scope, and a missing one is hidden like any other.
        var visible = await LoadVisibleAsync(assistantId: null, wanted, cancellationToken);
        var fieldIds = visible.Select(v => v.Lookup.DictionaryFieldId).OfType<Guid>().Distinct().ToList();
        var fields = await DictionaryVisibility.Fields(db).AsNoTracking().Where(f => fieldIds.Contains(f.Id)).ToListAsync(cancellationToken);

        var seen = visible
            .Where(v => IsVisibleTo(v, query.TenantId, query.Scope, fields))
            .Select(v => v.Lookup.Id)
            .ToHashSet();
        return [.. wanted.Where(id => !seen.Contains(id))];
    }

    public async Task<SliceDeclaration> DeclareSlicesAsync(SliceDeclarationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsCurrentTenant(request.TenantId))
        {
            return new SliceDeclaration(SliceDeclarationOutcome.InvalidFilters, Problem: "The filters cannot be checked.");
        }

        var key = await ParseAsync(request.Filters, cancellationToken);
        if (key.Problem is not null)
        {
            return new SliceDeclaration(SliceDeclarationOutcome.InvalidFilters, Problem: key.Problem);
        }

        // Declarations run one at a time: two tools with the same filters would otherwise both insert the same slice.
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({DeclareLockKey})", cancellationToken);

        var wanted = request.LookupIds.Distinct().ToList();
        var visible = await LoadVisibleAsync(assistantId: null, wanted, cancellationToken);
        var now = clock.GetUtcNow();

        // Every slice of the lookups this tool binds, and every slice the tool uses now: the second set tells which ones it lets go.
        var lookupIds = visible.Select(v => v.Lookup.Id).ToList();
        var slices = await db.LookupSlices.IgnoreQueryFilters()
            .Where(s => lookupIds.Contains(s.LookupId) || s.ToolIds.Contains(request.ToolId))
            .ToListAsync(cancellationToken);

        var needed = new HashSet<Guid>();
        foreach (var entry in visible)
        {
            var hash = entry.Lookup.Kind == LookupKind.Manual ? LookupSliceKey.WholeKnowledgeBase.Hash : key.Value!.Hash;
            if (entry.Lookup.Kind != LookupKind.Manual && !MapsEveryField(entry.KnowledgeBase!, key.Filters))
            {
                // The knowledge base has no such slice: the tool skips it and says so when it is read (spec 007 D-6, AC-26).
                continue;
            }

            var slice = slices.FirstOrDefault(s => s.LookupId == entry.Lookup.Id && s.FilterHash == hash);
            if (slice is null)
            {
                var canonical = entry.Lookup.Kind == LookupKind.Manual ? LookupSliceKey.WholeKnowledgeBase : key.Value!;
                slice = LookupSlice.Create(entry.Lookup.TenantId, entry.Lookup.Id, canonical.Hash, canonical.Filters, now);
                db.LookupSlices.Add(slice);
                slices.Add(slice);
            }

            slice.AddUser(request.ToolId);
            needed.Add(slice.Id);
        }

        var released = new List<LookupSlice>();
        foreach (var slice in slices.Where(s => !needed.Contains(s.Id)))
        {
            if (slice.RemoveUser(request.ToolId))
            {
                released.Add(slice);
            }
        }

        // A slice made for a tool goes with its last user; the one of the whole knowledge base belongs to the lookup.
        var orphans = released.Where(s => s.ToolIds.Count == 0 && s.FilterHash != LookupSliceKey.WholeKnowledgeBase.Hash).ToList();
        if (await TooManySlicesAsync(visible, slices, orphans, cancellationToken) is { } full)
        {
            // Nothing is saved: the caller's transaction ends without it and the change tracker is dropped with the request.
            db.ChangeTracker.Clear();
            return new SliceDeclaration(SliceDeclarationOutcome.TooManySlices, full);
        }

        db.LookupSlices.RemoveRange(orphans);
        await db.SaveChangesAsync(cancellationToken);
        foreach (var orphan in orphans)
        {
            await cache.EvictSliceAsync(orphan.Id, cancellationToken);
        }

        return new SliceDeclaration(SliceDeclarationOutcome.Declared);
    }

    public async Task ReleaseSlicesAsync(Guid tenantId, Guid toolId, CancellationToken cancellationToken)
    {
        if (!IsCurrentTenant(tenantId))
        {
            return;
        }

        // The same lock as a declaration: both rewrite the list of tools of a slice, and the later write would undo the earlier one.
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({DeclareLockKey})", cancellationToken);

        var slices = await db.LookupSlices.IgnoreQueryFilters().Where(s => s.ToolIds.Contains(toolId)).ToListAsync(cancellationToken);
        foreach (var slice in slices)
        {
            slice.RemoveUser(toolId);
        }

        var orphans = slices.Where(s => s.ToolIds.Count == 0 && s.FilterHash != LookupSliceKey.WholeKnowledgeBase.Hash).ToList();
        db.LookupSlices.RemoveRange(orphans);
        await db.SaveChangesAsync(cancellationToken);
        foreach (var orphan in orphans)
        {
            await cache.EvictSliceAsync(orphan.Id, cancellationToken);
        }
    }

    public async Task<LookupRead> ReadAsync(LookupReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsCurrentTenant(request.TenantId))
        {
            return LookupRead.Of(LookupReadOutcome.NotAvailable);
        }

        var found = (await LoadVisibleAsync(request.AssistantId, [request.LookupId], cancellationToken)).FirstOrDefault();
        if (found is null)
        {
            return LookupRead.Of(LookupReadOutcome.NotAvailable);
        }

        var (lookup, info) = (found.Lookup, found.Info);
        var key = lookup.Kind == LookupKind.Manual ? ParsedKey.Whole : await ParseAsync(request.Filters, cancellationToken);
        if (key.Problem is not null)
        {
            return LookupRead.Of(LookupReadOutcome.InvalidFilters, info.Name, info.KnowledgeBaseName);
        }

        if (lookup.Kind != LookupKind.Manual && !MapsEveryField(found.KnowledgeBase!, key.Filters))
        {
            return LookupRead.Of(LookupReadOutcome.KnowledgeBaseSkipped, info.Name, info.KnowledgeBaseName);
        }

        var rowFilters = LookupFilters.Parse(RowFiltersJson(request.RowFilters), lookup.Attributes);
        if (!rowFilters.IsSuccess)
        {
            return LookupRead.Of(LookupReadOutcome.InvalidFilters, info.Name, info.KnowledgeBaseName);
        }

        if (lookup.Storage == LookupStorage.Live)
        {
            // The store filters what it can scan, not the attributes of a snapshot: a row filter on a live list is not served yet.
            return rowFilters.Value.Count > 0
                ? LookupRead.Of(LookupReadOutcome.InvalidFilters, info.Name, info.KnowledgeBaseName)
                : await LiveReadAsync(request, lookup, info, found.KnowledgeBase!, cancellationToken);
        }

        var hash = key.Value!.Hash;
        var slice = await db.LookupSlices.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(s => s.LookupId == lookup.Id && s.FilterHash == hash, cancellationToken);
        if (slice is null)
        {
            return LookupRead.Of(LookupReadOutcome.Preparing, info.Name, info.KnowledgeBaseName);
        }

        if (slice.CurrentGeneration == 0)
        {
            return LookupRead.Of(slice.Status == LookupStatus.Failed ? LookupReadOutcome.Failed : LookupReadOutcome.Preparing, info.Name, info.KnowledgeBaseName);
        }

        var sort = request.Sort switch
        {
            LookupSortOrder.Name => LookupSort.Name,
            LookupSortOrder.Attribute => LookupSort.Attribute,
            _ => LookupSort.Count,
        };
        LookupAttributeSort? attribute = null;
        if (sort == LookupSort.Attribute)
        {
            if (request.Attribute is not { } asked || info.SortableAttributes?.Contains(asked.Name, StringComparer.Ordinal) != true)
            {
                return LookupRead.Of(LookupReadOutcome.InvalidSort, info.Name, info.KnowledgeBaseName);
            }

            attribute = new LookupAttributeSort(asked.Name, asked.Descending);
        }

        var page = await cache.ReadAsync(db, lookup, slice, new LookupPageRequest(request.Search, sort, request.Limit, request.Cursor, attribute, rowFilters.Value), cancellationToken);
        if (!page.IsSuccess)
        {
            return LookupRead.Of(LookupReadOutcome.InvalidCursor, info.Name, info.KnowledgeBaseName);
        }

        return new LookupRead(
            LookupReadOutcome.Ok,
            info.Name,
            info.KnowledgeBaseName,
            info.Unit,
            slice.ComputedAt,
            slice.Stale,
            slice.Truncated,
            slice.ValueCount,
            [.. page.Value.Values.Select(v => new LookupReadValue(v.Value, v.Label, v.Synonyms, v.Count, v.Attributes))],
            page.Value.Cursor,
            page.Value.CursorRestarted);
    }

    public async Task<LiveDocuments> DocumentsAsync(LiveDocumentsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return IsCurrentTenant(request.Scope.TenantId)
            ? await live.DocumentsAsync(request, cancellationToken)
            : new LiveDocuments(LiveOutcome.NotAvailable, NoKnowledgeBase);
    }

    public async Task<LiveCount> CountAsync(LiveCountRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return IsCurrentTenant(request.Scope.TenantId)
            ? await live.CountAsync(request, cancellationToken)
            : new LiveCount(LiveOutcome.NotAvailable, NoKnowledgeBase);
    }

    public async Task<LiveValues> LiveValuesAsync(LiveValuesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsCurrentTenant(request.Scope.TenantId)
            || (await LoadVisibleAsync(request.Scope.AssistantId, [request.LookupId], cancellationToken)).FirstOrDefault() is not { } found)
        {
            return new LiveValues(LiveOutcome.NotAvailable, "The list is not available.");
        }

        // A list typed by hand has no documents or entities behind it to filter.
        if (found.Lookup.Kind == LookupKind.Manual || found.KnowledgeBase is null)
        {
            return new LiveValues(
                LiveOutcome.NotComputed,
                $"The list '{found.Info.Name}' cannot be filtered: it is typed by hand. Read it without filters.");
        }

        return await live.ValuesAsync(found.Lookup, found.KnowledgeBase, found.Info.Name, request, cancellationToken);
    }

    public async Task<IReadOnlyList<UnindexedFilter>> UnindexedFiltersAsync(UnindexedFiltersQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return IsCurrentTenant(query.TenantId) ? await live.UnindexedAsync(query, cancellationToken) : [];
    }

    private const string NoKnowledgeBase = "None of the knowledge bases can be queried.";

    private bool IsCurrentTenant(Guid tenantId) => tenant.TenantId == tenantId && tenantId != Guid.Empty;

    /// <summary>The lookups the tenant can see: those of the knowledge bases it sees (for the assistant, when one answers) and the manual ones shared with it.</summary>
    private async Task<List<VisibleLookup>> LoadVisibleAsync(Guid? assistantId, IReadOnlyList<Guid>? lookupIds, CancellationToken cancellationToken)
    {
        var ids = lookupIds?.Distinct().ToList();
        var bases = assistantId is { } assistant ? KnowledgeBaseVisibility.ForAssistant(db, assistant) : KnowledgeBaseVisibility.KnowledgeBases(db);

        var ofBases = db.Lookups.IgnoreQueryFilters().AsNoTracking().Where(l => l.KnowledgeBaseId != null);
        var manual = LookupVisibility.Manual(db).AsNoTracking();
        if (ids is not null)
        {
            ofBases = ofBases.Where(l => ids.Contains(l.Id));
            manual = manual.Where(l => ids.Contains(l.Id));
        }

        var fromBases = await (
            from lookup in ofBases
            join knowledgeBase in bases.AsNoTracking() on lookup.KnowledgeBaseId equals knowledgeBase.Id
            select new { Lookup = lookup, KnowledgeBase = knowledgeBase })
            .ToListAsync(cancellationToken);
        var manuals = await manual.ToListAsync(cancellationToken);

        // The fields the lists are named after, and the attributes the entity lists show, which say what they can be ordered by.
        var fieldIds = fromBases
            .Select(f => f.Lookup.DictionaryFieldId)
            .OfType<Guid>()
            .Concat(fromBases.Where(f => f.Lookup.Kind == LookupKind.Entity).SelectMany(f => f.KnowledgeBase.Mapping.Fields.Where(m => m.EntityAttribute).Select(m => m.DictionaryFieldId)))
            .Distinct()
            .ToList();
        var dictionary = await DictionaryVisibility.Fields(db).AsNoTracking()
            .Where(f => fieldIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, cancellationToken);
        var fieldNames = dictionary.ToDictionary(pair => pair.Key, pair => pair.Value.Name);

        var visible = new List<VisibleLookup>();
        foreach (var entry in fromBases)
        {
            var (lookup, knowledgeBase) = (entry.Lookup, entry.KnowledgeBase);
            // A field the tenant cannot see makes its computed lookup invisible to the tenant.
            if (lookup.Kind == LookupKind.Computed && (lookup.DictionaryFieldId is not { } fieldId || !fieldNames.ContainsKey(fieldId)))
            {
                continue;
            }

            visible.Add(new VisibleLookup(
                lookup,
                knowledgeBase,
                new LookupInfo(
                    lookup.Id,
                    lookup.Name,
                    lookup.Kind == LookupKind.Computed ? LookupKindInfo.Computed : LookupKindInfo.Entity,
                    knowledgeBase.Id,
                    knowledgeBase.Name,
                    lookup.Description,
                    lookup.Unit?.ToString().ToLowerInvariant(),
                    lookup.Kind == LookupKind.Computed ? lookup.DictionaryFieldId : null,
                    lookup.Kind == LookupKind.Entity
                        ? EntityAttributes.Sortable(knowledgeBase.Mapping.Fields.Where(m => m.EntityAttribute).Select(m => dictionary.GetValueOrDefault(m.DictionaryFieldId)).OfType<DictionaryField>())
                        : null,
                    lookup.Storage.ToString(),
                    [.. lookup.Attributes.Select(a => new LookupAttributeInfo(a.Key, a.Label, a.Type.ToString()))])));
        }

        visible.AddRange(manuals.Select(lookup => new VisibleLookup(
            lookup,
            KnowledgeBase: null,
            new LookupInfo(lookup.Id, lookup.Name, LookupKindInfo.Manual, KnowledgeBaseId: null, KnowledgeBaseName: null, lookup.Description, Unit: null,
                Storage: LookupStorage.Snapshot.ToString(),
                Attributes: [.. lookup.Attributes.Select(a => new LookupAttributeInfo(a.Key, a.Label, a.Type.ToString()))]))));
        return visible;
    }

    /// <summary>The row filters of a read in the shape the admin reads take: <c>{ "&lt;key&gt;": { "&lt;operator&gt;": value } }</c>.</summary>
    private static string? RowFiltersJson(IReadOnlyList<LookupRowFilter>? filters)
    {
        if (filters is null || filters.Count == 0)
        {
            return null;
        }

        var conditions = new JsonObject();
        foreach (var filter in filters)
        {
            if (conditions[filter.Key] is not JsonObject operators)
            {
                operators = new JsonObject();
                conditions[filter.Key] = operators;
            }

            operators[filter.Operator] = JsonNode.Parse(filter.Value.GetRawText());
        }

        return conditions.ToJsonString();
    }

    /// <summary>An entity lookup in live storage is read from its store, with the scan of the lookups capability (spec 012 D-4, D-5).</summary>
    private async Task<LookupRead> LiveReadAsync(LookupReadRequest request, Lookup lookup, LookupInfo info, KnowledgeBase knowledgeBase, CancellationToken cancellationToken)
    {
        var scope = new LiveScope(
            request.TenantId,
            request.AssistantId ?? Guid.Empty,
            request.Filters.Filters,
            new Dictionary<string, JsonElement>(),
            request.Filters.Aliases,
            []);
        var values = await live.ValuesAsync(
            lookup,
            knowledgeBase,
            info.Name,
            new LiveValuesRequest(scope, lookup.Id, request.Search, request.Sort, request.Limit, request.Attribute),
            cancellationToken);

        return values.Outcome switch
        {
            LiveOutcome.Ok => new LookupRead(
                LookupReadOutcome.Ok,
                info.Name,
                info.KnowledgeBaseName,
                Truncated: values.Truncated,
                ValueCount: values.ValueCount,
                Values: values.Values,
                Live: true),
            LiveOutcome.NotAvailable => LookupRead.Of(LookupReadOutcome.NotAvailable, info.Name, info.KnowledgeBaseName),
            _ => LookupRead.Of(LookupReadOutcome.Failed, info.Name, info.KnowledgeBaseName),
        };
    }

    private static bool IsVisibleTo(VisibleLookup entry, Guid ownerTenantId, BindingScope scope, List<DictionaryField> fields)
    {
        var lookup = entry.Lookup;
        if (lookup.Kind == LookupKind.Manual)
        {
            var tenants = scope.Kind == BindingScopeKind.Tenant ? scope.TenantIds.Append(ownerTenantId).Distinct() : [ownerTenantId];
            return scope.Kind switch
            {
                BindingScopeKind.Global => lookup.Scope == LookupScope.Global,
                _ => tenants.All(tenantId =>
                    lookup.TenantId == tenantId
                    || lookup.Scope == LookupScope.Global
                    || (lookup.Scope == LookupScope.Tenant && lookup.ScopeTenantIds.Contains(tenantId))),
            };
        }

        var field = lookup.Kind == LookupKind.Computed ? fields.FirstOrDefault(f => f.Id == lookup.DictionaryFieldId) : null;
        return ScopeVisibility.KnowledgeBaseIsVisible(entry.KnowledgeBase!, ownerTenantId, scope)
            && (lookup.Kind != LookupKind.Computed || (field is not null && ScopeVisibility.FieldIsVisible(field, ownerTenantId, scope)));
    }

    private static bool MapsEveryField(KnowledgeBase knowledgeBase, IReadOnlyList<FieldFilter> parsed)
    {
        var mapped = knowledgeBase.Mapping.Fields.Select(f => f.DictionaryFieldId).ToHashSet();
        return parsed.All(filter => mapped.Contains(filter.Field.Id));
    }

    /// <summary>The knowledge base that would hold more slices than the limit once the change is applied, or <see langword="null"/>.</summary>
    private async Task<string?> TooManySlicesAsync(List<VisibleLookup> visible, List<LookupSlice> touched, List<LookupSlice> orphans, CancellationToken cancellationToken)
    {
        foreach (var group in visible.Where(v => v.KnowledgeBase is not null).GroupBy(v => v.KnowledgeBase!.Id))
        {
            var lookupIds = await db.Lookups.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.KnowledgeBaseId == group.Key)
                .Select(l => l.Id)
                .ToListAsync(cancellationToken);
            var existing = await db.LookupSlices.IgnoreQueryFilters().AsNoTracking()
                .Where(s => lookupIds.Contains(s.LookupId))
                .Select(s => s.Id)
                .ToListAsync(cancellationToken);

            var gone = orphans.Select(s => s.Id).ToHashSet();
            var slices = existing.Where(id => !gone.Contains(id))
                .Concat(touched.Where(s => lookupIds.Contains(s.LookupId) && !gone.Contains(s.Id)).Select(s => s.Id))
                .Distinct()
                .Count();
            if (slices > MaxSlicesPerKnowledgeBase)
            {
                return group.First().KnowledgeBase!.Name;
            }
        }

        return null;
    }

    private async Task<ParsedKey> ParseAsync(LookupSliceFilters filters, CancellationToken cancellationToken)
    {
        if (filters.Filters.Count == 0)
        {
            return ParsedKey.Whole;
        }

        var ids = filters.FieldIds.Concat(filters.Aliases.Values).Distinct().ToList();
        var fields = await DictionaryVisibility.Fields(db).AsNoTracking().Where(f => ids.Contains(f.Id)).ToListAsync(cancellationToken);
        var names = FixedFilterNames.Build(
            filters.Aliases.Where(pair => fields.Any(f => f.Id == pair.Value)).ToDictionary(pair => pair.Key, pair => fields.First(f => f.Id == pair.Value), StringComparer.Ordinal),
            fields.Where(f => filters.FieldIds.Contains(f.Id)));
        if (names.AmbiguityProblem(filters.Filters.Keys) is { } ambiguous)
        {
            return ParsedKey.Invalid(ambiguous);
        }

        var parsed = SearchFilterParser.Parse(filters.Filters, names.Fields, KnowledgeBaseSearchErrors.InvalidFixedFilter, ignoreEmpty: false);
        return parsed.IsSuccess
            ? new ParsedKey(LookupSliceKey.From(parsed.Value), parsed.Value, null)
            : ParsedKey.Invalid(parsed.Error.Description);
    }

    private sealed record VisibleLookup(Lookup Lookup, KnowledgeBase? KnowledgeBase, LookupInfo Info);

    private sealed record ParsedKey(LookupSliceKey? Value, IReadOnlyList<FieldFilter> Filters, string? Problem)
    {
        public static ParsedKey Whole { get; } = new(LookupSliceKey.WholeKnowledgeBase, [], null);

        public static ParsedKey Invalid(string problem) => new(null, [], problem);
    }
}
