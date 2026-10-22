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
using Quorum.Modules.Agents.Tools.Lookup;
using Quorum.Modules.KnowledgeBases.Contracts;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Persistence;
using Quorum.Shared.Results;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.Agents.Features.Tools;

/// <summary>
/// The lookup tools of the tenant (spec 007 D-12): a tool that reads lists of values kept from knowledge bases, configured by an
/// administrator. They are tools like the others (they appear in <c>GET /api/tools</c>, are activated per assistant and audited); this
/// is where their binding is edited. Saving a tool tells the lookups which slices it needs, in the same transaction.
/// </summary>
internal sealed partial class LookupToolEndpoints : IEndpointGroup
{
    private static readonly Error NotFound = Error.NotFound("lookup_tool.not_found", "The lookup tool does not exist.");

    private static readonly Error NameTaken = Error.Conflict("lookup_tool.name_taken", "Another lookup tool already has this name.");

    public void Map(IEndpointRouteBuilder app)
    {
        var tools = app.MapGroup("/api/tools/lookup")
            .WithTags("Lookup tools")
            .RequireAuthorization(QuorumPolicies.ToolsManage);

        tools.MapGet("/", ListAsync).WithName("ListLookupTools");
        tools.MapPost("/", CreateAsync).WithName("CreateLookupTool");
        tools.MapGet("/{id:guid}", GetAsync).WithName("GetLookupTool");
        tools.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateLookupTool");
        tools.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteLookupTool");
    }

    private static async Task<Ok<List<LookupToolListItem>>> ListAsync(
        AgentsDbContext db,
        ITenantContext tenant,
        IKnowledgeBaseCatalog catalog,
        IKnowledgeBaseLookups lookups,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Authorized requests have a tenant.");
        var tools = await ToolVisibility.LookupTools(db).AsNoTracking().OrderBy(t => t.Tool.Name).ToListAsync(cancellationToken);

        // Lookups and knowledge bases are named as the tenant sees them: one it cannot see is absent, and a shared tool shows none of its binding.
        var own = tools.Where(t => t.Tool.TenantId == tenantId).ToList();
        var seenLookups = await lookups.ListAsync(new LookupListQuery(tenantId, AssistantId: null, [.. own.SelectMany(t => t.Binding.LookupIds).Distinct()]), cancellationToken);
        var seen = await catalog.DescribeAsync(
            new BindingQuery(
                tenantId,
                AssistantId: null,
                [.. own.SelectMany(t => t.Binding.KnowledgeBaseIds).Concat(seenLookups.Select(l => l.KnowledgeBaseId).OfType<Guid>()).Distinct()],
                [.. own.SelectMany(t => t.Binding.ExposedFieldIds.Concat(t.Binding.FixedFilterFieldIds)).Distinct()]),
            cancellationToken);
        var lookupsById = seenLookups.ToDictionary(l => l.Id);
        var basesById = seen.KnowledgeBases.ToDictionary(k => k.Id);

        return TypedResults.Ok(tools
            .Select(t => t.Tool.TenantId == tenantId
                ? LookupToolListItem.ForOwner(
                    t,
                    [.. t.Binding.LookupIds.Where(lookupsById.ContainsKey).Select(id => new NamedId(id, lookupsById[id].Name))],
                    [.. t.Binding.KnowledgeBaseIds.Where(basesById.ContainsKey).Select(id => new NamedId(id, basesById[id].Name))],
                    LookupToolWarnings.Evaluate(t.Binding, seenLookups, seen))
                : LookupToolListItem.ForOther(t))
            .ToList());
    }

