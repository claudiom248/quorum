using System.Text.Json;

namespace Quorum.Modules.KnowledgeBases.Domain;

/// <summary>
/// The condition that picks the one point of each document a lookup counts (spec 007 D-2): a boolean field that is <c>true</c>
/// (<c>is_first_chunk</c>) or a field equal to a scalar (<c>chunk_index = 0</c>). The data owner guarantees one such point per document.
/// </summary>
/// <param name="Value">The scalar as JSON text (<c>true</c>, <c>0</c>, <c>"first"</c>), so it keeps its type in the stored mapping.</param>
internal sealed record CountedPoint(string Path, string Value)
{
    /// <summary>The scalar of a counted point: <c>true</c>, a number or a non-empty text. <c>false</c> would match nearly every chunk and count nothing useful.</summary>
    public static CountedPoint? TryCreate(string path, JsonElement value)
    {
        var literal = value.ValueKind switch
        {
            JsonValueKind.True => "true",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String when !string.IsNullOrEmpty(value.GetString()) && value.GetString()!.Length <= LookupValue.MaxValueLength => value.GetRawText(),
            _ => null,
        };

        return literal is null ? null : new CountedPoint(path, literal);
    }

    public JsonElement ToJsonElement() => JsonSerializer.Deserialize<JsonElement>(Value);
}
