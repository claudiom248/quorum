namespace Quorum.Web.Client;

/// <summary>
/// The filters and the page of the audit log, as they travel in the address (<c>?from=2026-10-01&amp;outcome=Failed&amp;page=2</c>),
/// so a view of the log can be shared and a reload keeps it. The log has its own paging (page and total) and no cursor.
/// </summary>
public sealed record AuditQuery
{
    public const int PageSize = 50;

    public string? From { get; init; }

    public string? To { get; init; }

    public string? Tool { get; init; }

    public Guid? User { get; init; }

    public string? Outcome { get; init; }

    public int Page { get; init; } = 1;

    public bool HasFilters => From is not null || To is not null || Tool is not null || User is not null || Outcome is not null;

    public static AuditQuery Parse(string? uriOrQuery)
    {
        var text = uriOrQuery ?? string.Empty;
        var start = text.IndexOf('?', StringComparison.Ordinal);
        if (start >= 0)
        {
            text = text[(start + 1)..];
        }

        var hash = text.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            text = text[..hash];
        }

        var query = new AuditQuery();
        foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var key = Decode(separator < 0 ? pair : pair[..separator]);
            var value = separator < 0 ? string.Empty : Decode(pair[(separator + 1)..]).Trim();
            if (value.Length == 0)
            {
                continue;
            }

            query = key switch
            {
                "from" => query with { From = value },
                "to" => query with { To = value },
                "tool" => query with { Tool = value },
                "user" => Guid.TryParse(value, out var user) ? query with { User = user } : query,
                "outcome" => query with { Outcome = value },
                "page" => int.TryParse(value, out var page) && page >= 1 ? query with { Page = page } : query,
                _ => query,
            };
        }

        return query;
    }

    /// <summary>The query string with its leading <c>?</c>, or an empty string when nothing is set. The order is fixed.</summary>
    public string ToQueryString()
    {
        var parts = new List<string>();
        Add(parts, "from", From);
        Add(parts, "to", To);
        Add(parts, "tool", Tool);
        Add(parts, "user", User?.ToString());
        Add(parts, "outcome", Outcome);
        if (Page > 1)
        {
            parts.Add($"page={Page}");
        }

        return parts.Count == 0 ? string.Empty : "?" + string.Join('&', parts);
    }

    /// <summary>The request to the API: the same filters and the page size.</summary>
    public string ToApiQuery() => ToQueryString() is { Length: > 0 } query
        ? $"{query}&pageSize={PageSize}"
        : $"?pageSize={PageSize}";

    private static void Add(List<string> parts, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add($"{key}={Uri.EscapeDataString(value)}");
        }
    }

    private static string Decode(string text) => Uri.UnescapeDataString(text.Replace('+', ' '));
}
