using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Persistence;
using Quorum.Shared.Results;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.Agents.Features.Agents;

/// <summary>The agents of the tenant and the edges to their sub-agents, managed by the admin (spec 013 D-1, D-24, D-26).</summary>
internal sealed class AgentEndpoints : IEndpointGroup
{
    private static readonly Error NotFound = Error.NotFound("agent.not_found", "The agent does not exist.");

    public void Map(IEndpointRouteBuilder app)
    {
        var agents = app.MapGroup("/api/agents")
            .WithTags("Agents")
            .RequireAuthorization(QuorumPolicies.AgentsManage);

        agents.MapGet("/", ListAsync).WithName("ListAgents");
        agents.MapGet("/{id:guid}", GetAsync).WithName("GetAgent");
        agents.MapPost("/", CreateAsync).WithName("CreateAgent");
        agents.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateAgent");
        agents.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteAgent");
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

    private static async Task<Results<Ok<AgentDocument>, ProblemHttpResult>> GetAsync(Guid id, AgentsDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken) is not { } agent)
        {
            return NotFound.ToProblem();
        }

        return TypedResults.Ok(await AgentDocument.LoadAsync(db, agent, cancellationToken));
    }

    private static async Task<Results<CreatedAtRoute<AgentDocument>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        AgentRequest request,
        AgentsDbContext db,
        ITenantContext tenant,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Authorized requests have a tenant.");

        var input = await AgentInput.ValidateAsync(request, existing: null, db, cancellationToken);
        if (input.Errors.Count > 0)
        {
            return TypedResults.ValidationProblem(input.Errors);
        }

        var agent = Agent.Create(
            tenantId,
            input.Slug,
            input.DisplayName,
            input.Description,
            input.SystemPrompt,
            input.ModelEndpointId,
            input.Scope,
            input.ScopeTenantIds,
            clock.GetUtcNow());
        db.Agents.Add(agent);
        db.AgentSubAgents.AddRange(input.Edges.Select(e => AgentSubAgent.Create(tenantId, agent.Id, e.Child.Id, e.FailurePolicy)));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            return AgentInput.SlugTaken();
        }

        return TypedResults.CreatedAtRoute(
            await AgentDocument.LoadAsync(db, agent, cancellationToken),
            "GetAgent",
            new RouteValueDictionary { ["id"] = agent.Id });
    }

    /// <summary>Replaces the agent and its list of sub-agents in one save; a running turn keeps the definitions it started with (spec 013 AC-25).</summary>
    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid id,
        AgentRequest request,
        AgentsDbContext db,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (await db.Agents.SingleOrDefaultAsync(a => a.Id == id, cancellationToken) is not { } agent)
        {
            return NotFound.ToProblem();
        }

        var input = await AgentInput.ValidateAsync(request, agent, db, cancellationToken);
        if (input.Errors.Count > 0)
        {
            return TypedResults.ValidationProblem(input.Errors);
        }

        // Narrowing the scope must not leave an agent that calls this one, or an assistant of another tenant, without it (D-21, D-25).
        if (await AgentReferences.BrokenByScopeAsync(db, agent, input.Scope, input.ScopeTenantIds, cancellationToken) is { Count: > 0 } broken)
        {
            return BlockingReference.Conflict(
                "agent.scope_in_use",
                "The new scope would hide this agent from agents or assistants that use it. Change them first.",
                broken);
        }

        agent.Revise(input.Slug, input.DisplayName, input.Description, input.SystemPrompt, input.ModelEndpointId, input.Scope, input.ScopeTenantIds, clock.GetUtcNow());

        var current = await db.AgentSubAgents.Where(e => e.ParentAgentId == id).ToListAsync(cancellationToken);
        db.AgentSubAgents.RemoveRange(current.Where(e => input.Edges.All(w => w.Child.Id != e.ChildAgentId)));
        foreach (var wanted in input.Edges)
        {
            if (current.FirstOrDefault(e => e.ChildAgentId == wanted.Child.Id) is { } kept)
            {
                kept.ChangeFailurePolicy(wanted.FailurePolicy);
            }
            else
            {
                db.AgentSubAgents.Add(AgentSubAgent.Create(agent.TenantId, id, wanted.Child.Id, wanted.FailurePolicy));
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            return AgentInput.SlugTaken();
        }

        return TypedResults.NoContent();
    }

    /// <summary>Refused while an assistant has the agent as its root or another agent calls it (D-16); its own edges and tools go with it.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(Guid id, AgentsDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Agents.SingleOrDefaultAsync(a => a.Id == id, cancellationToken) is not { } agent)
        {
            return NotFound.ToProblem();
        }

        if (await AgentReferences.OfAsync(db, agent.Id, cancellationToken) is { Count: > 0 } references)
        {
            return BlockingReference.Conflict(
                "agent.in_use",
                "The agent is in use. Point these to another agent, or remove it from them, first.",
                references);
        }

        db.Agents.Remove(agent);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
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

/// <summary>The agent as the future declarative format will hold it: sub-agents by slug (spec 013 D-8, D-24).</summary>
internal sealed record AgentRequest(
    string? Slug,
    string? DisplayName,
    string? Description,
    string? SystemPrompt,
    Guid? ModelEndpointId,
    string? Scope,
    List<Guid>? ScopeTenantIds,
    List<SubAgentDocument>? SubAgents);

internal sealed record SubAgentDocument(string? Slug, string? FailurePolicy);

internal sealed record AgentDocument(
    Guid Id,
    string Slug,
    string DisplayName,
    string Description,
    string SystemPrompt,
    Guid? ModelEndpointId,
    string Scope,
    List<Guid> ScopeTenantIds,
    List<SubAgentDocument> SubAgents)
{
    public static async Task<AgentDocument> LoadAsync(AgentsDbContext db, Agent agent, CancellationToken cancellationToken)
    {
        var edges = await (
            from edge in db.AgentSubAgents.AsNoTracking()
            join child in db.Agents.AsNoTracking() on edge.ChildAgentId equals child.Id
            where edge.ParentAgentId == agent.Id
            orderby child.Slug
            select new SubAgentDocument(child.Slug, edge.FailurePolicy.ToString()))
            .ToListAsync(cancellationToken);

        return new AgentDocument(
            agent.Id,
            agent.Slug,
            agent.DisplayName,
            agent.Description,
            agent.SystemPrompt,
            agent.ModelEndpointId,
            agent.Scope.ToString(),
            agent.ScopeTenantIds,
            edges);
    }
}