    private static async Task<Results<Ok<LookupToolDetail>, ProblemHttpResult>> GetAsync(
        Guid id,
        AgentsDbContext db,
        ITenantContext tenant,
        LookupToolResolver resolver,
        IKnowledgeBaseCatalog catalog,
        IKnowledgeBaseLookups lookups,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Authorized requests have a tenant.");

        // The tenant filter makes a tool shared by another tenant not found here: its binding is not for this tenant to read.
        var view = await (
            from tool in db.Tools
            join binding in db.LookupTools on tool.Id equals binding.ToolId
            where tool.Id == id
            select new LookupToolView { Tool = tool, Binding = binding })
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        if (view is null)
        {
            return NotFound.ToProblem();
        }

        var seenLookups = await lookups.ListAsync(new LookupListQuery(tenantId, AssistantId: null, view.Binding.LookupIds), cancellationToken);
        var seen = await catalog.DescribeAsync(
            new BindingQuery(
                tenantId,
                AssistantId: null,
                [.. view.Binding.KnowledgeBaseIds.Concat(seenLookups.Select(l => l.KnowledgeBaseId).OfType<Guid>()).Distinct()],
                [.. view.Binding.ExposedFieldIds.Concat(view.Binding.FixedFilterFieldIds).Distinct()]),
            cancellationToken);

        return TypedResults.Ok(LookupToolDetail.From(
            view.Tool,
            view.Binding,
            await PreviewAsync(resolver, tenantId, view.Tool, view.Binding, cancellationToken),
            LookupToolWarnings.Evaluate(view.Binding, seenLookups, seen),
            seen));
    }

    private static async Task<Results<CreatedAtRoute<CreatedLookupTool>, ProblemHttpResult>> CreateAsync(
        LookupToolRequest request,
        AgentsDbContext db,
        ITenantContext tenant,
        LookupToolValidation validation,
        LookupToolResolver resolver,
        IKnowledgeBaseLookups lookups,
        IPlatformUnitOfWork unitOfWork,
        TimeProvider clock,
        ILogger<LookupToolEndpoints> logger,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Authorized requests have a tenant.");

        var valid = await validation.ValidateAsync(request, tenantId, cancellationToken);
        if (!valid.IsSuccess)
        {
            return valid.Error.ToProblem();
        }

        var input = valid.Value;
        if (await db.Tools.AnyAsync(t => t.Origin == ToolOrigin.Lookup && t.Name == input.Name, cancellationToken))
        {
            return NameTaken.ToProblem();
        }

        var tool = Tool.CreateLookup(
            tenantId,
            input.Name,
            input.Description,
            LookupToolHash.Compute(input.Name, input.Description, input.Configuration),
            input.Policy ?? ToolPolicy.Auto);
        var binding = LookupTool.Create(tool.Id, tenantId, input.Configuration, clock.GetUtcNow());

        // The tool and the slices it needs are saved together: a tool never exists without the lists it reads being on their way.
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            db.Tools.Add(tool);
            db.LookupTools.Add(binding);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (exception.IsUniqueViolation())
            {
                return NameTaken.ToProblem();
            }

            var declared = await DeclareAsync(lookups, tenantId, tool.Id, binding, cancellationToken);
            if (declared is not null)
            {
                return declared.ToProblem();
            }

            await transaction.CommitAsync(cancellationToken);
        }

        LogSaved(logger, tool.Id, binding.LookupIds.Count, binding.KnowledgeBaseIds.Count, "created");
        return TypedResults.CreatedAtRoute(
            new CreatedLookupTool(tool.Id, await PreviewAsync(resolver, tenantId, tool, binding, cancellationToken)),
            "GetLookupTool",
            new RouteValueDictionary { ["id"] = tool.Id });
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> UpdateAsync(
        Guid id,
        LookupToolRequest request,
        AgentsDbContext db,
        ITenantContext tenant,
        LookupToolValidation validation,
        IKnowledgeBaseLookups lookups,
        IPlatformUnitOfWork unitOfWork,
        TimeProvider clock,
        ILogger<LookupToolEndpoints> logger,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Authorized requests have a tenant.");

        var tool = await db.Tools.SingleOrDefaultAsync(t => t.Id == id && t.Origin == ToolOrigin.Lookup, cancellationToken);
        var binding = tool is null ? null : await db.LookupTools.SingleOrDefaultAsync(l => l.ToolId == id, cancellationToken);
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
        if (await db.Tools.AnyAsync(t => t.Origin == ToolOrigin.Lookup && t.Name == input.Name && t.Id != id, cancellationToken))
        {
            return NameTaken.ToProblem();
        }

        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            // The hash changes with the binding: a call that was waiting on the old one is refused when the user answers.
            tool.Reconfigure(input.Name, input.Description, LookupToolHash.Compute(input.Name, input.Description, input.Configuration), input.Policy ?? tool.Policy);
            binding.Apply(input.Configuration, clock.GetUtcNow());

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (exception.IsUniqueViolation())
            {
                return NameTaken.ToProblem();
            }

            var declared = await DeclareAsync(lookups, tenantId, id, binding, cancellationToken);
            if (declared is not null)
            {
                return declared.ToProblem();
            }

            await transaction.CommitAsync(cancellationToken);
        }

