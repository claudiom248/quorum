using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Features.Skills;
using Quorum.Modules.Agents.Persistence;

namespace Quorum.Modules.Agents.Tools.Skills;

/// <summary>
/// Reads the skills of an agent when a turn starts (spec 017 D-3, D-6, D-17, D-18): the assigned skills visible to the tenant of the turn,
/// with their bodies, and the built-in <c>load_skill</c> row when it is enabled. Nothing when there are no skills, so an agent without
/// them is unchanged (D-18). A shared agent runs with the skills its owner assigned, as it does with its tools.
/// </summary>
internal static class SkillRuntime
{
    /// <returns>The tool for the turn, or <see langword="null"/> when the agent has no skill to offer or <c>load_skill</c> is disabled.</returns>
    public static async Task<SkillQuorumTool?> LoadAsync(AgentsDbContext db, Guid agentId, CancellationToken cancellationToken)
    {
        var builtin = await db.Tools.AsNoTracking()
            .Where(tool => tool.Origin == ToolOrigin.Builtin && tool.Name == BuiltinToolCatalog.LoadSkill && tool.State == ToolState.Enabled)
            .SingleOrDefaultAsync(cancellationToken);
        if (builtin is null)
        {
            return null;
        }

        var owner = await db.Agents.IgnoreQueryFilters().Where(agent => agent.Id == agentId).Select(agent => agent.TenantId).SingleOrDefaultAsync(cancellationToken);
        var assigned = db.AgentSkills.IgnoreQueryFilters().Where(link => link.AgentId == agentId && link.TenantId == owner).Select(link => link.SkillId);
        var skills = await SkillVisibility.For(db)
            .AsNoTracking()
            .Where(skill => assigned.Contains(skill.Id))
            .OrderBy(skill => skill.Slug)
            .ToListAsync(cancellationToken);
        if (skills.Count == 0)
        {
            return null;
        }

        var definition = BuiltinToolCatalog.All.Single(tool => tool.Name == BuiltinToolCatalog.LoadSkill);
        var descriptor = new ActiveTool(builtin.Id, ToolOrigin.Builtin, builtin.Name, builtin.ExposedName, null, definition.Description, definition.InputSchema);
        var snapshot = skills.Select(skill => new SkillSnapshotEntry(skill.Id, skill.Slug, skill.Description, skill.Body)).ToList();
        return new SkillQuorumTool(descriptor, snapshot);
    }
}
