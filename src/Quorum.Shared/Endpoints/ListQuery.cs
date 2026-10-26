using Microsoft.AspNetCore.Http;
using Quorum.Shared.Results;

namespace Quorum.Shared.Endpoints;

/// <summary>
/// The filters, the search and the page of a list endpoint, as the query string carries them (spec 010). Plumbing only: binding and
/// validation. What each filter means is decided by the module that owns the list, on its own query.
/// </summary>
/// <param name="Scope">The scope kinds to keep (any of them).</param>
/// <param name="Tenant">Items owned by, or shared with, any of these tenants.</param>
/// <param name="Assistant">Items an assistant can use: scoped to it, or not scoped to assistants at all.</param>
/// <param name="Cursor">Opaque position after the last item of the previous page; see <see cref="ListCursor"/>.</param>
public sealed record ListQuery(
    IReadOnlyList<string> Scope,
    IReadOnlyList<Guid> Tenant,
    IReadOnlyList<Guid> Assistant,
    IReadOnlyList<string> Type,
    IReadOnlyList<string> Status,
    string? Q,
    int Limit,
    string? Cursor)
{
    public const int DefaultLimit = 50;

    public const int MaxLimit = 200;

    public const int MaxSearchLength = 200;

    private static readonly Error InvalidLimit =
        Error.Validation("list.invalid_limit", $"The limit must be a whole number from 1 to {MaxLimit}.");

    private static readonly Error SearchTooLong =
        Error.Validation("list.invalid_filter", $"The search text must be at most {MaxSearchLength} characters.");

    private static readonly Error InvalidIds =
        Error.Validation("list.invalid_filter", "The tenant and assistant filters take ids.");

    public static ListQuery Empty { get; } = new([], [], [], [], [], null, DefaultLimit, null);

    /// <summary>Reads the query string. A value may repeat or be comma separated; unknown keys are ignored.</summary>
    public static Result<ListQuery> Parse(IQueryCollection query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var limit = DefaultLimit;
        if (query.TryGetValue("limit", out var limitText) && limitText.Count > 0
            && (!int.TryParse(limitText[0], out limit) || limit is < 1 or > MaxLimit))
        {
            return InvalidLimit;
        }

        if (!TryIds(query, "tenant", out var tenants) || !TryIds(query, "assistant", out var assistants))
        {
            return InvalidIds;
        }

        var q = query.TryGetValue("q", out var qText) ? qText.ToString().Trim() : null;
        if (q is { Length: > MaxSearchLength })
        {
            return SearchTooLong;
        }

        var cursor = query.TryGetValue("cursor", out var cursorText) && !string.IsNullOrEmpty(cursorText) ? cursorText.ToString() : null;

        return new ListQuery(Values(query, "scope"), tenants, assistants, Values(query, "type"), Values(query, "status"), string.IsNullOrEmpty(q) ? null : q, limit, cursor);
    }

    private static List<string> Values(IQueryCollection query, string key) =>
        query.TryGetValue(key, out var values)
            ? [.. values.SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Distinct(StringComparer.OrdinalIgnoreCase)]
            : [];

    private static bool TryIds(IQueryCollection query, string key, out List<Guid> ids)
    {
        ids = [];
        foreach (var value in Values(query, key))
        {
            if (!Guid.TryParse(value, out var id))
            {
                return false;
            }

            ids.Add(id);
        }

        return true;
    }
}

/// <param name="Cursor">Present when there is a next page: pass it back as <c>cursor</c>.</param>
public sealed record Page<T>(IReadOnlyList<T> Items, string? Cursor);
