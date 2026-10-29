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
using Quorum.Modules.Agents.Tools.WebFetch;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Persistence;
using Quorum.Shared.Results;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.Agents.Features.Tools;

/// <summary>
/// The web fetch tools of the tenant (spec 006): a tool that reads the content of the pages the model names, configured by an
/// administrator. Like the rag, lookup and web search tools they appear in <c>GET /api/tools</c>, are activated per assistant and
/// audited; this is where their binding is edited. Unlike a web search tool, none needs the platform to have anything set: the
/// defaults come from <see cref="WebConfiguration"/> and the tool can override them.
/// </summary>
internal sealed partial class WebFetchToolEndpoints : IEndpointGroup
{
    private static readonly Error NotFound = Error.NotFound("web_fetch.not_found", "The web fetch tool does not exist.");

    private static readonly Error NameTaken = Error.Conflict("web_fetch.name_taken", "Another web fetch tool already has this name.");

    public void Map(IEndpointRouteBuilder app)
    {
        var tools = app.MapGroup("/api/tools/web-fetch")
            .WithTags("Web fetch tools")
            .RequireAuthorization(QuorumPolicies.ToolsManage);

        tools.MapGet("/", ListAsync).WithName("ListWebFetchTools");
        tools.MapPost("/", CreateAsync).WithName("CreateWebFetchTool");
        tools.MapGet("/{id:guid}", GetAsync).WithName("GetWebFetchTool");
        tools.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateWebFetchTool");
        tools.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteWebFetchTool");
    }

    private static async Task<Results<Ok<Page<WebFetchToolListItem>>, ProblemHttpResult>> ListAsync(
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
            ToolVisibility.WebFetchTools(db).AsNoTracking(),
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

        var page = await filtered.Value.ToPageAsync(query.Value, "web-fetch-tools", t => t.Tool.Name, t => t.Tool.Id, t => t, cancellationToken);
        if (!page.IsSuccess)
        {
            return page.Error.ToProblem();
        }

        return TypedResults.Ok(new Page<WebFetchToolListItem>(
            [.. page.Value.Items.Select(t => t.Tool.TenantId == db.CurrentTenantId
                ? WebFetchToolListItem.ForOwner(t)
                : WebFetchToolListItem.ForOther(t))],
            page.Value.Cursor));
    }

    private static async Task<Results<Ok<WebFetchToolDetail>, ProblemHttpResult>> GetAsync(
        Guid id,
        AgentsDbContext db,
        CancellationToken cancellationToken)
    {
        // The tenant filter makes a tool shared by another tenant not found here: its binding is not for this tenant to read.
        var view = await (
            from tool in db.Tools
            join binding in db.WebFetchTools on tool.Id equals binding.ToolId
            where tool.Id == id
            select new WebFetchToolView { Tool = tool, Binding = binding })
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        if (view is null)
        {
            return NotFound.ToProblem();
        }

        return TypedResults.Ok(WebFetchToolDetail.From(view.Tool, view.Binding));
    }

    private static async Task<Results<CreatedAtRoute<CreatedWebFetchTool>, ProblemHttpResult>> CreateAsync(
        WebFetchToolRequest request,
        AgentsDbContext db,
        ITenantContext tenant,
        WebFetchToolValidation validation,
        TimeProvider clock,
        ILogger<WebFetchToolEndpoints> logger,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Authorized requests have a tenant.");

        var valid = await validation.ValidateAsync(request, tenantId, cancellationToken);
        if (!valid.IsSuccess)
        {
            return valid.Error.ToProblem();
        }

        var input = valid.Value;
        if (await db.Tools.AnyAsync(t => t.Origin == ToolOrigin.WebFetch && t.Name == input.Name, cancellationToken))
        {
            return NameTaken.ToProblem();
        }

        var tool = Tool.CreateWebFetch(
            tenantId,
            input.Name,
            input.Description,
            WebFetchToolHash.Compute(input.Name, input.Description, input.Configuration),
            input.Policy ?? ToolPolicy.Auto);
        var binding = WebFetchTool.Create(tool.Id, tenantId, input.Configuration, clock.GetUtcNow());
        db.Tools.Add(tool);
        db.WebFetchTools.Add(binding);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            return NameTaken.ToProblem();
        }

