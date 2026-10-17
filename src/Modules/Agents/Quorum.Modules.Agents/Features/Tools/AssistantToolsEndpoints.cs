using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.Agents.Tools;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Results;

namespace Quorum.Modules.Agents.Features.Tools;

/// <summary>The assistants of the tenant and which tools of the pool each one has active.</summary>
internal sealed class AssistantToolsEndpoints : IEndpointGroup
{
    private static readonly Error AssistantNotFound = Error.NotFound("assistant.not_found", "The assistant does not exist.");

    private static readonly Error NotInPool = Error.Validation(
        "tool.not_in_pool",
        "Only enabled tools that are available to this assistant can be activated. Enable the tool first, or remove it from the request.");

    public void Map(IEndpointRouteBuilder app)
    {
        var assistants = app.MapGroup("/api/assistants").WithTags("Assistants");

        assistants.MapGet("/", ListAsync).RequireAuthorization().WithName("ListAssistants");
        assistants.MapGet("/{id:guid}/tools", GetToolsAsync).RequireAuthorization(QuorumPolicies.ToolsAttach).WithName("GetAssistantTools");
        assistants.MapPut("/{id:guid}/tools", SetToolsAsync).RequireAuthorization(QuorumPolicies.ToolsAttach).WithName("SetAssistantTools");
    }

    private static async Task<Ok<List<AssistantResponse>>> ListAsync(AgentsDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await db.Assistants
            .AsNoTracking()
            .OrderBy(a => a.Id)
            .Select(a => new AssistantResponse(a.Id, a.Name))
            .ToListAsync(cancellationToken));

    private static async Task<Results<Ok<AssistantToolsResponse>, ProblemHttpResult>> GetToolsAsync(
        Guid id,
        AgentsDbContext db,
        CancellationToken cancellationToken)
    {
        var budget = await (
            from assistant in db.Assistants
            join endpoint in db.ModelEndpoints on assistant.ModelEndpointId equals endpoint.Id
            where assistant.Id == id
            select (int?)endpoint.ContextTokenBudget)
            .SingleOrDefaultAsync(cancellationToken);
        if (budget is null)
        {
            return AssistantNotFound.ToProblem();
        }

        var pool = await ToolPool.For(db, id).AsNoTracking().ToListAsync(cancellationToken);
        var active = (await db.AssistantTools.Where(a => a.AssistantId == id).Select(a => a.ToolId).ToListAsync(cancellationToken)).ToHashSet();

        var activeDefinitions = pool.Where(t => active.Contains(t.Tool.Id)).Select(t => t.Definition).ToList();
        var warnings = ToolWarnings.Evaluate(
            activeDefinitions.Count,
            ToolDefinitionRenderer.Length(activeDefinitions),
            budget.Value * ContextUsage.CharactersPerToken);

        return TypedResults.Ok(new AssistantToolsResponse(
            pool.Select(t => new PoolToolResponse(t.Tool.Id, t.ExposedName, active.Contains(t.Tool.Id)))
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ToList(),
            warnings));
    }

    /// <summary>Replaces the active set: what is not in the request is deactivated.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> SetToolsAsync(
        Guid id,
        SetAssistantToolsRequest request,
        AgentsDbContext db,
        CancellationToken cancellationToken)
    {
        if (await db.Assistants.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken) is not { } assistant)
        {
            return AssistantNotFound.ToProblem();
        }

        var wanted = (request.ToolIds ?? []).Distinct().ToList();
        var inPool = await ToolPool.For(db, id).Where(t => wanted.Contains(t.Tool.Id)).Select(t => t.Tool.Id).ToListAsync(cancellationToken);
        if (inPool.Count != wanted.Count)
        {
            return NotInPool.ToProblem();
        }

        var current = await db.AssistantTools.Where(a => a.AssistantId == id).ToListAsync(cancellationToken);
        db.AssistantTools.RemoveRange(current.Where(a => !wanted.Contains(a.ToolId)));
        db.AssistantTools.AddRange(wanted
            .Where(toolId => current.All(a => a.ToolId != toolId))
            .Select(toolId => AssistantTool.Create(assistant.TenantId, id, toolId)));

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}

internal sealed record AssistantResponse(Guid Id, string Name);

internal sealed record PoolToolResponse(Guid ToolId, string Name, bool Active);

internal sealed record AssistantToolsResponse(List<PoolToolResponse> Pool, IReadOnlyList<ToolWarning> Warnings);

internal sealed record SetAssistantToolsRequest(List<Guid>? ToolIds);
