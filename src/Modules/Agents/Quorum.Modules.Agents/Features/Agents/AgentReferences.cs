using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Features.Skills;
using Quorum.Modules.Agents.Persistence;

namespace Quorum.Modules.Agents.Features.Agents;

/// <summary>Who depends on an agent: the assistants that answer through it and the agents that call it (spec 013 D-16, D-21, D-25).</summary>
internal static class AgentReferences
{
    /// <summary>Everything that points at the agent: what makes its deletion a conflict.</summary>
    public static async Task<List<BlockingReference>> OfAsync(AgentsDbContext db, Guid agentId, CancellationToken cancellationToken)
    {
        var assistants = await RootOfAsync(db, agentId, cancellationToken);
        var callers = await CallersAsync(db, agentId, cancellationToken);

        return
        [
            .. assistants.Select(a => new BlockingReference(BlockingReference.AssistantKind, a.Id, a.Name)),
            .. callers.Select(c => new BlockingReference(BlockingReference.AgentKind, c.Id, c.DisplayName)),
        ];
    }

    /// <summary>The users of the agent that could not see it with the scope it is about to get.</summary>
    public static async Task<List<BlockingReference>> BrokenByScopeAsync(
        AgentsDbContext db,
        Agent agent,
        AgentScope newScope,
        IReadOnlyCollection<Guid> newTenantIds,
        CancellationToken cancellationToken)
    {
        var callers = await CallersAsync(db, agent.Id, cancellationToken);
        var assistants = await RootOfAsync(db, agent.Id, cancellationToken);

        // The owner always sees its agent; an assistant of another tenant needs the new scope to still include that tenant.
        var hidden = assistants.Where(a => a.TenantId != agent.TenantId && newScope != AgentScope.Global && !newTenantIds.Contains(a.TenantId));
        var cut = callers.Where(c => !AgentRules.IsVisibleAsWidelyAs(newScope, newTenantIds.ToList(), c.Scope, c.ScopeTenantIds));

        // A skill must be at least as visible as the agent that holds it (spec 017 D-13), so a wider agent scope can break it too.
        var skills = await SkillScopeRules.SkillsBrokenByAsync(db, agent.Id, newScope, newTenantIds, cancellationToken);

        return
        [
            .. hidden.Select(a => new BlockingReference(BlockingReference.AssistantKind, a.Id, a.Name)),
            .. cut.Select(c => new BlockingReference(BlockingReference.AgentKind, c.Id, c.DisplayName)),
            .. skills,
        ];
    }

    // Assistants of every tenant: a global agent may be the root of an assistant that is not the owner's.
    private static Task<List<Assistant>> RootOfAsync(AgentsDbContext db, Guid agentId, CancellationToken cancellationToken) =>
        db.Assistants.IgnoreQueryFilters().AsNoTracking().Where(a => a.RootAgentId == agentId).OrderBy(a => a.Name).ToListAsync(cancellationToken);

    private static Task<List<Agent>> CallersAsync(AgentsDbContext db, Guid agentId, CancellationToken cancellationToken) =>
        (from edge in db.AgentSubAgents.AsNoTracking()
         join parent in db.Agents.AsNoTracking() on edge.ParentAgentId equals parent.Id
         where edge.ChildAgentId == agentId
         orderby parent.DisplayName
         select parent)
        .ToListAsync(cancellationToken);
}