        LogSaved(logger, id, binding.LookupIds.Count, binding.KnowledgeBaseIds.Count, "updated");
        return TypedResults.NoContent();
    }

    /// <summary>Removes the tool with its binding and its activations, and lets go of its slices. Audit entries keep the names they recorded.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid id,
        AgentsDbContext db,
        ITenantContext tenant,
        IKnowledgeBaseLookups lookups,
        IPlatformUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Authorized requests have a tenant.");

        if (await db.Tools.SingleOrDefaultAsync(t => t.Id == id && t.Origin == ToolOrigin.Lookup, cancellationToken) is not { } tool)
        {
            return NotFound.ToProblem();
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        db.Tools.Remove(tool);
        await db.SaveChangesAsync(cancellationToken);
        await lookups.ReleaseSlicesAsync(tenantId, id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    /// <returns>The error to answer with, or <see langword="null"/> when the slices of the tool are declared.</returns>
    private static async Task<Error?> DeclareAsync(IKnowledgeBaseLookups lookups, Guid tenantId, Guid toolId, LookupTool binding, CancellationToken cancellationToken)
    {
        var declaration = await lookups.DeclareSlicesAsync(
            new SliceDeclarationRequest(tenantId, toolId, binding.LookupIds, LookupBinding.From(binding).SliceFilters()),
            cancellationToken);

        return declaration.Outcome switch
        {
            SliceDeclarationOutcome.Declared => null,
            SliceDeclarationOutcome.TooManySlices => Error.Validation(
                "lookup_tool.too_many_slices",
                $"The knowledge base {declaration.KnowledgeBaseName} already has the most lists computed for different fixed filters. Reuse the filters of another tool or remove a tool that has some."),
            _ => Error.Validation("lookup_tool.invalid_filter", declaration.Problem ?? "The fixed filters are not valid for the lookups of the tool."),
        };
    }

    /// <summary>The definition the model would get now, as an administrator sees it; <see langword="null"/> when no lookup of the tool is visible.</summary>
    private static async Task<LookupToolDefinitionPreview?> PreviewAsync(LookupToolResolver resolver, Guid tenantId, Tool tool, LookupTool binding, CancellationToken cancellationToken)
    {
        var resolved = await resolver.ResolveAsync(
            tenantId,
            assistantId: null,
            [new LookupToolToDescribe(tool.Id, tool.Description, LookupBinding.From(binding))],
            cancellationToken);
        if (!resolved.TryGetValue(tool.Id, out var found))
        {
            return null;
        }

        var exposedName = ToolNaming.Lookup(tool.Name);
        var characters = ToolDefinitionRenderer.Length([new ToolDefinition(exposedName, found.Definition.Description, found.Definition.InputSchema)]);
        using var schema = JsonDocument.Parse(found.Definition.InputSchema);
        return new LookupToolDefinitionPreview(
            exposedName,
            found.Definition.Description,
            schema.RootElement.Clone(),
            (characters + ContextUsage.CharactersPerToken - 1) / ContextUsage.CharactersPerToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Lookup tool {ToolId} {Action} with {LookupCount} lookups and {KnowledgeBaseCount} knowledge bases")]
    private static partial void LogSaved(ILogger logger, Guid toolId, int lookupCount, int knowledgeBaseCount, string action);
}

internal sealed record NamedId(Guid Id, string Name);

/// <param name="Shared"><see langword="true"/> for a tool of another tenant made visible by its scope: read-only, and its binding is not shown.</param>
internal sealed record LookupToolListItem(
    Guid Id,
    string Name,
    IReadOnlyList<NamedId> Lookups,
    IReadOnlyList<NamedId> KnowledgeBases,
    ScopeResponse Scope,
    string State,
    string Policy,
    bool Shared,
    IReadOnlyList<ToolWarning> Warnings)
{
    public static LookupToolListItem ForOwner(LookupToolView view, IReadOnlyList<NamedId> lookups, IReadOnlyList<NamedId> knowledgeBases, IReadOnlyList<ToolWarning> warnings) => new(
        view.Tool.Id,
        view.Tool.Name,
        lookups,
        knowledgeBases,
        new ScopeResponse(view.Binding.Scope.ToString(), view.Binding.ScopeTenantIds, view.Binding.ScopeAssistantIds),
        view.Tool.State.ToString(),
        view.Tool.Policy.ToString(),
        Shared: false,
        warnings);

    public static LookupToolListItem ForOther(LookupToolView view) => new(
        view.Tool.Id,
        view.Tool.Name,
        [],
        [],
        new ScopeResponse(view.Binding.Scope.ToString(), [], []),
        view.Tool.State.ToString(),
        view.Tool.Policy.ToString(),
        Shared: true,
        []);
}

/// <param name="Name">The name the model sees: <c>lookup__&lt;name&gt;</c>.</param>
/// <param name="EstimatedTokens">What the definition takes of the context, the figure the context gauge counts.</param>
internal sealed record LookupToolDefinitionPreview(string Name, string Description, JsonElement InputSchema, int EstimatedTokens);

internal sealed record CreatedLookupTool(Guid Id, LookupToolDefinitionPreview? Definition);

/// <param name="Alias">The name the model sees for the lookup, when one was given.</param>
internal sealed record BoundLookupDetail(Guid LookupId, string? Alias);

internal sealed record LookupToolDetail(
    Guid Id,
    string Name,
    string Description,
    IReadOnlyList<BoundLookupDetail> Lookups,
    IReadOnlyList<Guid> KnowledgeBaseIds,
    IReadOnlyList<ExposedFieldDetail> ExposedFields,
    JsonElement FixedFilters,
    ScopeResponse Scope,
    string State,
    string Policy,
    LookupToolDefinitionPreview? Definition,
    IReadOnlyList<ToolWarning> Warnings)
{
    public static LookupToolDetail From(Tool tool, LookupTool binding, LookupToolDefinitionPreview? definition, IReadOnlyList<ToolWarning> warnings, BindingDescription seen)
    {
        using var filters = JsonDocument.Parse(binding.FixedFilters);
        var current = LookupBinding.From(binding);
        return new LookupToolDetail(
            tool.Id,
            tool.Name,
            tool.Description,
            [.. binding.LookupIds.Select(id => new BoundLookupDetail(id, current.LookupAliases.GetValueOrDefault(id)))],
            binding.KnowledgeBaseIds,
            [
                .. binding.ExposedFieldIds.Select(id => new ExposedFieldDetail(
                    id,
                    current.ExposedAliases.GetValueOrDefault(id) ?? seen.Fields.FirstOrDefault(f => f.Id == id)?.Name)),
            ],
            filters.RootElement.Clone(),
            new ScopeResponse(binding.Scope.ToString(), binding.ScopeTenantIds, binding.ScopeAssistantIds),
            tool.State.ToString(),
            tool.Policy.ToString(),
            definition,
            warnings);
    }
}
