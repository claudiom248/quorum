using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.Agents.Tools;
using Quorum.Modules.Agents.Tools.WebSearch;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Persistence;
using Quorum.Shared.Results;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.Agents.Features.Tools;

/// <summary>
/// The web search tools of the tenant (spec 005): a tool that asks the platform's SearXNG instance, configured by an administrator.
/// Like the rag and lookup tools they appear in <c>GET /api/tools</c>, are activated per assistant and audited; this is where their
/// binding is edited. None can be saved while the platform has no instance to search with (AC-7).
/// </summary>
internal sealed partial class WebSearchToolEndpoints : IEndpointGroup
{
    private static readonly Error NotFound = Error.NotFound("web_search.not_found", "The web search tool does not exist.");

    private static readonly Error NameTaken = Error.Conflict("web_search.name_taken", "Another web search tool already has this name.");

    public void Map(IEndpointRouteBuilder app)
    {
        var tools = app.MapGroup("/api/tools/web-search")
            .WithTags("Web search tools")
            .RequireAuthorization(QuorumPolicies.ToolsManage);

        tools.MapGet("/", ListAsync).WithName("ListWebSearchTools");
        tools.MapPost("/", CreateAsync).WithName("CreateWebSearchTool");
        tools.MapGet("/{id:guid}", GetAsync).WithName("GetWebSearchTool");
        tools.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateWebSearchTool");
        tools.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteWebSearchTool");
    }

    private static async Task<Results<Ok<Page<WebSearchToolListItem>>, ProblemHttpResult>> ListAsync(
        HttpRequest request,
        AgentsDbContext db,
        CancellationToken cancellationToken)
    {
        var query = ListQuery.Parse(request.Query);
        if (!query.IsSuccess)
        {
            return query.Error.ToProblem();
        }

        var filtered = ScopeFilter.Apply(
            ToolVisibility.WebSearchTools(db).AsNoTracking(),
            query.Value,
            v => v.Binding.Scope,
            v => v.Tool.TenantId,
            v => v.Binding.ScopeTenantIds,
            v => v.Binding.ScopeAssistantIds,
            v => v.Tool.State,
            v => v.Tool.Name,
            v => v.Tool.Description);
        if (!filtered.IsSuccess)
        {
            return filtered.Error.ToProblem();
        }

        var page = await filtered.Value.ToPageAsync(query.Value, "web-search-tools", t => t.Tool.Name, t => t.Tool.Id, t => t, cancellationToken);
        if (!page.IsSuccess)
        {
            return page.Error.ToProblem();
        }

        return TypedResults.Ok(new Page<WebSearchToolListItem>(
            [.. page.Value.Items.Select(t => t.Tool.TenantId == db.CurrentTenantId
                ? WebSearchToolListItem.ForOwner(t)
                : WebSearchToolListItem.ForOther(t))],
            page.Value.Cursor));
    }

    private static async Task<Results<Ok<WebSearchToolDetail>, ProblemHttpResult>> GetAsync(
        Guid id,
        AgentsDbContext db,
        CancellationToken cancellationToken)
    {
        // The tenant filter makes a tool shared by another tenant not found here: its binding is not for this tenant to read.
        var view = await (
            from tool in db.Tools
            join binding in db.WebSearchTools on tool.Id equals binding.ToolId
            where tool.Id == id
            select new WebSearchToolView { Tool = tool, Binding = binding })
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        if (view is null)
        {
            return NotFound.ToProblem();
        }

        return TypedResults.Ok(WebSearchToolDetail.From(view.Tool, view.Binding));
    }

    private static async Task<Results<CreatedAtRoute<CreatedWebSearchTool>, ProblemHttpResult>> CreateAsync(
        WebSearchToolRequest request,
        AgentsDbContext db,
        ITenantContext tenant,
        WebSearchToolValidation validation,
        TimeProvider clock,
        ILogger<WebSearchToolEndpoints> logger,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Authorized requests have a tenant.");

        var valid = await validation.ValidateAsync(request, tenantId, cancellationToken);
        if (!valid.IsSuccess)
        {
            return valid.Error.ToProblem();
        }

        var input = valid.Value;
        if (await db.Tools.AnyAsync(t => t.Origin == ToolOrigin.WebSearch && t.Name == input.Name, cancellationToken))
        {
            return NameTaken.ToProblem();
        }

        var tool = Tool.CreateWebSearch(
            tenantId,
            input.Name,
            input.Description,
            WebSearchToolHash.Compute(input.Name, input.Description, input.Configuration),
            input.Policy ?? ToolPolicy.Auto);
        var binding = WebSearchTool.Create(tool.Id, tenantId, input.Configuration, clock.GetUtcNow());
        db.Tools.Add(tool);
        db.WebSearchTools.Add(binding);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            return NameTaken.ToProblem();
        }

