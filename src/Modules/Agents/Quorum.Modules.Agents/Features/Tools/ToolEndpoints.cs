using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Shared.Endpoints;

namespace Quorum.Modules.Agents.Features.Tools;

/// <summary>The tools of the tenant, native and from MCP servers: the admin enables, disables and sets the policy of each.</summary>
internal sealed class ToolEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder app)
    {
        var tools = app.MapGroup("/api/tools")
            .WithTags("Tools")
            .RequireAuthorization(QuorumPolicies.ToolsManage);

        tools.MapGet("/", ListAsync).WithName("ListTools");
        tools.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateTool");
    }

    private static async Task<Ok<List<ToolResponse>>> ListAsync(AgentsDbContext db, CancellationToken cancellationToken)
    {
        var tools = await db.Tools
            .AsNoTracking()
            .OrderBy(t => t.Origin).ThenBy(t => t.Name)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(tools
            .Select(t => new ToolResponse(t.Id, t.Origin.ToString(), ServerName: null, t.Name, t.Description, t.State.ToString(), t.Policy.ToString()))
            .ToList());
    }

    /// <summary>A tool pending review is left by enabling or disabling it; nothing sets that state from outside.</summary>
    private static async Task<Results<NoContent, ValidationProblem, NotFound>> UpdateAsync(
        Guid id,
        UpdateToolRequest request,
        AgentsDbContext db,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (!TryParse<ToolState>(request.State, out var state) || state == ToolState.PendingReview)
        {
            errors["state"] = ["The state must be Enabled or Disabled."];
        }

        if (!TryParse<ToolPolicy>(request.Policy, out var policy))
        {
            errors["policy"] = ["The policy must be Auto or Confirm."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        if (await db.Tools.SingleOrDefaultAsync(t => t.Id == id, cancellationToken) is not { } tool)
        {
            return TypedResults.NotFound();
        }

        tool.Review(state, policy);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    // Enum.TryParse accepts numbers too ("5"); only the names are part of the API.
    private static bool TryParse<TEnum>(string? value, out TEnum result)
        where TEnum : struct, Enum =>
        Enum.TryParse(value, ignoreCase: true, out result) && Enum.IsDefined(result) && !int.TryParse(value, out _);
}

internal sealed record ToolResponse(Guid Id, string Origin, string? ServerName, string Name, string Description, string State, string Policy);

internal sealed record UpdateToolRequest(string? State, string? Policy);
