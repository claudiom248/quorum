using System.Text.RegularExpressions;

namespace Quorum.Modules.Agents.Tools.Mcp;

/// <summary>What an admin may enter for an MCP server: its name, URL, headers and scope.</summary>
internal static partial class McpServerRules
{
    /// <summary>Leaves room in the 64 characters of a model-facing name for <c>__</c> and a tool name (D-16).</summary>
    public const int MaxNameLength = 32;

    public const int MaxUrlLength = 2048;

    public const int MaxHeaders = 20;

    public const int MaxHeaderValueLength = 4096;

    /// <summary>The prefix of native tools: a server with this name would be able to pose as them.</summary>
    private const string ReservedName = "native";

    // The headers the transport or the protocol own; letting an admin set them would break or confuse the connection.
    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Content-Type", "Content-Encoding", "Transfer-Encoding", "Connection", "Upgrade", "Accept",
        "Mcp-Session-Id", "Mcp-Protocol-Version", "Last-Event-ID",
    };

    /// <summary>Lower-case letters, digits, <c>_</c> and <c>-</c>, starting with a letter or digit; no <c>__</c>, which separates the server from the tool.</summary>
    public static bool IsValidName(string? name) =>
        name is { Length: > 0 and <= MaxNameLength }
        && NamePattern().IsMatch(name)
        && !name.Contains("__", StringComparison.Ordinal)
        && !string.Equals(name, ReservedName, StringComparison.Ordinal);

    /// <summary>
    /// An absolute http(s) URL without credentials: secrets belong in the headers, which are stored encrypted. The URL itself,
    /// query string included, is stored as it is and returned by the API, so a key placed in it is not protected.
    /// </summary>
    public static bool TryParseUrl(string? url, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxUrlLength
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https")
            || parsed.UserInfo.Length > 0
            || parsed.Host.Length == 0)
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    /// <returns>What is wrong with the headers, or <see langword="null"/> when they can be used.</returns>
    public static string? ValidateHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null)
        {
            return null;
        }

        if (headers.Count > MaxHeaders)
        {
            return $"At most {MaxHeaders} headers are allowed.";
        }

        foreach (var (name, value) in headers)
        {
            if (!HeaderNamePattern().IsMatch(name))
            {
                return $"'{name}' is not a valid header name.";
            }

            if (ReservedHeaders.Contains(name))
            {
                return $"The header '{name}' is set by the connection and cannot be configured.";
            }

            if (value is null || value.Length > MaxHeaderValueLength || value.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
            {
                return $"The value of '{name}' is not valid.";
            }
        }

        return null;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[!#$%&'*+.^_`|~0-9A-Za-z-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderNamePattern();
}
