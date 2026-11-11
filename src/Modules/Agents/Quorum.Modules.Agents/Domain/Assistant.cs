namespace Quorum.Modules.Agents.Domain;

/// <summary>What a conversation talks to: a root agent (spec 013 D-2) and the parallelism its delegations may use (D-14).</summary>
internal sealed class Assistant
{
    public const int DefaultMaxParallelSubAgents = 1;

    private Assistant()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = null!;

    public Guid RootAgentId { get; private set; }

    /// <summary>How many sub-agents may run at once; 1 runs them one after the other, as local hardware needs (D-23).</summary>
    public int MaxParallelSubAgents { get; private set; } = DefaultMaxParallelSubAgents;

    /// <summary>Only exposed assistants can be chosen by consumers (spec 018 D-1).</summary>
    public bool Exposed { get; private set; }

    /// <summary>When on, consumers see the root agent's tools and may turn the optional ones off (spec 018 D-2).</summary>
    public bool AllowToolSelection { get; private set; }

    public static Assistant Create(Guid tenantId, string name, Guid rootAgentId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        TenantId = tenantId,
        Name = name,
        RootAgentId = rootAgentId,
    };

    public void Revise(string name, Guid rootAgentId, int maxParallelSubAgents)
    {
        Name = name;
        RootAgentId = rootAgentId;
        MaxParallelSubAgents = maxParallelSubAgents;
    }

    public void SetChatExposure(bool exposed, bool allowToolSelection)
    {
        Exposed = exposed;
        AllowToolSelection = allowToolSelection;
    }

    /// <summary>Same as <see cref="Create"/> with a parallelism limit chosen by the administrator.</summary>
    public static Assistant Create(Guid tenantId, string name, Guid rootAgentId, int maxParallelSubAgents, DateTimeOffset now)
    {
        var assistant = Create(tenantId, name, rootAgentId, now);
        assistant.MaxParallelSubAgents = maxParallelSubAgents;
        return assistant;
    }
}
