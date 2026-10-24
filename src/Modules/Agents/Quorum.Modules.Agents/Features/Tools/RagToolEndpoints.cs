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
using Quorum.Modules.Agents.Tools.Rag;
using Quorum.Modules.KnowledgeBases.Contracts;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Persistence;
using Quorum.Shared.Results;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.Agents.Features.Tools;

/// <summary>
/// The rag tools of the tenant (spec 003 D-4): a tool that searches knowledge bases, configured by an administrator. They are tools
/// like the others (they appear in <c>GET /api/tools</c>, are activated per assistant and audited); this is where their binding is edited.
/// </summary>
internal sealed partial class RagToolEndpoints : IEndpointGroup
{
    private static readonly Error NotFound = Error.NotFound("rag.not_found", "The rag tool does not exist.");

    private static readonly Error NameTaken = Error.Conflict("rag.name_taken", "Another rag tool already has this name.");

    public void Map(IEndpointRouteBuilder app)
    {
        var rag = app.MapGroup("/api/tools/rag")
            .WithTags("Rag tools")
            .RequireAuthorization(QuorumPolicies.ToolsManage);

        rag.MapGet("/", ListAsync).WithName("ListRagTools");
        rag.MapPost("/", CreateAsync).WithName("CreateRagTool");
        rag.MapGet("/{id:guid}", GetAsync).WithName("GetRagTool");
        rag.MapGet("/{id:guid}/live-warnings", LiveWarningsAsync).WithName("GetRagToolLiveWarnings");
        rag.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateRagTool");
        rag.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteRagTool");
    }

