namespace Quorum.Web.Client;

/// <summary>Which sections of the app a role may open. The API is the real guard; this keeps the menu and the pages honest.</summary>
public static class AccessRules
{
    public const string Admin = "Admin";

    private static readonly string[] AdminSections = ["tools", "knowledge"];

    public static bool IsAdmin(string? role) => string.Equals(role, Admin, StringComparison.Ordinal);

    /// <summary>Whether the page at this base-relative path (<c>tools/mcp</c>, <c>knowledge?scope=global</c>) is only for admins.</summary>
    public static bool IsAdminOnly(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        var end = relativePath.IndexOfAny(['?', '#']);
        var path = (end < 0 ? relativePath : relativePath[..end]).Trim('/');
        var first = path.Split('/', 2)[0];
        return AdminSections.Contains(first, StringComparer.OrdinalIgnoreCase);
    }

    public static bool CanOpen(string? role, string relativePath) => IsAdmin(role) || !IsAdminOnly(relativePath);
}
