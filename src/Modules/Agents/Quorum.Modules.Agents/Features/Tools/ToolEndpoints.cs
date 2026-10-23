using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.Agents.Tools;
using Quorum.Shared.Results;
using Quorum.Shared.Endpoints;

namespace Quorum.Modules.Agents.Features.Tools;

/// <summary>The tools of the tenant, built-in and from MCP servers: the admin enables, disables and sets the policy of each.</summary>
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

    private static async Task<Results<Ok<List<ToolResponse>>, ProblemHttpResult>> ListAsync(HttpRequest request, AgentsDbContext db, CancellationToken cancellationToken)
    {
        var query = ListQuery.Parse(request.Query);
        if (!query.IsSuccess)
        {
            return query.Error.ToProblem();
        }

        // The tools are few and kept whole: only the state and the search narrow them, so a scope is refused instead of ignored.
        if (query.Value.Scope.Count + query.Value.Tenant.Count + query.Value.Assistant.Count + query.Value.Type.Count > 0)
        {
            return ScopeFilter.NotForTools.ToProblem();
        }

        var statused = ScopeFilter.Status(ToolVisibility.Tools(db).AsNoTracking(), query.Value, t => t.Tool.State);
        if (!statused.IsSuccess)
        {
            return statused.Error.ToProblem();
        }

        var tools = await ScopeFilter.Search(statused.Value, query.Value, t => t.Tool.Name, t => t.Tool.Description)
            .OrderBy(t => t.Tool.Origin).ThenBy(t => t.ServerName).ThenBy(t => t.Tool.Name)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(tools.Select(t => ToolResponse.From(t.Tool, t.Server, db.CurrentTenantId)).ToList());
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

/// <param name="Annotations">What the server claims about the tool (<c>readOnlyHint</c>...): information for the admin, it never changes the policy.</param>
/// <param name="ServerId">Only for a tool of an MCP server.</param>
/// <param name="ServerScope">The scope of that server, for the badge: a tool is shared the way its server is.</param>
/// <param name="Shared"><see langword="true"/> for a tool of another tenant's server made visible by its scope: read-only here.</param>
internal sealed record ToolResponse(
    Guid Id,
    string Origin,
    string? ServerName,
    string Name,
    string Description,
    string State,
    string Policy,
    JsonElement? Annotations,
    Guid? ServerId = null,
    ScopeResponse? ServerScope = null,
    bool Shared = false)
{
    public static ToolResponse From(Tool tool, McpServer? server, Guid? currentTenantId) => new(
        tool.Id,
        tool.Origin.ToString(),
        server?.Name,
        tool.Name,
        tool.Description,
        tool.State.ToString(),
        tool.Policy.ToString(),
        Json.Parse(tool.Annotations),
        tool.ServerId,
        server is null ? null : new ScopeResponse(server.Scope.ToString(), tool.TenantId == currentTenantId ? server.ScopeTenantIds : [], tool.TenantId == currentTenantId ? server.ScopeAssistantIds : []),
        tool.TenantId != currentTenantId);
}

internal sealed record UpdateToolRequest(string? State, string? Policy);