        LogSaved(logger, tool.Id, binding.MaxUrlsPerInvocation, "created");
        return TypedResults.CreatedAtRoute(
            new CreatedWebFetchTool(tool.Id, Preview(tool, binding)),
            "GetWebFetchTool",
            new RouteValueDictionary { ["id"] = tool.Id });
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> UpdateAsync(
        Guid id,
        WebFetchToolRequest request,
        AgentsDbContext db,
        ITenantContext tenant,
        WebFetchToolValidation validation,
        TimeProvider clock,
        ILogger<WebFetchToolEndpoints> logger,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Authorized requests have a tenant.");

        var tool = await db.Tools.SingleOrDefaultAsync(t => t.Id == id && t.Origin == ToolOrigin.WebFetch, cancellationToken);
        var binding = tool is null ? null : await db.WebFetchTools.SingleOrDefaultAsync(w => w.ToolId == id, cancellationToken);
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
        if (await db.Tools.AnyAsync(t => t.Origin == ToolOrigin.WebFetch && t.Name == input.Name && t.Id != id, cancellationToken))
        {
            return NameTaken.ToProblem();
        }

        // The hash changes with the binding: a call that was waiting on the old one is refused when the user answers.
        tool.Reconfigure(input.Name, input.Description, WebFetchToolHash.Compute(input.Name, input.Description, input.Configuration), input.Policy ?? tool.Policy);
        binding.Apply(input.Configuration, clock.GetUtcNow());

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            return NameTaken.ToProblem();
        }

        LogSaved(logger, id, binding.MaxUrlsPerInvocation, "updated");
        return TypedResults.NoContent();
    }

    /// <summary>Removes the tool with its binding and its activations. Audit entries keep the names they recorded.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(Guid id, AgentsDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Tools.SingleOrDefaultAsync(t => t.Id == id && t.Origin == ToolOrigin.WebFetch, cancellationToken) is not { } tool)
        {
            return NotFound.ToProblem();
        }

        db.Tools.Remove(tool);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>The definition the model would get now: it depends only on the stored binding, unlike a rag tool's.</summary>
    internal static WebFetchToolDefinitionPreview Preview(Tool tool, WebFetchTool binding)
    {
        var generated = WebFetchToolDefinition.Build(tool.Description, binding.MaxUrlsPerInvocation);
        var exposedName = ToolNaming.WebFetch(tool.Name);
        var characters = ToolDefinitionRenderer.Length([new ToolDefinition(exposedName, generated.Description, generated.InputSchema)]);
        using var schema = JsonDocument.Parse(generated.InputSchema);
        return new WebFetchToolDefinitionPreview(
            exposedName,
            generated.Description,
            schema.RootElement.Clone(),
            (characters + ContextUsage.CharactersPerToken - 1) / ContextUsage.CharactersPerToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Web fetch tool {ToolId} {Action} with {MaxUrls} URLs per call")]
    private static partial void LogSaved(ILogger logger, Guid toolId, int maxUrls, string action);
}

/// <param name="Name">The name the model sees: <c>web_fetch__&lt;name&gt;</c>.</param>
/// <param name="EstimatedTokens">What the definition takes of the context, the figure the context gauge counts.</param>
internal sealed record WebFetchToolDefinitionPreview(string Name, string Description, JsonElement InputSchema, int EstimatedTokens);

/// <param name="Shared"><see langword="true"/> for a tool of another tenant made visible by its scope: read-only, and its binding is not shown.</param>
internal sealed record WebFetchToolListItem(
    Guid Id,
    string Name,
    string Description,
    ScopeResponse Scope,
    string State,
    string Policy,
    bool Shared)
{
    public static WebFetchToolListItem ForOwner(WebFetchToolView view) => new(
        view.Tool.Id,
        view.Tool.Name,
        view.Tool.Description,
        new ScopeResponse(view.Binding.Scope.ToString(), view.Binding.ScopeTenantIds, view.Binding.ScopeAssistantIds),
        view.Tool.State.ToString(),
        view.Tool.Policy.ToString(),
        Shared: false);

    public static WebFetchToolListItem ForOther(WebFetchToolView view) => new(
        view.Tool.Id,
        view.Tool.Name,
        view.Tool.Description,
        new ScopeResponse(view.Binding.Scope.ToString(), [], []),
        view.Tool.State.ToString(),
        view.Tool.Policy.ToString(),
        Shared: true);
}

internal sealed record CreatedWebFetchTool(Guid Id, WebFetchToolDefinitionPreview Definition);

internal sealed record WebFetchToolDetail(
    Guid Id,
    string Name,
    string Description,
    int TokenBudgetPerPage,
    int TotalTokenBudget,
    int MaxUrlsPerInvocation,
    int Timeout,
    ScopeResponse Scope,
    string State,
    string Policy,
    WebFetchToolDefinitionPreview Definition)
{
    public static WebFetchToolDetail From(Tool tool, WebFetchTool binding) => new(
        tool.Id,
        tool.Name,
        tool.Description,
        binding.TokenBudgetPerPage,
        binding.TotalTokenBudget,
        binding.MaxUrlsPerInvocation,
        binding.Timeout,
        new ScopeResponse(binding.Scope.ToString(), binding.ScopeTenantIds, binding.ScopeAssistantIds),
        tool.State.ToString(),
        tool.Policy.ToString(),
        WebFetchToolEndpoints.Preview(tool, binding));
}
