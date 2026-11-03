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

        // The tools are few and kept whole: only the state and the search narrow them, so a scope or a page is refused instead of ignored.
        if (query.Value.Scope.Count + query.Value.Tenant.Count + query.Value.Assistant.Count + query.Value.Agent.Count + query.Value.Type.Count > 0
            || request.Query.ContainsKey("limit") || request.Query.ContainsKey("cursor"))
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
        var name = request.Name?.Trim();
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

        // The name of a built-in tool is the administrator's to change like that of any other tool (spec 015 D-10). Omitted keeps it; an empty
        // name, or the default one, gives the tool back its default.
        if (name is not null)
        {
            if (tool.Origin != ToolOrigin.Builtin)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Only a built-in tool is renamed here: the others are named in their own form."] });
            }

            var custom = name.Length == 0 || string.Equals(name, tool.Name, StringComparison.Ordinal) ? null : name;
            if (custom is not null)
            {
                if (!ToolNameRules.IsValid(custom))
                {
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["name"] = [ToolNameRules.Description] });
                }

                if (await ToolNameRules.ClashAsync(db, ToolOrigin.Builtin, custom, tool.Id, cancellationToken) is { } clash)
                {
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["name"] = [clash.Description] });
                }
            }

            tool.Rename(custom);
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
/// <param name="Name">What the model and people read: for a built-in tool, the name the administrator gave it or else its default.</param>
/// <param name="DefaultName">The name a built-in tool comes with, which the administrator can go back to; <see langword="null"/> for the other tools.</param>
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
    bool Shared = false,
    string? DefaultName = null)
{
    public static ToolResponse From(Tool tool, McpServer? server, Guid? currentTenantId) => new(
        tool.Id,
        tool.Origin.ToString(),
        server?.Name,
        tool.ExposedName,
        tool.Description,
        tool.State.ToString(),
        tool.Policy.ToString(),
        Json.Parse(tool.Annotations),
        tool.ServerId,
        server is null ? null : new ScopeResponse(server.Scope.ToString(), tool.TenantId == currentTenantId ? server.ScopeTenantIds : [], tool.TenantId == currentTenantId ? server.ScopeAgentIds : []),
        tool.TenantId != currentTenantId,
        tool.Origin == ToolOrigin.Builtin ? tool.Name : null);
}

/// <param name="Name">For a built-in tool: its new name (spec 015 D-10). Omitted keeps the stored one, an empty string or the default name gives the default back.</param>
internal sealed record UpdateToolRequest(string? State, string? Policy, string? Name = null);
