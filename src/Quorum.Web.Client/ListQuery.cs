namespace Quorum.Web.Client;

/// <summary>One choice of a filter: what goes in the URL and what the admin reads.</summary>
public sealed record FilterOption(string Value, string Label);

/// <summary>
/// The filters, the search and nothing else of a list, as they travel in the query string of the page
/// (<c>?scope=assistant&amp;tenant=…&amp;q=search</c>), so a view can be shared and a reload keeps it.
/// </summary>
public sealed record ListQuery
{
    public IReadOnlyList<string> Scope { get; init; } = [];

    public IReadOnlyList<Guid> Tenant { get; init; } = [];

    public IReadOnlyList<Guid> Assistant { get; init; } = [];

    public IReadOnlyList<string> Type { get; init; } = [];

    public IReadOnlyList<string> Status { get; init; } = [];

    public string? Q { get; init; }

    public bool HasFilters => Scope.Count + Tenant.Count + Assistant.Count + Type.Count + Status.Count > 0 || !string.IsNullOrWhiteSpace(Q);

    public static ListQuery Empty => new();

    /// <summary>Reads the filters from a URI or from a bare query string; unknown keys and ids that are not ids are ignored.</summary>
    public static ListQuery Parse(string? uriOrQuery)
    {
        var query = new ListQuery();
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

        foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var key = Decode(separator < 0 ? pair : pair[..separator]);
            var value = separator < 0 ? string.Empty : pair[(separator + 1)..];

            query = key switch
            {
                "scope" => query with { Scope = Merge(query.Scope, Values(value)) },
                "type" => query with { Type = Merge(query.Type, Values(value)) },
                "status" => query with { Status = Merge(query.Status, Values(value)) },
                "tenant" => query with { Tenant = Merge(query.Tenant, Ids(value)) },
                "assistant" => query with { Assistant = Merge(query.Assistant, Ids(value)) },
                "q" => query with { Q = string.IsNullOrWhiteSpace(Decode(value)) ? null : Decode(value).Trim() },
                _ => query,
            };
        }

        return query;
    }

    /// <summary>The query string, with its leading <c>?</c>, or an empty string when nothing is filtered. The order is fixed.</summary>
    public string ToQueryString()
    {
        var parts = new List<string>();
        Add(parts, "scope", Scope);
        Add(parts, "tenant", Tenant.Select(id => id.ToString()));
        Add(parts, "assistant", Assistant.Select(id => id.ToString()));
        Add(parts, "type", Type);
        Add(parts, "status", Status);
        if (!string.IsNullOrWhiteSpace(Q))
        {
            parts.Add($"q={Uri.EscapeDataString(Q.Trim())}");
        }

        return parts.Count == 0 ? string.Empty : "?" + string.Join('&', parts);
    }

    /// <summary>The same address with these filters in place of the ones it carries; the fragment is dropped.</summary>
    public string ApplyTo(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var end = uri.IndexOfAny(['?', '#']);
        return (end < 0 ? uri : uri[..end]) + ToQueryString();
    }

    private static void Add(List<string> parts, string key, IEnumerable<string> values)
    {
        var list = values.Select(Uri.EscapeDataString).ToList();
        if (list.Count > 0)
        {
            parts.Add($"{key}={string.Join(',', list)}");
        }
    }

    private static string Decode(string text) => Uri.UnescapeDataString(text.Replace('+', ' '));

    private static List<string> Values(string value) =>
        [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Decode).Where(v => v.Length > 0)];

    private static List<Guid> Ids(string value) =>
        [.. Values(value).Select(v => Guid.TryParse(v, out var id) ? id : (Guid?)null).Where(id => id.HasValue).Select(id => id!.Value)];

    private static List<T> Merge<T>(IReadOnlyList<T> current, List<T> more) => [.. current.Union(more)];
}
