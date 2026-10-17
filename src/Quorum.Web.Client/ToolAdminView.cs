using System.Text.Json;
using Quorum.Web.Client.Api;

namespace Quorum.Web.Client;

/// <summary>What the tools admin page shows and reads from its forms, kept apart from the markup so it can be tested alone.</summary>
public static class ToolAdminView
{
    private static readonly (string Hint, string Label)[] KnownHints =
    [
        ("readOnlyHint", "read-only"),
        ("destructiveHint", "destructive"),
        ("idempotentHint", "idempotent"),
        ("openWorldHint", "reaches outside systems"),
    ];

    private static readonly char[] ListSeparators = [',', ';', ' ', '\t', '\r', '\n'];

    /// <summary>
    /// The hints a server claims about a tool, as short labels. They are information for the admin: the server may be wrong or
    /// lying, so they never change a policy and the page says whose claim they are.
    /// </summary>
    public static IReadOnlyList<string> DescribeAnnotations(JsonElement? annotations)
    {
        if (annotations is not { ValueKind: JsonValueKind.Object } hints)
        {
            return [];
        }

        var labels = new List<string>();
        foreach (var (hint, label) in KnownHints)
        {
            if (hints.TryGetProperty(hint, out var value) && value.ValueKind == JsonValueKind.True)
            {
                labels.Add(label);
            }
        }

        return labels;
    }

    /// <summary>
    /// Reads one <c>Name: value</c> header per line. <see langword="null"/> headers mean none were typed; <c>Error</c> says what is
    /// wrong with a line. The server checks reserved names and limits again.
    /// </summary>
    public static (Dictionary<string, string>? Headers, string? Error) ParseHeaders(string? text)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            var name = colon > 0 ? line[..colon].Trim() : string.Empty;
            if (name.Length == 0 || name.Any(char.IsWhiteSpace))
            {
                return (null, $"\"{Shorten(line)}\" is not a header. Write one per line as Name: value.");
            }

            if (!headers.TryAdd(name, line[(colon + 1)..].Trim()))
            {
                return (null, $"The header {name} appears twice.");
            }
        }

        return (headers.Count == 0 ? null : headers, null);
    }

    /// <summary>Reads ids separated by commas, spaces or new lines.</summary>
    public static (List<Guid> Ids, string? Error) ParseIds(string? text)
    {
        var ids = new List<Guid>();
        foreach (var part in (text ?? string.Empty).Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Guid.TryParse(part, out var id) || id == Guid.Empty)
            {
                return ([], $"\"{Shorten(part)}\" is not an id.");
            }

            if (!ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        return (ids, null);
    }

    /// <summary>Who can use a server, in words.</summary>
    public static string ScopeSummary(ToolScope scope, IReadOnlyList<AssistantSummary> assistants)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(assistants);

        switch (scope.Kind)
        {
            case ScopeKinds.Global:
                return "Every tenant";
            case ScopeKinds.Tenant:
                return scope.TenantIds.Count == 0
                    ? "This tenant"
                    : $"This tenant and {scope.TenantIds.Count} other(s)";
            case ScopeKinds.Assistant:
                var names = scope.AssistantIds.Select(id => assistants.FirstOrDefault(a => a.Id == id)?.Name ?? "an assistant you cannot see");
                return $"Assistants: {string.Join(", ", names)}";
            default:
                return scope.Kind;
        }
    }

    public static string StateLabel(string state) => state switch
    {
        ToolStates.PendingReview => "Needs review",
        ToolStates.Enabled => "Enabled",
        ToolStates.Disabled => "Disabled",
        _ => state,
    };

    private static string Shorten(string text) => text.Length <= 40 ? text : text[..40] + "…";
}
