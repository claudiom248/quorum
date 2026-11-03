using System.Text.Json;
using Microsoft.Extensions.AI;
using Quorum.Modules.Agents.Domain;

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

/// <summary>
/// The names the model calls tools by. A tool made in Quorum (built-in, rag, lookup, web search, web fetch) is called by its own name,
/// the one people read in the lists and in the ledger; the model and the person see the same thing. Only an MCP tool carries its server
/// (<c>server__tool</c>), because it comes from outside and two servers may offer a tool of one name. The names of a tenant's tools,
/// agents and lookup operations share one space (<see cref="ToolNameRules"/>), so no two collapse into one.
/// </summary>
internal static class ToolNaming
{
    /// <summary>What the audit log records as the server of a rag tool: the kind of tool, since the name the model sees no longer says it. A call is found by it like a call of an MCP server's tool.</summary>
    public const string RagAuditServer = "rag";

    /// <summary>What the audit log records as the server of a lookup tool, as <see cref="RagAuditServer"/> does for rag tools.</summary>
    public const string LookupAuditServer = "lookup";

    /// <summary>What the audit log records as the server of a web search tool, as <see cref="RagAuditServer"/> does for rag tools.</summary>
    public const string WebSearchAuditServer = "web_search";

    /// <summary>What the audit log records as the server of a web fetch tool, as <see cref="WebSearchAuditServer"/> does for web search tools.</summary>
    public const string WebFetchAuditServer = "web_fetch";

    /// <summary>What the audit log records as the server of a delegation to a sub-agent (spec 013 D-31): the call has the sub-agent slug as its tool name.</summary>
    public const string AgentAuditServer = "agent";

    /// <summary>The longest name the model APIs accept (D-16).</summary>
    public const int MaxLength = 64;

    private const int HashLength = 8;

    /// <summary>The operations of a lookup tool, each its own function: the name the model sees is the tool's name and the operation.</summary>
    public const string EntriesOperation = "entries";

    public const string DocumentsOperation = "documents";

    public const string CountOperation = "count";

    public const string DescribeOperation = "describe";

    public const string QueryOperation = "query";

    public static string Builtin(string toolName) => toolName;

    public static string Rag(string toolName) => toolName;

    public static string Lookup(string toolName) => toolName;

    public static string WebSearch(string toolName) => toolName;

    public static string WebFetch(string toolName) => toolName;

    /// <summary>The function that reads the lists of a lookup tool; its sisters ask the knowledge bases (spec 007 A1).</summary>
    public static string LookupEntries(string toolName) => $"{toolName}_{EntriesOperation}";

    public static string LookupDocuments(string toolName) => $"{toolName}_{DocumentsOperation}";

    public static string LookupCount(string toolName) => $"{toolName}_{CountOperation}";

    /// <summary>The name the model sees for one operation of a lookup tool.</summary>
    public static string LookupFunction(string toolName, string operation) => operation switch
    {
        "documents" => LookupDocuments(toolName),
        "count" => LookupCount(toolName),
        "describe" => LookupDescribe(toolName),
        "query" => LookupQuery(toolName),
        _ => LookupEntries(toolName),
    };

    /// <summary>The lean manifest's function that describes the lists and knowledge bases of a lookup tool (spec 012 D-8).</summary>
    public static string LookupDescribe(string toolName) => $"{toolName}_{DescribeOperation}";

    /// <summary>The lean manifest's function that reads one of them (spec 012 D-8).</summary>
    public static string LookupQuery(string toolName) => $"{toolName}_{QueryOperation}";

    /// <summary>Every name the model may be offered for a tool: the lookup tools take one for each operation, the others their own.</summary>
    public static IReadOnlyList<string> FunctionsOf(ToolOrigin origin, string toolName) => origin == ToolOrigin.Lookup
        ? [LookupEntries(toolName), LookupDocuments(toolName), LookupCount(toolName), LookupDescribe(toolName), LookupQuery(toolName)]
        : [toolName];
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
