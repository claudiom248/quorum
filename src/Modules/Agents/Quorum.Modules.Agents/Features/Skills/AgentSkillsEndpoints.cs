using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Results;

namespace Quorum.Modules.Agents.Features.Skills;

/// <summary>Which skills each agent has assigned (spec 017 D-2, D-15): assigning is a permission of the agents, not of the skills.</summary>
internal sealed class AgentSkillsEndpoints : IEndpointGroup
{
    private static readonly Error AgentNotFound = Error.NotFound("agent.not_found", "The agent does not exist.");

    public void Map(IEndpointRouteBuilder app)
    {
        var agents = app.MapGroup("/api/agents").WithTags("Agents");

        agents.MapGet("/{id:guid}/skills", GetAsync).RequireAuthorization(QuorumPolicies.AgentsManage).WithName("GetAgentSkills");
        agents.MapPut("/{id:guid}/skills", SetAsync).RequireAuthorization(QuorumPolicies.AgentsManage).WithName("SetAgentSkills");
    }

    private static async Task<Results<Ok<List<SkillSummaryResponse>>, ProblemHttpResult>> GetAsync(Guid id, AgentsDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Agents.AsNoTracking().AnyAsync(a => a.Id == id, cancellationToken))
        {
            return AgentNotFound.ToProblem();
        }

        var skills = await (
            from link in db.AgentSkills.AsNoTracking()
            join skill in SkillVisibility.For(db).AsNoTracking() on link.SkillId equals skill.Id
            where link.AgentId == id
            orderby skill.Slug
            select new SkillSummaryResponse(
                skill.Id,
                skill.Slug,
                skill.DisplayName,
                skill.Description,
                skill.Scope.ToString(),
                db.AgentSkills.Count(a => a.SkillId == skill.Id)))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(skills);
    }

    /// <summary>Replaces the assigned set in one save: what is not in the request is removed, the rest is added (AC-4).</summary>
    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> SetAsync(
        Guid id,
        SetAgentSkillsRequest request,
        AgentsDbContext db,
        CancellationToken cancellationToken)
    {
        if (await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken) is not { } agent)
        {
            return AgentNotFound.ToProblem();
        }

        var wanted = (request.SkillIds ?? []).Distinct().ToList();
        var skills = await SkillVisibility.For(db).AsNoTracking().Where(s => wanted.Contains(s.Id)).ToListAsync(cancellationToken);
        if (skills.Count != wanted.Count)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["skillIds"] = ["A skill does not exist or is not visible to this tenant."],
            });
        }

        // A skill must be visible at least as widely as the agent that holds it (D-13): a Global agent accepts only Global skills.
        var unfit = skills
            .Where(s => !AgentRules.IsVisibleAsWidelyAs(s.Scope, s.ScopeTenantIds, agent.Scope, agent.ScopeTenantIds))
            .Select(s => s.Slug)
            .ToList();
        if (unfit.Count > 0)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["skillIds"] = [$"The agent is visible to more tenants than these skills: {string.Join(", ", unfit)}."],
            });
        }

        var current = await db.AgentSkills.Where(a => a.AgentId == id).ToListAsync(cancellationToken);
        db.AgentSkills.RemoveRange(current.Where(a => !wanted.Contains(a.SkillId)));
        db.AgentSkills.AddRange(wanted
            .Where(skillId => current.All(a => a.SkillId != skillId))
            .Select(skillId => AgentSkill.Create(agent.TenantId, id, skillId)));

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}

/// <param name="SkillIds">The skills the agent has after the call; the list replaces the previous one.</param>
internal sealed record SetAgentSkillsRequest(List<Guid>? SkillIds);
