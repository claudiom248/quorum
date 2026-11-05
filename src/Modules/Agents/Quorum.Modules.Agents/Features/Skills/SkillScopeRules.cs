using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;

namespace Quorum.Modules.Agents.Features.Skills;

/// <summary>
/// The scope rule between a skill and the agents that hold it (spec 017 D-13): a skill is visible at least as widely as each agent
/// it is assigned to. The same rule as between an agent and its sub-agents (spec 013 D-21), reused through <see cref="AgentRules"/>.
/// </summary>
internal static class SkillScopeRules
{
    /// <summary>The agents that hold the skill and could not see it with the scope it is about to get.</summary>
    public static async Task<List<BlockingReference>> AgentsBrokenByAsync(
        AgentsDbContext db,
        Guid skillId,
        AgentScope scope,
        IReadOnlyCollection<Guid> tenantIds,
        CancellationToken cancellationToken)
    {
        var holders = await (
            from link in db.AgentSkills.AsNoTracking()
            join agent in db.Agents.AsNoTracking() on link.AgentId equals agent.Id
            where link.SkillId == skillId
            orderby agent.DisplayName
            select agent)
            .ToListAsync(cancellationToken);

        return holders
            .Where(agent => !AgentRules.IsVisibleAsWidelyAs(scope, tenantIds.ToList(), agent.Scope, agent.ScopeTenantIds))
            .Select(agent => new BlockingReference(BlockingReference.AgentKind, agent.Id, agent.DisplayName))
            .ToList();
    }

    /// <summary>The skills of an agent that are less visible than the agent with the scope it is about to get.</summary>
    public static async Task<List<BlockingReference>> SkillsBrokenByAsync(
        AgentsDbContext db,
        Guid agentId,
        AgentScope scope,
        IReadOnlyCollection<Guid> tenantIds,
        CancellationToken cancellationToken)
    {
        // A skill of another tenant (a Global one) is read with the filter off: the assignment belongs to this tenant.
        var assigned = await (
            from link in db.AgentSkills.AsNoTracking()
            join skill in db.Skills.IgnoreQueryFilters().AsNoTracking() on link.SkillId equals skill.Id
            where link.AgentId == agentId
            orderby skill.Slug
            select skill)
            .ToListAsync(cancellationToken);

        return assigned
            .Where(skill => !AgentRules.IsVisibleAsWidelyAs(skill.Scope, skill.ScopeTenantIds, scope, tenantIds.ToList()))
            .Select(skill => new BlockingReference(BlockingReference.SkillKind, skill.Id, skill.Slug))
            .ToList();
    }

    /// <summary>The agents that hold the skill, for the 409 of a delete.</summary>
    public static Task<List<BlockingReference>> HoldersOfAsync(AgentsDbContext db, Guid skillId, CancellationToken cancellationToken) =>
        (from link in db.AgentSkills.AsNoTracking()
         join agent in db.Agents.AsNoTracking() on link.AgentId equals agent.Id
         where link.SkillId == skillId
         orderby agent.DisplayName
         select new BlockingReference(BlockingReference.AgentKind, agent.Id, agent.DisplayName))
        .ToListAsync(cancellationToken);
}
