using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
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
        string? slice,
        string? search,
        string? sort,
        int? limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var lookup = await LookupVisibility.Readable(db).AsNoTracking().SingleOrDefaultAsync(l => l.Id == lookupId, cancellationToken);
        if (lookup is null)
        {
            return TypedResults.NotFound();
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

        var hash = slice ?? LookupSliceKey.WholeKnowledgeBase.Hash;
        var found = await db.LookupSlices.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(s => s.LookupId == lookup.Id && s.FilterHash == hash, cancellationToken);
        if (found is null)
        {
            return TypedResults.NotFound();
        }

        var page = await cache.ReadAsync(db, lookup, found, new LookupPageRequest(search, order, limit ?? LookupReader.DefaultLimit, cursor, attribute), cancellationToken);
        return page.IsSuccess
            ? TypedResults.Ok(LookupValuesDto.From(lookup, found, page.Value))
            : page.Error.ToProblem();
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
        var keys = EntityAttributes.Names(fields);
        return fields.Any(f => keys[f.Id] == name && f.Type is DictionaryFieldType.Number or DictionaryFieldType.Date)
            ? new LookupAttributeSort(name, descending)
            : null;
    }
}

/// <param name="Unit">What the counts count, as <c>documents</c> or <c>chunks</c>; <see langword="null"/> for a lookup with no counts.</param>
/// <param name="AsOf">When the list was computed; <see langword="null"/> before the first computation.</param>
internal sealed record LookupValuesDto(string? Unit, DateTimeOffset? AsOf, bool Stale, bool Truncated, List<LookupValueDto> Values, string? Cursor, bool CursorRestarted)
{
    public static LookupValuesDto From(Lookup lookup, LookupSlice slice, LookupPage page) => new(
        lookup.Unit?.ToString().ToLowerInvariant(),
        slice.ComputedAt,
        slice.Stale,
        slice.Truncated,
        [.. page.Values.Select(LookupValueDto.From)],
        page.Cursor,
        page.CursorRestarted);
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
