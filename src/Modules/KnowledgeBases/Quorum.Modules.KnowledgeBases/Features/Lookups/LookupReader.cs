using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
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
}

/// <param name="Search">A case-insensitive substring of the value, its label or one of its synonyms.</param>
/// <param name="Cursor">What the previous page returned.</param>
internal sealed record LookupPageRequest(string? Search, LookupSort Sort, int Limit, string? Cursor);

internal sealed record LookupPageValue(string Value, string? Label, IReadOnlyList<string> Synonyms, int? Count, string? Attributes);

/// <param name="Cursor"><see langword="null"/> on the last page.</param>
/// <param name="CursorRestarted">The cursor belonged to a generation that is no longer current, so the list starts again from the top.</param>
internal sealed record LookupPage(IReadOnlyList<LookupPageValue> Values, string? Cursor, bool CursorRestarted);

/// <summary>
/// Reads a page of the current generation of one slice from the tables (spec 007 D-12). It is the one reader of every kind of lookup.
/// The caller has already decided that the tenant may see the slice's lookup, so the tenant filter is off here: a manual lookup
/// another tenant shares is read through the owner's rows.
/// </summary>
internal static class LookupReader
{
    public const int DefaultLimit = 50;

    public const int MaxLimit = 200;

    private const string EscapeCharacter = "\\";

    private static readonly Error InvalidCursor = Error.Validation("lookup.invalid_cursor", "The cursor was not returned by this lookup in this order.");

    public static async Task<Result<LookupPage>> ReadAsync(KnowledgeBasesDbContext db, LookupSlice slice, LookupPageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(slice);
        ArgumentNullException.ThrowIfNull(request);

        var limit = Math.Clamp(request.Limit, 1, MaxLimit);
        var cursor = Cursor.Parse(request.Cursor);
        var unreadable = cursor is null && !string.IsNullOrEmpty(request.Cursor);
        if (unreadable || (cursor is not null && cursor.Sort != request.Sort))
        {
            return InvalidCursor;
        }

        // No generation yet: nothing to read, and a cursor cannot have come from here.
        if (slice.CurrentGeneration == 0)
        {
            return new LookupPage([], null, cursor is not null);
        }

        var restarted = cursor is not null && cursor.Generation != slice.CurrentGeneration;
        if (restarted)
        {
            cursor = null;
        }

        var generation = slice.CurrentGeneration;
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
        var next = rows.Count > limit ? Cursor.Encode(generation, request.Sort, page[^1]) : null;

        return new LookupPage(
            [.. page.Select(v => new LookupPageValue(v.Value, v.Label, v.Synonyms, v.DocumentCount, v.Attributes))],
            next,
            restarted);
    }

    [SuppressMessage("Globalization", "CA1309:Use ordinal string comparison", Justification = "Translated to SQL: the comparison uses the collation of the column, as the ORDER BY next to it does.")]
    private static IQueryable<LookupValue> Order(IQueryable<LookupValue> values, LookupSort sort, Cursor? after)
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

    /// <summary>Opaque to the client: the generation it was issued for, the order and the last row it reached.</summary>
    private sealed record Cursor(
        [property: JsonPropertyName("g")] int Generation,
        [property: JsonPropertyName("s")] LookupSort Sort,
        [property: JsonPropertyName("p")] int? Position,
        [property: JsonPropertyName("v")] string? Value)
    {
        private static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

        public static string Encode(int generation, LookupSort sort, LookupValue last) =>
            Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(
                new Cursor(generation, sort, last.Position, sort == LookupSort.Name ? last.Value : null),
                Options));

        /// <summary>The cursor in <paramref name="text"/>, or <see langword="null"/> when there is none or it cannot be one of ours.</summary>
        public static Cursor? Parse(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            try
            {
                var cursor = JsonSerializer.Deserialize<Cursor>(Base64Url.DecodeFromChars(text), Options);
                return cursor is { Generation: > 0, Position: not null } && (cursor.Sort == LookupSort.Count || cursor.Value is not null) ? cursor : null;
            }
            catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException)
            {
                return null;
            }
        }
    }
}
