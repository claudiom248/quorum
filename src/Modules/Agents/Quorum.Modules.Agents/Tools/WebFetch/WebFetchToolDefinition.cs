using System.Text;
using System.Text.Json;

namespace Quorum.Modules.Agents.Tools.WebFetch;

/// <summary>What the model is told about a web fetch tool, generated from its binding (spec 006).</summary>
internal sealed record GeneratedWebFetchDefinition(string Description, string InputSchema);

internal static class WebFetchToolDefinition
{
    public const string UrlsParameter = "urls";

    /// <summary>How long one URL as the model writes it may be, an upper bound that keeps the schema honest without chasing a real-world limit.</summary>
    public const int MaxUrlCharacters = 2_048;

    /// <param name="description">The administrator's text for the model.</param>
    /// <param name="maxUrls">How many pages one call may fetch, so the model knows the limit before it asks.</param>
    public static GeneratedWebFetchDefinition Build(string description, int maxUrls)
    {
        ArgumentNullException.ThrowIfNull(description);

        return new GeneratedWebFetchDefinition(Describe(description, maxUrls), Schema(maxUrls));
    }

    private static string Describe(string description, int maxUrls) =>
        $"{description.Trim()}\n\nUp to {maxUrls} pages per call, each with its title and its main text.";

    private static string Schema(int maxUrls)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteStartObject("properties");

            writer.WriteStartObject(UrlsParameter);
            writer.WriteString("type", "array");
            writer.WriteString("description", $"The pages to read, at most {maxUrls}.");
            writer.WriteNumber("minItems", 1);
            writer.WriteNumber("maxItems", maxUrls);
            writer.WriteStartObject("items");
            writer.WriteString("type", "string");
            writer.WriteString("format", "uri");
            writer.WriteNumber("maxLength", MaxUrlCharacters);
            writer.WriteEndObject();
            writer.WriteEndObject();

            writer.WriteEndObject();
            writer.WriteStartArray("required");
            writer.WriteStringValue(UrlsParameter);
            writer.WriteEndArray();
            writer.WriteBoolean("additionalProperties", false);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
