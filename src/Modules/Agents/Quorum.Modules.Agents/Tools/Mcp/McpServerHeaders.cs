using System.Text.Json;
using Quorum.Modules.Agents.Secrets;

namespace Quorum.Modules.Agents.Tools.Mcp;

/// <summary>The static headers of a server: kept as one encrypted JSON object, never returned by the API (D-21).</summary>
internal static class McpServerHeaders
{
    public static string? ProtectHeaders(this McpHeadersProtector protector, IReadOnlyDictionary<string, string>? headers)
    {
        ArgumentNullException.ThrowIfNull(protector);
        return headers is null or { Count: 0 } ? null : protector.Protect(JsonSerializer.Serialize(headers));
    }

    public static Dictionary<string, string>? RevealHeaders(this McpHeadersProtector protector, string? protectedHeaders)
    {
        ArgumentNullException.ThrowIfNull(protector);
        return protectedHeaders is null
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, string>>(protector.Unprotect(protectedHeaders)!);
    }
}