        LogSaved(logger, tool.Id, binding.TopN, "created");
        return TypedResults.CreatedAtRoute(
            new CreatedWebSearchTool(tool.Id, Preview(tool, binding)),
            "GetWebSearchTool",
            new RouteValueDictionary { ["id"] = tool.Id });
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> UpdateAsync(
        Guid id,
        WebSearchToolRequest request,
        AgentsDbContext db,
        ITenantContext tenant,
        WebSearchToolValidation validation,
        TimeProvider clock,
        ILogger<WebSearchToolEndpoints> logger,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Authorized requests have a tenant.");

        var tool = await db.Tools.SingleOrDefaultAsync(t => t.Id == id && t.Origin == ToolOrigin.WebSearch, cancellationToken);
        var binding = tool is null ? null : await db.WebSearchTools.SingleOrDefaultAsync(w => w.ToolId == id, cancellationToken);
        if (tool is null || binding is null)
        {
            return NotFound.ToProblem();
        }

        var valid = await validation.ValidateAsync(request, tenantId, cancellationToken);
        if (!valid.IsSuccess)
        {
            return valid.Error.ToProblem();
        }

        var input = valid.Value;
        if (await db.Tools.AnyAsync(t => t.Origin == ToolOrigin.WebSearch && t.Name == input.Name && t.Id != id, cancellationToken))
        {
            return NameTaken.ToProblem();
        }

        // The hash changes with the binding: a call that was waiting on the old one is refused when the user answers.
        tool.Reconfigure(input.Name, input.Description, WebSearchToolHash.Compute(input.Name, input.Description, input.Configuration), input.Policy ?? tool.Policy);
        binding.Apply(input.Configuration, clock.GetUtcNow());

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            return NameTaken.ToProblem();
        }

        LogSaved(logger, id, binding.TopN, "updated");
        return TypedResults.NoContent();
    }

    /// <summary>Removes the tool with its binding and its activations. Audit entries keep the names they recorded.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(Guid id, AgentsDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Tools.SingleOrDefaultAsync(t => t.Id == id && t.Origin == ToolOrigin.WebSearch, cancellationToken) is not { } tool)
        {
            return NotFound.ToProblem();
        }

        db.Tools.Remove(tool);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>The definition the model would get now: it depends only on the stored binding, unlike a rag tool's.</summary>
    internal static WebSearchToolDefinitionPreview Preview(Tool tool, WebSearchTool binding)
    {
        var generated = WebSearchToolDefinition.Build(tool.Description, binding.TopN);
        var exposedName = ToolNaming.WebSearch(tool.Name);
        var characters = ToolDefinitionRenderer.Length([new ToolDefinition(exposedName, generated.Description, generated.InputSchema)]);
        using var schema = JsonDocument.Parse(generated.InputSchema);
        return new WebSearchToolDefinitionPreview(
            exposedName,
            generated.Description,
            schema.RootElement.Clone(),
            (characters + ContextUsage.CharactersPerToken - 1) / ContextUsage.CharactersPerToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Web search tool {ToolId} {Action} with {TopN} results")]
    private static partial void LogSaved(ILogger logger, Guid toolId, int topN, string action);
}

/// <param name="Name">The name the model sees: <c>web_search__&lt;name&gt;</c>.</param>
/// <param name="EstimatedTokens">What the definition takes of the context, the figure the context gauge counts.</param>
internal sealed record WebSearchToolDefinitionPreview(string Name, string Description, JsonElement InputSchema, int EstimatedTokens);

/// <param name="Shared"><see langword="true"/> for a tool of another tenant made visible by its scope: read-only, and its binding is not shown.</param>
internal sealed record WebSearchToolListItem(
    Guid Id,
    string Name,
    string Description,
    ScopeResponse Scope,
    string State,
    string Policy,
    bool Shared)
{
    public static WebSearchToolListItem ForOwner(WebSearchToolView view) => new(
        view.Tool.Id,
        view.Tool.Name,
        view.Tool.Description,
        new ScopeResponse(view.Binding.Scope.ToString(), view.Binding.ScopeTenantIds, view.Binding.ScopeAssistantIds),
        view.Tool.State.ToString(),
        view.Tool.Policy.ToString(),
        Shared: false);

    public static WebSearchToolListItem ForOther(WebSearchToolView view) => new(
        view.Tool.Id,
        view.Tool.Name,
        view.Tool.Description,
        new ScopeResponse(view.Binding.Scope.ToString(), [], []),
        view.Tool.State.ToString(),
        view.Tool.Policy.ToString(),
        Shared: true);
}

internal sealed record CreatedWebSearchTool(Guid Id, WebSearchToolDefinitionPreview Definition);

internal sealed record WebSearchToolDetail(
    Guid Id,
    string Name,
    string Description,
    int TopN,
    IReadOnlyList<string>? SearchEngines,
    string? Language,
    int SafeSearch,
    string? TimeRange,
    int MaxInvocationsPerConversation,
    ScopeResponse Scope,
    string State,
    string Policy,
    WebSearchToolDefinitionPreview Definition)
{
    public static WebSearchToolDetail From(Tool tool, WebSearchTool binding) => new(
        tool.Id,
        tool.Name,
        tool.Description,
        binding.TopN,
        binding.SearchEngines,
        binding.Language,
        binding.SafeSearch,
        binding.TimeRange,
        binding.MaxInvocationsPerConversation,
        new ScopeResponse(binding.Scope.ToString(), binding.ScopeTenantIds, binding.ScopeAssistantIds),
        tool.State.ToString(),
        tool.Policy.ToString(),
        WebSearchToolEndpoints.Preview(tool, binding));
}
