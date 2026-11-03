using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Agents.Persistence;
using Quorum.Shared.Endpoints;

namespace Quorum.Modules.Agents.Features.Agents;

/// <summary>The agents of the tenant (spec 013). Read only for now: the pickers of the tool scopes need the list.</summary>
internal sealed class AgentEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder app)
    {
        var agents = app.MapGroup("/api/agents")
            .WithTags("Agents")
            .RequireAuthorization(QuorumPolicies.AgentsManage);

        agents.MapGet("/", ListAsync).WithName("ListAgents");
    }

    private static async Task<Ok<List<AgentSummaryResponse>>> ListAsync(AgentsDbContext db, CancellationToken cancellationToken)
    {
        var agents = await db.Agents
            .AsNoTracking()
            .OrderBy(a => a.DisplayName)
            .ThenBy(a => a.Id)
            .Select(a => new AgentSummaryResponse(
                a.Id,
                a.Slug,
                a.DisplayName,
                a.Description,
                a.Scope.ToString(),
                a.ModelEndpointId,
                db.AgentSubAgents.Count(e => e.ParentAgentId == a.Id),
                db.AgentTools.Count(t => t.AgentId == a.Id)))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(agents);
    }
}

internal sealed record AgentSummaryResponse(
    Guid Id,
    string Slug,
    string DisplayName,
    string Description,
    string Scope,
    Guid? ModelEndpointId,
    int SubAgentCount,
    int ToolCount);
