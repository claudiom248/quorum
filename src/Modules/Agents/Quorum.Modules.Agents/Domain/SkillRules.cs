namespace Quorum.Modules.Agents.Domain;

/// <summary>What an administrator may enter for a skill (spec 017 D-11, D-12, D-21).</summary>
internal static class SkillRules
{
    /// <summary>The limit of the Agent Skills standard: the description is in the prompt on every turn.</summary>
    public const int MaxDescriptionLength = 1024;

    public const int MaxDisplayNameLength = 200;

    /// <summary>The slug is the same format as an agent's (D-12), so it reuses its rule.</summary>
    public static bool IsValidSlug(string? slug) => AgentRules.IsValidSlug(slug);

    public static bool IsValidDescription(string? description) =>
        description is { Length: > 0 } && description.Length <= MaxDescriptionLength;

    /// <summary>The body has no size limit (D-10); it only has to exist.</summary>
    public static bool IsValidBody(string? body) => !string.IsNullOrWhiteSpace(body);
}