    private static async Task<Results<Ok<Page<RagToolListItem>>, ProblemHttpResult>> ListAsync(
        HttpRequest request,
        AgentsDbContext db,
        ITenantContext tenant,
        IKnowledgeBaseCatalog catalog,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Authorized requests have a tenant.");
        var query = ListQuery.Parse(request.Query);
        if (!query.IsSuccess)
        {
            return query.Error.ToProblem();
        }

        var filtered = ScopeFilter.Apply(
            ToolVisibility.RagTools(db).AsNoTracking(),
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

        var page = await filtered.Value.ToPageAsync(query.Value, "rag-tools", t => t.Tool.Name, t => t.Tool.Id, t => t, cancellationToken);
        if (!page.IsSuccess)
        {
            return page.Error.ToProblem();
        }

        // Knowledge bases are named as the tenant sees them: one it cannot see is absent, and a shared tool shows none of its binding.
        var own = page.Value.Items.Where(t => t.Tool.TenantId == tenantId).ToList();
        var seen = await catalog.DescribeAsync(
            new BindingQuery(
                tenantId,
                AssistantId: null,
                [.. own.SelectMany(t => t.Binding.KnowledgeBaseIds).Distinct()],
                [.. own.SelectMany(t => t.Binding.ExposedFieldIds.Concat(t.Binding.FixedFilterFieldIds)).Distinct()]),
            cancellationToken);
        var byId = seen.KnowledgeBases.ToDictionary(k => k.Id);

        return TypedResults.Ok(new Page<RagToolListItem>(
            [.. page.Value.Items.Select(t => t.Tool.TenantId == tenantId
                ? RagToolListItem.ForOwner(t, [.. t.Binding.KnowledgeBaseIds.Where(byId.ContainsKey).Select(id => new RagKnowledgeBase(id, byId[id].Name))], RagToolWarnings.Evaluate(t.Binding, seen))
                : RagToolListItem.ForOther(t))],
            page.Value.Cursor));
    }

    private static async Task<Results<Ok<RagToolDetail>, ProblemHttpResult>> GetAsync(
        Guid id,
        bool? live,
        AgentsDbContext db,
        ITenantContext tenant,
        RagToolResolver resolver,
        IKnowledgeBaseCatalog catalog,
        IKnowledgeBaseLookups lookups,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Authorized requests have a tenant.");

        // The tenant filter makes a tool shared by another tenant not found here: its binding is not for this tenant to read.
        var view = await (
            from tool in db.Tools
            join binding in db.RagTools on tool.Id equals binding.ToolId
            where tool.Id == id
            select new RagToolView { Tool = tool, Binding = binding })
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        if (view is null)
        {
            return NotFound.ToProblem();
        }

        var seen = await catalog.DescribeAsync(
            new BindingQuery(
                tenantId,
                AssistantId: null,
                view.Binding.KnowledgeBaseIds,
                [.. view.Binding.ExposedFieldIds.Concat(view.Binding.FixedFilterFieldIds).Distinct()]),
            cancellationToken);

        return TypedResults.Ok(RagToolDetail.From(
            view.Tool,
            view.Binding,
            await PreviewAsync(resolver, tenantId, view.Tool, view.Binding, cancellationToken),
            [
                .. RagToolWarnings.Evaluate(view.Binding, seen),
                .. (live == false ? [] : await RagToolWarnings.UnindexedDocumentIdAsync(lookups, tenantId, view.Binding, cancellationToken)),
            ],
            seen));
    }

    /// <summary>
    /// What only the store can tell (an unindexed field): it asks the store, which can take seconds, so the editor reads the tool
    /// without it (<c>live=false</c>) and asks for this after it is on screen.
    /// </summary>
    private static async Task<Results<Ok<IReadOnlyList<ToolWarning>>, ProblemHttpResult>> LiveWarningsAsync(
        Guid id,
        AgentsDbContext db,
        ITenantContext tenant,
        IKnowledgeBaseLookups lookups,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Authorized requests have a tenant.");
        var binding = await db.RagTools.AsNoTracking().SingleOrDefaultAsync(b => b.ToolId == id, cancellationToken);
        if (binding is null)
        {
            return NotFound.ToProblem();
        }

        return TypedResults.Ok(await RagToolWarnings.UnindexedDocumentIdAsync(lookups, tenantId, binding, cancellationToken));
    }

    private static async Task<Results<CreatedAtRoute<CreatedRagTool>, ProblemHttpResult>> CreateAsync(
        RagToolRequest request,
        AgentsDbContext db,
        ITenantContext tenant,
        RagToolValidation validation,
        RagToolResolver resolver,
        TimeProvider clock,
        ILogger<RagToolEndpoints> logger,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Authorized requests have a tenant.");

        var valid = await validation.ValidateAsync(request, tenantId, cancellationToken);
        if (!valid.IsSuccess)
        {
            return valid.Error.ToProblem();
        }

        var input = valid.Value;
        if (await db.Tools.AnyAsync(t => t.Origin == ToolOrigin.Rag && t.Name == input.Name, cancellationToken))
        {
            return NameTaken.ToProblem();
        }

        var tool = Tool.CreateRag(
            tenantId,
            input.Name,
            input.Description,
            RagToolHash.Compute(input.Name, input.Description, input.Configuration),
            input.Policy ?? ToolPolicy.Auto);
        var binding = RagTool.Create(tool.Id, tenantId, input.Configuration, clock.GetUtcNow());
        db.Tools.Add(tool);
        db.RagTools.Add(binding);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            return NameTaken.ToProblem();
        }

        LogSaved(logger, tool.Id, binding.KnowledgeBaseIds.Count, binding.ExposedFieldIds.Count, "created");
        return TypedResults.CreatedAtRoute(
            new CreatedRagTool(tool.Id, await PreviewAsync(resolver, tenantId, tool, binding, cancellationToken)),
            "GetRagTool",
            new RouteValueDictionary { ["id"] = tool.Id });
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> UpdateAsync(
        Guid id,
        RagToolRequest request,
        AgentsDbContext db,
        ITenantContext tenant,
        RagToolValidation validation,
        TimeProvider clock,
        ILogger<RagToolEndpoints> logger,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Authorized requests have a tenant.");

        var tool = await db.Tools.SingleOrDefaultAsync(t => t.Id == id && t.Origin == ToolOrigin.Rag, cancellationToken);
        var binding = tool is null ? null : await db.RagTools.SingleOrDefaultAsync(r => r.ToolId == id, cancellationToken);
        if (tool is null || binding is null)
        {
            return NotFound.ToProblem();
        }

        var valid = await validation.ValidateAsync(request, tenantId, cancellationToken, documentIdsWhenOmitted: binding.AllowDocumentIds);
        if (!valid.IsSuccess)
        {
            return valid.Error.ToProblem();
        }

        var input = valid.Value;
        if (await db.Tools.AnyAsync(t => t.Origin == ToolOrigin.Rag && t.Name == input.Name && t.Id != id, cancellationToken))
        {
            return NameTaken.ToProblem();
        }

        // The hash changes with the binding: a call that was waiting on the old one is refused when the user answers.
        tool.Reconfigure(input.Name, input.Description, RagToolHash.Compute(input.Name, input.Description, input.Configuration), input.Policy ?? tool.Policy);
        binding.Apply(input.Configuration, clock.GetUtcNow());

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            return NameTaken.ToProblem();
        }

        LogSaved(logger, id, binding.KnowledgeBaseIds.Count, binding.ExposedFieldIds.Count, "updated");
        return TypedResults.NoContent();
    }

    /// <summary>Removes the tool with its binding and its activations. Audit entries keep the names they recorded.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(Guid id, AgentsDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Tools.SingleOrDefaultAsync(t => t.Id == id && t.Origin == ToolOrigin.Rag, cancellationToken) is not { } tool)
        {
            return NotFound.ToProblem();
        }

