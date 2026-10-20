using System.Globalization;
using System.Text.Json.Nodes;
using Quorum.Modules.KnowledgeBases.Contracts;
using Quorum.Modules.KnowledgeBases.Mapping;

namespace Quorum.Modules.KnowledgeBases.Features.Search;

/// <summary>
/// Reads a point's payload through the roles a knowledge base mapped (spec 003 D-9). The payload belongs to the data owner, so
/// nothing in it is trusted to have the shape the mapping promises.
/// </summary>
internal static class ChunkReader
{
    // What reaches the model and is stored with a message: a payload cannot make a source reference arbitrarily large.
    private const int MaxReferenceLength = 300;

    private const int MaxUrlLength = 2000;

    /// <summary>The payload paths a search has to ask the store for.</summary>
    public static IReadOnlyList<string> PathsOf(MappingRoles roles) =>
        [.. new[] { roles.Text, roles.DocumentId, roles.Title, roles.Position, roles.SourceUrl }.Where(path => path is not null).Select(path => path!).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// The chunk, or <see langword="null"/> when the payload has no usable text: a chunk without text is of no use to the model.
    /// A reference that is missing or of the wrong type only leaves the source less precise.
    /// </summary>
    public static RetrievedChunk? Read(string knowledgeBase, MappingRoles roles, double score, JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(payload);

        if (roles.Text is null || Find(payload, roles.Text) is not JsonValue textNode
            || !textNode.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return new RetrievedChunk(
            knowledgeBase,
            score,
            text,
            Reference(payload, roles.DocumentId, MaxReferenceLength),
            Reference(payload, roles.Title, MaxReferenceLength),
            Reference(payload, roles.Position, MaxReferenceLength),
            Reference(payload, roles.SourceUrl, MaxUrlLength, truncate: false));
    }

    private static string? Reference(JsonObject payload, string? path, int maxLength, bool truncate = true)
    {
        if (path is null || Find(payload, path) is not JsonValue node)
        {
            return null;
        }

        string? value;
        if (node.TryGetValue<string>(out var text))
        {
            value = text;
        }
        else if (node.TryGetValue<long>(out var integer))
        {
            value = integer.ToString(CultureInfo.InvariantCulture);
        }
        else if (node.TryGetValue<double>(out var number))
        {
            value = number.ToString("R", CultureInfo.InvariantCulture);
        }
        else
        {
            return null;
        }

        value = value.Trim();
        if (value.Length == 0)
        {
            return null;
        }

        // A link cut short would point somewhere else, so a too long one is dropped instead.
        return value.Length <= maxLength ? value : truncate ? value[..maxLength] : null;
    }

    /// <summary>The value at a payload path in dot notation (<c>meta.year</c>).</summary>
    private static JsonNode? Find(JsonObject payload, string path)
    {
        JsonNode? current = payload;
        foreach (var segment in path.Split('.'))
        {
            if (current is not JsonObject node || !node.TryGetPropertyValue(segment, out current))
            {
                return null;
            }
        }

        return current;
    }
}
