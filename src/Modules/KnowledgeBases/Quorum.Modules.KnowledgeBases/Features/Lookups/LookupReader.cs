using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBases.Features.Lookups;

internal enum LookupSort
{
    /// <summary>The rank of the generation: by count, then value.</summary>
    Count,

    Name,

    /// <summary>An entity lookup by one of its attributes (spec 007 D-12); <see cref="LookupPageRequest.Attribute"/> says which.</summary>
    Attribute,
}

/// <param name="Name">The key of the attribute in the stored values.</param>
/// <param name="Descending">Largest or latest first. A value without the attribute comes last either way.</param>
internal sealed record LookupAttributeSort(string Name, bool Descending)
{
    /// <summary>What a cursor remembers of the order, so a cursor of one order is refused in another.</summary>
    public string Key => Descending ? "-" + Name : Name;
}

/// <param name="Search">A case-insensitive substring of the value, its label or one of its synonyms.</param>
/// <param name="Cursor">What the previous page returned.</param>
/// <param name="Attribute">With <see cref="LookupSort.Attribute"/>: the attribute to order by.</param>
internal sealed record LookupPageRequest(string? Search, LookupSort Sort, int Limit, string? Cursor, LookupAttributeSort? Attribute = null);

internal sealed record LookupPageValue(string Value, string? Label, IReadOnlyList<string> Synonyms, int? Count, string? Attributes);

/// <param name="Cursor"><see langword="null"/> on the last page.</param>
/// <param name="CursorRestarted">The cursor belonged to a generation that is no longer current, so the list starts again from the top.</param>
internal sealed record LookupPage(IReadOnlyList<LookupPageValue> Values, string? Cursor, bool CursorRestarted);

/// <summary>
/// Reads a page of the current generation of one slice from the tables (spec 007 D-12). It is the one reader of every kind of lookup
/// when the cache cannot answer, and the source the cache is filled from.
/// The caller has already decided that the tenant may see the slice's lookup, so the tenant filter is off here: a manual lookup
/// another tenant shares is read through the owner's rows.
/// </summary>
internal static class LookupReader
{
    public const int DefaultLimit = 50;

    public const int MaxLimit = 200;

    private const string EscapeCharacter = "\\";

    public static async Task<Result<LookupPage>> ReadAsync(KnowledgeBasesDbContext db, LookupSlice slice, LookupPageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(slice);
        ArgumentNullException.ThrowIfNull(request);

        var limit = Math.Clamp(request.Limit, 1, MaxLimit);
        var resolved = LookupCursor.Resolve(request, slice);
        if (!resolved.IsSuccess)
        {
            return resolved.Error;
        }

        var (cursor, restarted) = (resolved.Value.After, resolved.Value.Restarted);
        if (slice.CurrentGeneration == 0)
        {
            return new LookupPage([], null, restarted);
        }

        var generation = slice.CurrentGeneration;
        if (request.Sort == LookupSort.Attribute)
        {
            // The order is by a value inside the JSON of each row, so the generation (at most the ceiling of values) is ordered in memory, as the cache does.
            return LookupPager.Page(await LookupCache.LoadAsync(db, slice.Id, generation, cancellationToken), slice, request);
        }

        var values = db.LookupValues.IgnoreQueryFilters().AsNoTracking().Where(v => v.SliceId == slice.Id && v.Generation == generation);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var pattern = $"%{Escape(request.Search.Trim())}%";
            values = values.Where(v =>
                EF.Functions.ILike(v.Value, pattern, EscapeCharacter)
                || (v.Label != null && EF.Functions.ILike(v.Label, pattern, EscapeCharacter))
                || v.Synonyms.Any(synonym => EF.Functions.ILike(synonym, pattern, EscapeCharacter)));
        }

        // One more than the page tells whether there is a next one without counting.
        var rows = await Order(values, request.Sort, cursor).Take(limit + 1).ToListAsync(cancellationToken);
        var page = rows.Take(limit).ToList();
        var next = rows.Count > limit ? LookupCursor.Encode(generation, request.Sort, page[^1].Position, page[^1].Value) : null;

        return new LookupPage(
            [.. page.Select(v => new LookupPageValue(v.Value, v.Label, v.Synonyms, v.DocumentCount, v.Attributes))],
            next,
            restarted);
    }

    [SuppressMessage("Globalization", "CA1309:Use ordinal string comparison", Justification = "Translated to SQL: the comparison uses the collation of the column, as the ORDER BY next to it does.")]
    private static IQueryable<LookupValue> Order(IQueryable<LookupValue> values, LookupSort sort, LookupCursor? after)
    {
        if (sort == LookupSort.Count)
        {
            if (after is { Position: { } position })
            {
                values = values.Where(v => v.Position > position);
            }

            return values.OrderBy(v => v.Position);
        }

        // Value alone is not a key (entities may share a name), so the position breaks ties; both sides use the database's own order.
        if (after is { Value: { } last, Position: { } lastPosition })
        {
            values = values.Where(v => string.Compare(v.Value, last) > 0 || (v.Value == last && v.Position > lastPosition));
        }

        return values.OrderBy(v => v.Value).ThenBy(v => v.Position);
    }

    private static string Escape(string text) => text
        .Replace(EscapeCharacter, EscapeCharacter + EscapeCharacter, StringComparison.Ordinal)
        .Replace("%", EscapeCharacter + "%", StringComparison.Ordinal)
        .Replace("_", EscapeCharacter + "_", StringComparison.Ordinal);
}
