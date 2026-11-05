using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;

namespace Quorum.Modules.Agents.Features.Skills;

/// <summary>
/// The skills the current tenant may use: its own, and the <c>Global</c> ones and the ones whose scope includes it (spec 017 D-13).
/// The tenant filter is off here, so the rule is stated in the query; the same shape as the visibility of agents.
/// </summary>
internal static class SkillVisibility
{
    public static IQueryable<Skill> For(AgentsDbContext db)
    {
        var tenantId = db.CurrentTenantId ?? Guid.Empty;
        return db.Skills.IgnoreQueryFilters().Where(skill =>
            skill.TenantId == tenantId
            || skill.Scope == AgentScope.Global
            || (skill.Scope == AgentScope.Tenant && skill.ScopeTenantIds.Contains(tenantId)));
    }
}
