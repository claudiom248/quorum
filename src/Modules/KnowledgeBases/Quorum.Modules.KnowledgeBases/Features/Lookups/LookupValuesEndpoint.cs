using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.KnowledgeBases.Contracts;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBases.Features.Lookups;

/// <summary>The values of any lookup, as the administrator sees them: the same page the lookup tool will read.</summary>
internal sealed class LookupValuesEndpoint : IEndpointGroup
{
    private static readonly Error InvalidSort = Error.Validation(
        "lookup.invalid_sort",
        "The sort must be 'count' or 'name'; for an entity lookup it can also be the name of an attribute of type number or date, with '-' in front for the largest first.");

    public void Map(IEndpointRouteBuilder app)
    {
        var lookups = app.MapGroup("/api/knowledge-bases/lookups")
            .WithTags("Knowledge bases")
            .RequireAuthorization(QuorumPolicies.KnowledgeBaseManage);

        lookups.MapGet("/{lookupId:guid}/values", ReadAsync).WithName("ReadLookupValues");
    }

    /// <param name="slice">The hash of the filters of the slice; the whole knowledge base when it is left out.</param>
    private static async Task<Results<Ok<LookupValuesDto>, NotFound, ProblemHttpResult>> ReadAsync(
        Guid lookupId,
        KnowledgeBasesDbContext db,
        LookupCache cache,
        LiveLookupOperations live,
        string? slice,
        string? search,
        string? sort,
        int? limit,
        string? cursor,
        string? filters,
        CancellationToken cancellationToken)
    {
        var lookup = await LookupVisibility.Readable(db).AsNoTracking().SingleOrDefaultAsync(l => l.Id == lookupId, cancellationToken);
        if (lookup is null)
        {
            return TypedResults.NotFound();
        }

        var parsedFilters = LookupFilters.Parse(filters, lookup.Attributes);
        if (!parsedFilters.IsSuccess)
        {
            return parsedFilters.Error.ToProblem();
        }

        LookupAttributeSort? attribute = null;
        if (!TryParseSort(sort, out var order))
        {
            attribute = await AttributeSortAsync(db, lookup, sort!, cancellationToken);
            if (attribute is null)
            {
                return InvalidSort.ToProblem();
            }

            order = LookupSort.Attribute;
        }

        if (lookup.Storage == LookupStorage.Live && lookup.KnowledgeBaseId is { } knowledgeBaseId)
        {
            return await LiveValuesAsync(db, live, lookup, knowledgeBaseId, search, order, attribute, limit, parsedFilters.Value, cancellationToken);
        }

        var hash = slice ?? LookupSliceKey.WholeKnowledgeBase.Hash;
        var found = await db.LookupSlices.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(s => s.LookupId == lookup.Id && s.FilterHash == hash, cancellationToken);
        if (found is null)
        {
            return TypedResults.NotFound();
        }

        var page = await cache.ReadAsync(db, lookup, found, new LookupPageRequest(search, order, limit ?? LookupReader.DefaultLimit, cursor, attribute, parsedFilters.Value), cancellationToken);
        return page.IsSuccess
            ? TypedResults.Ok(LookupValuesDto.From(lookup, found, page.Value))
            : page.Error.ToProblem();
    }

