using System.Text.RegularExpressions;

namespace Quorum.Modules.Agents.Domain;

/// <summary>What an administrator may enter for an agent (spec 013 D-18): the slug is also the name of its delegation tool.</summary>
internal static partial class AgentRules
{
    public const int MaxSlugLength = 32;

    public const int MaxDisplayNameLength = 200;

    public const int MaxDescriptionLength = 2000;

    /// <summary>Lower-case letters, digits and hyphens, starting with a letter or digit: the format of an MCP server's name, without <c>_</c>.</summary>
    public static bool IsValidSlug(string? slug) =>
        slug is { Length: > 0 and <= MaxSlugLength } && SlugPattern().IsMatch(slug);

    /// <summary>
    /// Whether the child is visible at least as widely as the parent (spec 013 D-21): a global parent accepts only global children, one
    /// limited to some tenants accepts global children or children whose scope includes those tenants. Both have the same owner, who sees either.
    /// </summary>
    public static bool IsVisibleAsWidelyAs(AgentScope child, IReadOnlyCollection<Guid> childTenantIds, AgentScope parent, IReadOnlyCollection<Guid> parentTenantIds) =>
        child == AgentScope.Global || (parent == AgentScope.Tenant && parentTenantIds.All(childTenantIds.Contains));

    /// <summary>Whether the slug of a sub-agent is also the name of a tool of its parent: the calling model sees one flat list (spec 013 AC-26).</summary>
    public static bool ClashesWithTool(string slug, IEnumerable<string> toolNames) =>
        toolNames.Contains(slug, StringComparer.Ordinal);

    /// <summary>A slug made from a display name: what the migration does in SQL for the assistants that existed before agents.</summary>
    public static string SlugFrom(string name)
    {
        var slug = NonSlugCharacters().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > MaxSlugLength)
        {
            slug = slug[..MaxSlugLength].TrimEnd('-');
        }

        return slug.Length == 0 ? "agent" : slug;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonSlugCharacters();
}
