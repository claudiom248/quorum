using System.Text;
using System.Text.Json;

namespace Quorum.Modules.Agents.Tools.WebSearch;

/// <summary>What the model is told about a web search tool, generated from its binding (spec 005).</summary>
internal sealed record GeneratedWebSearchDefinition(string Description, string InputSchema);

internal static class WebSearchToolDefinition
{
    public const string QueryParameter = "query";

    /// <summary>The longest query SearXNG answers: what is longer is cut, silently, as the spec decided.</summary>
    public const int MaxQueryCharacters = 1_000;

    /// <param name="description">The administrator's text for the model.</param>
    /// <param name="topN">How many results a search returns, so the model knows what it gets before it asks.</param>
    public static GeneratedWebSearchDefinition Build(string description, int topN)
    {
        ArgumentNullException.ThrowIfNull(description);

        return new GeneratedWebSearchDefinition(Describe(description, topN), Schema());
    }

    private static string Describe(string description, int topN) =>
        $"{description.Trim()}\n\nUp to {topN} results, each with its title, address and a short extract.";

    private static string Schema()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteStartObject("properties");

            writer.WriteStartObject(QueryParameter);
            writer.WriteString("type", "string");
            writer.WriteString("description", $"What to look for, as a short question or a few key words, at most {MaxQueryCharacters} characters.");
            writer.WriteEndObject();

            writer.WriteEndObject();
            writer.WriteStartArray("required");
            writer.WriteStringValue(QueryParameter);
            writer.WriteEndArray();
            writer.WriteBoolean("additionalProperties", false);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
