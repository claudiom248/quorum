using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBases.Features.Lookups;

/// <summary>Opaque to the client: the generation it was issued for, the order and the last row it reached.</summary>
internal sealed record LookupCursor(
    [property: JsonPropertyName("g")] int Generation,
    [property: JsonPropertyName("s")] LookupSort Sort,
    [property: JsonPropertyName("p")] int? Position,
    [property: JsonPropertyName("v")] string? Value)
{
    private static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private static readonly Error Invalid = Error.Validation("lookup.invalid_cursor", "The cursor was not returned by this lookup in this order.");

    /// <param name="value">The value of the last row for a page by name; the key of the attribute order for a page by attribute.</param>
    public static string Encode(int generation, LookupSort sort, int position, string value) =>
        Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(
            new LookupCursor(generation, sort, position, sort == LookupSort.Count ? null : value),
            Options));

    /// <summary>
    /// What a request's cursor means for the current generation of a slice: where to continue, or that it must start again because the
    /// cursor belongs to a generation that is gone. The same rule for the tables and for the cache, so a client can move between them.
    /// </summary>

    public static Result<ResolvedCursor> Resolve(LookupPageRequest request, LookupSlice slice)
    {
        var cursor = Parse(request.Cursor);
        var unreadable = cursor is null && !string.IsNullOrEmpty(request.Cursor);
        if (unreadable
            || (cursor is not null && cursor.Sort != request.Sort)
            || (cursor is { Sort: LookupSort.Attribute } && cursor.Value != request.Attribute?.Key))
        {
            return Invalid;
        }

        // No generation yet: nothing to read, and a cursor cannot have come from here.
        if (slice.CurrentGeneration == 0)
        {
            return new ResolvedCursor(null, cursor is not null);
        }

        return cursor is not null && cursor.Generation != slice.CurrentGeneration
            ? new ResolvedCursor(null, true)
            : new ResolvedCursor(cursor, false);
    }

    /// <summary>The cursor in <paramref name="text"/>, or <see langword="null"/> when there is none or it cannot be one of ours.</summary>
    private static LookupCursor? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        try
        {
            var cursor = JsonSerializer.Deserialize<LookupCursor>(Base64Url.DecodeFromChars(text), Options);
            return cursor is { Generation: > 0, Position: not null } && (cursor.Sort == LookupSort.Count || cursor.Value is not null) ? cursor : null;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException)
        {
            return null;
        }
    }
}

/// <param name="After">Where to continue, or <see langword="null"/> to start at the top.</param>
/// <param name="Restarted">The cursor belonged to a generation that is no longer current.</param>
internal sealed record ResolvedCursor(LookupCursor? After, bool Restarted);
