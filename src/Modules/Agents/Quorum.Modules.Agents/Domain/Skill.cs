namespace Quorum.Modules.Agents.Domain;

/// <summary>
/// A named piece of instructions an agent loads on demand (spec 017 D-1). The slug is the argument of <c>load_skill</c> and the stable
/// reference of the declarative format (D-12, D-27); the body is text only.
/// </summary>
internal sealed class Skill
{
    private Skill()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>The owner.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>Unique per tenant, in the format of agent slugs (D-12).</summary>
    public string Slug { get; private set; } = null!;

    public string DisplayName { get; private set; } = null!;

    /// <summary>When to use the skill: it is in the prompt on every turn of an agent that has it (D-11).</summary>
    public string Description { get; private set; } = null!;

    public string Body { get; private set; } = null!;

    public AgentScope Scope { get; private set; }

    /// <summary>With <see cref="AgentScope.Tenant"/>: the tenants besides the owner that may use the skill.</summary>
    public List<Guid> ScopeTenantIds { get; private set; } = [];

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Skill Create(
        Guid tenantId,
        string slug,
        string displayName,
        string description,
        string body,
        AgentScope scope,
        IEnumerable<Guid> scopeTenantIds,
        DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(now),
            TenantId = tenantId,
            Slug = slug,
            DisplayName = displayName,
            Description = description,
            Body = body,
            Scope = scope,
            ScopeTenantIds = [.. scopeTenantIds],
            UpdatedAt = now,
        };

    /// <summary>Replaces every editable field. Assignments are by id, so they survive a change of slug (spec 017 edge cases).</summary>
    public void Revise(
        string slug,
        string displayName,
        string description,
        string body,
        AgentScope scope,
        IEnumerable<Guid> scopeTenantIds,
        DateTimeOffset now)
    {
        Slug = slug;
        DisplayName = displayName;
        Description = description;
        Body = body;
        Scope = scope;
        ScopeTenantIds = [.. scopeTenantIds];
        UpdatedAt = now;
    }
}

/// <summary>The assignment of a skill to an agent (spec 017 D-2); unique per pair.</summary>
internal sealed class AgentSkill
{
    private AgentSkill()
    {
    }

    public Guid AgentId { get; private set; }

    public Guid SkillId { get; private set; }

    public Guid TenantId { get; private set; }

    public static AgentSkill Create(Guid tenantId, Guid agentId, Guid skillId) => new()
    {
        TenantId = tenantId,
        AgentId = agentId,
        SkillId = skillId,
    };
}

/// <summary>How a skill imported from a package is named (spec 017 D-28). Stored per tenant; absent means <see cref="Slug"/>.</summary>
internal enum SkillDisplayNameStyle
{
    Slug,
    Humanized,
}

/// <summary>The skill settings of one tenant, created on the first save (spec 017 D-28).</summary>
internal sealed class SkillSettings
{
    private SkillSettings()
    {
    }

    public Guid TenantId { get; private set; }

    public SkillDisplayNameStyle DisplayNameStyle { get; private set; }

    public static SkillSettings Create(Guid tenantId, SkillDisplayNameStyle style) => new()
    {
        TenantId = tenantId,
        DisplayNameStyle = style,
    };

    public void ChangeDisplayNameStyle(SkillDisplayNameStyle style) => DisplayNameStyle = style;
}