        db.Tools.Remove(tool);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>The definition the model would get now, as an administrator sees it; <see langword="null"/> when no knowledge base of the tool is visible.</summary>
    private static async Task<RagToolDefinitionPreview?> PreviewAsync(RagToolResolver resolver, Guid tenantId, Tool tool, RagTool binding, CancellationToken cancellationToken)
    {
        var resolved = await resolver.ResolveAsync(
            tenantId,
            assistantId: null,
            [new RagToolToDescribe(tool.Id, tool.Description, RagBinding.From(binding))],
            cancellationToken);
        if (!resolved.TryGetValue(tool.Id, out var found))
        {
            return null;
        }

        var exposedName = ToolNaming.Rag(tool.Name);
        var characters = ToolDefinitionRenderer.Length([new ToolDefinition(exposedName, found.Definition.Description, found.Definition.InputSchema)]);
        using var schema = JsonDocument.Parse(found.Definition.InputSchema);
        return new RagToolDefinitionPreview(
            exposedName,
            found.Definition.Description,
            schema.RootElement.Clone(),
            (characters + ContextUsage.CharactersPerToken - 1) / ContextUsage.CharactersPerToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Rag tool {ToolId} {Action} with {KnowledgeBaseCount} knowledge bases and {FieldCount} exposed fields")]
    private static partial void LogSaved(ILogger logger, Guid toolId, int knowledgeBaseCount, int fieldCount, string action);
}

internal sealed record RagKnowledgeBase(Guid Id, string Name);

/// <param name="Shared"><see langword="true"/> for a tool of another tenant made visible by its scope: read-only, and its binding is not shown.</param>
internal sealed record RagToolListItem(
    Guid Id,
    string Name,
    IReadOnlyList<RagKnowledgeBase> KnowledgeBases,
    ScopeResponse Scope,
    string State,
    string Policy,
    bool Shared,
    IReadOnlyList<ToolWarning> Warnings)
{
    public static RagToolListItem ForOwner(RagToolView view, IReadOnlyList<RagKnowledgeBase> knowledgeBases, IReadOnlyList<ToolWarning> warnings) => new(
        view.Tool.Id,
        view.Tool.Name,
        knowledgeBases,
        new ScopeResponse(view.Binding.Scope.ToString(), view.Binding.ScopeTenantIds, view.Binding.ScopeAssistantIds),
        view.Tool.State.ToString(),
        view.Tool.Policy.ToString(),
        Shared: false,
        warnings);

    public static RagToolListItem ForOther(RagToolView view) => new(
        view.Tool.Id,
        view.Tool.Name,
        [],
        new ScopeResponse(view.Binding.Scope.ToString(), [], []),
        view.Tool.State.ToString(),
        view.Tool.Policy.ToString(),
        Shared: true,
        []);
}

/// <param name="Name">The name the model sees: <c>rag__&lt;name&gt;</c>.</param>
/// <param name="EstimatedTokens">What the definition takes of the context, the figure the context gauge counts.</param>
internal sealed record RagToolDefinitionPreview(string Name, string Description, JsonElement InputSchema, int EstimatedTokens);

internal sealed record CreatedRagTool(Guid Id, RagToolDefinitionPreview? Definition);

internal sealed record RagToolDetail(
    Guid Id,
    string Name,
    string Description,
    IReadOnlyList<Guid> KnowledgeBaseIds,
    IReadOnlyList<ExposedFieldDetail> ExposedFields,
    JsonElement FixedFilters,
    int TopK,
    double? ScoreThreshold,
    int MaxResultCharacters,
    ScopeResponse Scope,
    string State,
    string Policy,
    RagToolDefinitionPreview? Definition,
    IReadOnlyList<ToolWarning> Warnings,
    bool AllowDocumentIds)
{
    public static RagToolDetail From(Tool tool, RagTool binding, RagToolDefinitionPreview? definition, IReadOnlyList<ToolWarning> warnings, BindingDescription seen)
    {
        using var filters = JsonDocument.Parse(binding.FixedFilters);
        return new RagToolDetail(
            tool.Id,
            tool.Name,
            tool.Description,
            binding.KnowledgeBaseIds,
            ExposedFieldDetail.ListOf(binding, seen),
            filters.RootElement.Clone(),
            binding.TopK,
            binding.ScoreThreshold,
            binding.MaxResultCharacters,
            new ScopeResponse(binding.Scope.ToString(), binding.ScopeTenantIds, binding.ScopeAssistantIds),
            tool.State.ToString(),
            tool.Policy.ToString(),
            definition,
            warnings,
            binding.AllowDocumentIds);
    }
}

/// <param name="Alias">The parameter name the model sees: the alias given to the field, or its own name; <see langword="null"/> when the field is no longer visible.</param>
internal sealed record ExposedFieldDetail(Guid FieldId, string? Alias)
{
    public static List<ExposedFieldDetail> ListOf(RagTool binding, BindingDescription seen)
    {
        var aliases = RagBinding.From(binding).ExposedAliases;
        return
        [
            .. binding.ExposedFieldIds.Select(id => new ExposedFieldDetail(
                id,
                aliases.GetValueOrDefault(id) ?? seen.Fields.FirstOrDefault(f => f.Id == id)?.Name)),
        ];
    }
}