    /// <summary>An entity lookup in live storage is read from its store (spec 012 D-4). Filters on its attributes are not served from the store yet.</summary>
    private static async Task<Results<Ok<LookupValuesDto>, NotFound, ProblemHttpResult>> LiveValuesAsync(
        KnowledgeBasesDbContext db,
        LiveLookupOperations live,
        Lookup lookup,
        Guid knowledgeBaseId,
        string? search,
        LookupSort order,
        LookupAttributeSort? attribute,
        int? limit,
        List<LookupFilter> filters,
        CancellationToken cancellationToken)
    {
        if (filters.Count > 0)
        {
            return Error.Validation("lookup.invalid_filter", "The filters on attributes are not served from the store yet: read the lookup in Snapshot storage.").ToProblem();
        }

        var knowledgeBase = await db.KnowledgeBases.AsNoTracking().SingleOrDefaultAsync(k => k.Id == knowledgeBaseId, cancellationToken);
        if (knowledgeBase is null)
        {
            return TypedResults.NotFound();
        }

        var scope = new LiveScope(db.CurrentTenantId ?? Guid.Empty, Guid.Empty, new Dictionary<string, JsonElement>(), new Dictionary<string, JsonElement>(), new Dictionary<string, Guid>(), []);
        var sort = order switch
        {
            LookupSort.Name => LookupSortOrder.Name,
            LookupSort.Attribute => LookupSortOrder.Attribute,
            _ => LookupSortOrder.Count,
        };
        var values = await live.ValuesAsync(
            lookup,
            knowledgeBase,
            lookup.Name,
            new LiveValuesRequest(scope, lookup.Id, search, sort, limit ?? LookupReader.DefaultLimit, attribute is null ? null : new LookupAttributeOrder(attribute.Name, attribute.Descending)),
            cancellationToken);

        if (values.Outcome != LiveOutcome.Ok)
        {
            return Error.Validation("lookup.live_unavailable", values.Message ?? "The store cannot answer now: try again, or read the lookup in Snapshot storage.").ToProblem();
        }

        var rows = (values.Values ?? []).Select(v => new LookupValueDto(
            v.Value,
            v.Label,
            v.Synonyms.Count == 0 ? null : v.Synonyms,
            v.Count,
            v.Attributes is null ? null : JsonSerializer.Deserialize<JsonElement>(v.Attributes)));
        return TypedResults.Ok(new LookupValuesDto(null, null, false, values.Truncated, [.. rows], null, false, values.ValueCount));
    }

    private static bool TryParseSort(string? sort, out LookupSort order)
    {
        order = LookupSort.Count;
        return string.IsNullOrEmpty(sort) || (DictionaryFieldRules.TryParseMember(sort, out order) && order != LookupSort.Attribute);
    }

    /// <summary>
    /// The attribute an entity lookup can be ordered by: one of the attributes its knowledge base marks, of type number or date, since
    /// a text has no order a model could rely on. A leading '-' asks for the largest first.
    /// </summary>
    private static async Task<LookupAttributeSort?> AttributeSortAsync(KnowledgeBasesDbContext db, Lookup lookup, string sort, CancellationToken cancellationToken)
    {
        var descending = sort.StartsWith('-');
        var name = descending ? sort[1..] : sort;
        if (lookup.Kind != LookupKind.Entity || await db.KnowledgeBases.AsNoTracking().SingleOrDefaultAsync(k => k.Id == lookup.KnowledgeBaseId, cancellationToken) is not { } knowledgeBase)
        {
            return null;
        }

        var fieldIds = knowledgeBase.Mapping.Fields.Where(f => f.EntityAttribute).Select(f => f.DictionaryFieldId).ToList();
        var fields = await db.DictionaryFields.IgnoreQueryFilters().AsNoTracking().Where(f => fieldIds.Contains(f.Id)).ToListAsync(cancellationToken);
        return EntityAttributes.Sortable(fields).Contains(name, StringComparer.Ordinal)
            ? new LookupAttributeSort(name, descending)
            : null;
    }
}

/// <param name="Unit">What the counts count, as <c>documents</c> or <c>chunks</c>; <see langword="null"/> for a lookup with no counts.</param>
/// <param name="AsOf">When the list was computed; <see langword="null"/> before the first computation.</param>
/// <param name="Matching">How many rows match the search and the filters, when the read counted them.</param>
internal sealed record LookupValuesDto(string? Unit, DateTimeOffset? AsOf, bool Stale, bool Truncated, List<LookupValueDto> Values, string? Cursor, bool CursorRestarted, int? Matching)
{
    public static LookupValuesDto From(Lookup lookup, LookupSlice slice, LookupPage page) => new(
        lookup.Unit?.ToString().ToLowerInvariant(),
        slice.ComputedAt,
        slice.Stale,
        slice.Truncated,
        [.. page.Values.Select(LookupValueDto.From)],
        page.Cursor,
        page.CursorRestarted,
        page.Matching);
}

internal sealed record LookupValueDto(string Value, string? Label, IReadOnlyList<string>? Synonyms, int? Count, JsonElement? Attributes)
{
    public static LookupValueDto From(LookupPageValue value) => new(
        value.Value,
        value.Label,
        value.Synonyms.Count == 0 ? null : value.Synonyms,
        value.Count,
        value.Attributes is null ? null : JsonSerializer.Deserialize<JsonElement>(value.Attributes));
}
