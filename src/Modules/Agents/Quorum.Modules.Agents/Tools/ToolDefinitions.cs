using System.Text.Json;

namespace Quorum.Modules.Agents.Tools;

/// <summary>A tool as it is sent to the model: the name the model calls it by, its description and its argument schema.</summary>
internal sealed record ToolDefinition(string Name, string Description, string InputSchema);

internal static class ToolNaming
{
    public const string NativePrefix = "native__";

    public static string Native(string toolName) => NativePrefix + toolName;
}

/// <summary>
/// Renders tool definitions in the OpenAI <c>tools</c> format, in a stable order. Its length is what the definitions cost
/// in the context budget, so the same text must be what the request carries.
/// </summary>
internal static class ToolDefinitionRenderer
{
    public static string Render(IEnumerable<ToolDefinition> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var tool in tools.OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "function");
                writer.WriteStartObject("function");
                writer.WriteString("name", tool.Name);
                writer.WriteString("description", tool.Description);
                writer.WritePropertyName("parameters");
                using (var schema = JsonDocument.Parse(tool.InputSchema))
                {
                    schema.RootElement.WriteTo(writer);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Characters the definitions take in the request; zero without tools, because then no <c>tools</c> are sent.</summary>
    public static int Length(IReadOnlyCollection<ToolDefinition> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return tools.Count == 0 ? 0 : Render(tools).Length;
    }
}
