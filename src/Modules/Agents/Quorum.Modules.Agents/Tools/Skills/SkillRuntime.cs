using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Features.Skills;
using Quorum.Modules.Agents.Persistence;

namespace Quorum.Modules.Agents.Tools.Skills;

/// <summary>
/// Reads the skills of the agents of a turn when it starts (spec 017 D-3, D-6, D-17, D-18): the assigned skills visible to the tenant of
/// the turn, with their bodies, and the built-in <c>load_skill</c> row when it is enabled. Nothing for an agent without skills, so it is
/// unchanged (D-18). A shared agent runs with the skills its owner assigned, as it does with its tools.
/// </summary>
internal static class SkillRuntime
{
    /// <returns>The tool for the turn, or <see langword="null"/> when the agent has no skill to offer or <c>load_skill</c> is disabled.</returns>
    public static async Task<SkillQuorumTool?> LoadAsync(AgentsDbContext db, Guid agentId, ILogger logger, CancellationToken cancellationToken) =>
        (await LoadManyAsync(db, [agentId], logger, cancellationToken)).GetValueOrDefault(agentId);

    /// <summary>
    /// The tool of each agent that has skills, in one read: the built-in row once, the owners and assignments of all the agents together,
    /// and their skills in one query, instead of three queries for each agent of the turn.
    /// </summary>
    public static async Task<Dictionary<Guid, SkillQuorumTool>> LoadManyAsync(
        AgentsDbContext db,
        IReadOnlyCollection<Guid> agentIds,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var tools = new Dictionary<Guid, SkillQuorumTool>();
        if (agentIds.Count == 0)
        {
            return tools;
        }

        var builtin = await db.Tools.AsNoTracking()
            .Where(tool => tool.Origin == ToolOrigin.Builtin && tool.Name == BuiltinToolCatalog.LoadSkill && tool.State == ToolState.Enabled)
            .SingleOrDefaultAsync(cancellationToken);
        if (builtin is null)
        {
            return tools;
        }

        // An agent another tenant shares runs with the assignments of its owner (as with its tools).
        var owners = await db.Agents.IgnoreQueryFilters().AsNoTracking()
            .Where(agent => agentIds.Contains(agent.Id))
            .Select(agent => new { agent.Id, agent.TenantId })
            .ToDictionaryAsync(agent => agent.Id, agent => agent.TenantId, cancellationToken);
        var links = (await db.AgentSkills.IgnoreQueryFilters().AsNoTracking()
                .Where(link => agentIds.Contains(link.AgentId))
                .Select(link => new { link.AgentId, link.SkillId, link.TenantId })
                .ToListAsync(cancellationToken))
            .Where(link => owners.TryGetValue(link.AgentId, out var owner) && owner == link.TenantId)
            .ToList();
        if (links.Count == 0)
        {
            return tools;
        }

        var skillIds = links.Select(link => link.SkillId).Distinct().ToList();
        var skills = await SkillVisibility.For(db)
            .AsNoTracking()
            .Where(skill => skillIds.Contains(skill.Id))
            .ToDictionaryAsync(skill => skill.Id, cancellationToken);

        var definition = BuiltinToolCatalog.All.Single(tool => tool.Name == BuiltinToolCatalog.LoadSkill);
        foreach (var agentId in agentIds)
        {
            var snapshot = links
                .Where(link => link.AgentId == agentId)
                .Select(link => skills.GetValueOrDefault(link.SkillId))
                .OfType<Skill>()
                .OrderBy(skill => skill.Slug, StringComparer.Ordinal)
                .Select(skill => new SkillSnapshotEntry(skill.Id, skill.Slug, skill.Description, skill.Body))
                .ToList();
            if (snapshot.Count > 0)
            {
                var descriptor = new ActiveTool(builtin.Id, ToolOrigin.Builtin, builtin.Name, builtin.ExposedName, null, definition.Description, definition.InputSchema);
                tools[agentId] = new SkillQuorumTool(descriptor, snapshot, agentId, logger);
            }
        }

        return tools;
    }
}
