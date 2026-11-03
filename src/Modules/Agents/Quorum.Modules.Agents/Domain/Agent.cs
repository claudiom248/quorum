namespace Quorum.Modules.Agents.Domain;

/// <summary>Who may use an agent: its owner and, with <see cref="Tenant"/>, the tenants it lists (spec 013 D-20).</summary>
internal enum AgentScope
{
    Global,
    Tenant,
}

/// <summary>
/// What an agent does with a failing sub-agent (spec 013 D-13). Stored for the delegation runtime that comes with a later pull request.
/// </summary>
internal enum SubAgentFailurePolicy
{
    /// <summary>The caller gets a readable error as the tool result and the turn goes on.</summary>
    ReturnError,

    /// <summary>The failure ends the turn with an error to the user.</summary>
    AbortTurn,
}

/// <summary>
/// A system prompt, a model endpoint and tools (spec 013 D-1). An assistant answers through its root agent; an agent may also be
/// called by another as a tool (D-3), through the edges in <see cref="AgentSubAgent"/>.
/// </summary>
internal sealed class Agent
{
    private Agent()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>The owner.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>Unique per tenant; the name of the delegation tool seen by the calling model and the stable reference of the declarative format (D-18).</summary>
    public string Slug { get; private set; } = null!;

    public string DisplayName { get; private set; } = null!;

    /// <summary>When to call this agent: the description of its delegation tool (D-9).</summary>
    public string Description { get; private set; } = null!;

    public string SystemPrompt { get; private set; } = null!;

    /// <summary>Null only for an agent that always runs as a sub-agent and inherits the caller's endpoint (D-12).</summary>
    public Guid? ModelEndpointId { get; private set; }

    public AgentScope Scope { get; private set; }

    /// <summary>With <see cref="AgentScope.Tenant"/>: the tenants besides the owner that may use the agent.</summary>
    public List<Guid> ScopeTenantIds { get; private set; } = [];

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Agent Create(
        Guid tenantId,
        string slug,
        string displayName,
        string description,
        string systemPrompt,
        Guid? modelEndpointId,
        AgentScope scope,
        IEnumerable<Guid> scopeTenantIds,
        DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(now),
            TenantId = tenantId,
            Slug = slug,
            DisplayName = displayName,
            Description = description,
            SystemPrompt = systemPrompt,
            ModelEndpointId = modelEndpointId,
            Scope = scope,
            ScopeTenantIds = [.. scopeTenantIds],
            UpdatedAt = now,
        };

    public void ChangeSystemPrompt(string systemPrompt, DateTimeOffset now)
    {
        SystemPrompt = systemPrompt;
        UpdatedAt = now;
    }

    public void Revise(
        string slug,
        string displayName,
        string description,
        string systemPrompt,
        Guid? modelEndpointId,
        AgentScope scope,
        IEnumerable<Guid> scopeTenantIds,
        DateTimeOffset now)
    {
        Slug = slug;
        DisplayName = displayName;
        Description = description;
        SystemPrompt = systemPrompt;
        ModelEndpointId = modelEndpointId;
        Scope = scope;
        ScopeTenantIds = [.. scopeTenantIds];
        UpdatedAt = now;
    }
}

/// <summary>The edge of the graph (spec 013 D-2, D-24): <see cref="ParentAgentId"/> can call <see cref="ChildAgentId"/> as a tool.</summary>
internal sealed class AgentSubAgent
{
    private AgentSubAgent()
    {
    }

    public Guid ParentAgentId { get; private set; }

    public Guid ChildAgentId { get; private set; }

    public Guid TenantId { get; private set; }

    public SubAgentFailurePolicy FailurePolicy { get; private set; }

    public void ChangeFailurePolicy(SubAgentFailurePolicy failurePolicy) => FailurePolicy = failurePolicy;

    public static AgentSubAgent Create(Guid tenantId, Guid parentAgentId, Guid childAgentId, SubAgentFailurePolicy failurePolicy) => new()
    {
        TenantId = tenantId,
        ParentAgentId = parentAgentId,
        ChildAgentId = childAgentId,
        FailurePolicy = failurePolicy,
    };
}
