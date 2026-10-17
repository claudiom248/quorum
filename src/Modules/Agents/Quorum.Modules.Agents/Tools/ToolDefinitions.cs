using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Quorum.Modules.Agents.Tools;

/// <summary>A tool as it is sent to the model: the name the model calls it by, its description and its argument schema.</summary>
internal sealed record ToolDefinition(string Name, string Description, string InputSchema)
{
    /// <summary>The definition of a tool the model is offered: the one source of what its request carries and what the budget counts.</summary>
    public static ToolDefinition From(AIFunctionDeclaration tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return new ToolDefinition(tool.Name, tool.Description, tool.JsonSchema.GetRawText());
    }
}

internal static class ToolNaming
{
    public const string BuiltinPrefix = "builtin__";

    /// <summary>The longest name the model APIs accept (D-16).</summary>
    public const int MaxLength = 64;

    private const int HashLength = 8;

    public static string Builtin(string toolName) => BuiltinPrefix + toolName;

    /// <summary>
    /// <c>&lt;server&gt;__&lt;tool&gt;</c>. A tool name the model APIs would not accept (characters outside <c>[a-zA-Z0-9_-]</c>, or too
    /// long) is sanitized and gets a short hash of the original name, so two different names never collapse into one. The
    /// name is never parsed back: the model's name is mapped to the tool through the request's own table.
    /// </summary>
    public static string Mcp(string serverName, string toolName)
    {
        var prefix = serverName + "__";
        var budget = MaxLength - prefix.Length;
        var sanitized = new string([.. toolName.Select(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-' ? c : '_')]);
        if (sanitized == toolName && toolName.Length <= budget)
        {
            return prefix + toolName;
        }

        var suffix = "_" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(toolName)))[..HashLength];
        return prefix + sanitized[..Math.Min(sanitized.Length, budget - suffix.Length)] + suffix;
    }
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
