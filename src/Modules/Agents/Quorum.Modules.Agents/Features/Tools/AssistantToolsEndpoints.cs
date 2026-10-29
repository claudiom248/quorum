using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.Agents.Tools;
using Quorum.Modules.Agents.Tools.Lookup;
using Quorum.Modules.Agents.Tools.Rag;
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

    private static readonly Error ManifestNotSupported = Error.Validation(
        "tool.manifest_not_supported",
        "A manifest can be set only on a lookup tool, and it is full or lean.");

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
        RagToolResolver resolver,
        LookupToolResolver lookupResolver,
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
        // A rag tool whose knowledge bases this assistant cannot see is not in its pool: there is nothing it could search.
        var rag = await resolver.ResolvePoolAsync(pool, db.CurrentTenantId ?? Guid.Empty, id, cancellationToken);
        // The same for a lookup tool whose lookups this assistant cannot see.
        var lookups = await lookupResolver.ResolvePoolAsync(pool, db.CurrentTenantId ?? Guid.Empty, id, cancellationToken);
        pool = [.. pool.Where(t => (t.Tool.Origin != ToolOrigin.Rag || rag.ContainsKey(t.Tool.Id)) && (t.Tool.Origin != ToolOrigin.Lookup || lookups.ContainsKey(t.Tool.Id)))];
        var activations = await db.AssistantTools.Where(a => a.AssistantId == id).ToDictionaryAsync(a => a.ToolId, a => a.Manifest, cancellationToken);
        var active = activations.Keys.ToHashSet();

        // The size a rag tool takes is that of the definition the model gets, not of the stored one.
        var activeDefinitions = pool
            .Where(t => active.Contains(t.Tool.Id))
            .SelectMany(t => rag.TryGetValue(t.Tool.Id, out var resolved)
                ? [new ToolDefinition(t.ExposedName, resolved.Definition.Description, resolved.Definition.InputSchema)]
                : lookups.TryGetValue(t.Tool.Id, out var lookup)
                    // A lookup tool is offered as the functions of its manifest on this assistant, and each takes its own part of the context.
                    ? FunctionsOn(t.Tool, lookup, activations.GetValueOrDefault(t.Tool.Id)).Select(f => new ToolDefinition(ToolNaming.LookupFunction(t.Tool.Name, f.Operation), f.Description, f.InputSchema)).ToList()
                    : [t.Definition])
            .ToList();
        var warnings = ToolWarnings.Evaluate(
            activeDefinitions.Count,
            ToolDefinitionRenderer.Length(activeDefinitions),
            budget.Value * ContextUsage.CharactersPerToken);

        return TypedResults.Ok(new AssistantToolsResponse(
            pool.Select(t => new PoolToolResponse(
                    t.Tool.Id,
                    t.ExposedName,
                    active.Contains(t.Tool.Id),
                    t.Tool.Origin == ToolOrigin.Lookup ? activations.GetValueOrDefault(t.Tool.Id, LookupManifest.Full) : null))
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ToList(),
            warnings));
    }

    /// <summary>The functions a lookup tool is offered as on one assistant: its full ones, or the lean pair of spec 012 D-8.</summary>
    private static IReadOnlyList<GeneratedLookupFunction> FunctionsOn(Tool tool, ResolvedLookupTool lookup, string? manifest) =>
        manifest == LookupManifest.Lean ? LookupLeanDefinition.Build(tool.Name, tool.Description, lookup).Functions : lookup.Definition.Functions;

    /// <summary>Replaces the active set: what is not in the request is deactivated. A manifest not sent keeps the one the tool has.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> SetToolsAsync(
        Guid id,
        SetAssistantToolsRequest request,
        AgentsDbContext db,
        RagToolResolver resolver,
        LookupToolResolver lookupResolver,
        CancellationToken cancellationToken)
    {
        if (await db.Assistants.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken) is not { } assistant)
        {
            return AssistantNotFound.ToProblem();
        }

        var wanted = (request.ToolIds ?? []).Distinct().ToList();
        var inPool = await ToolPool.For(db, id).AsNoTracking().Where(t => wanted.Contains(t.Tool.Id)).ToListAsync(cancellationToken);
        if (inPool.Count != wanted.Count)
        {
            return NotInPool.ToProblem();
        }

        // A rag tool with no knowledge base this assistant can see is not in its pool (spec 003 AC-23).
        var rag = await resolver.ResolvePoolAsync(inPool, db.CurrentTenantId ?? Guid.Empty, id, cancellationToken);
        if (inPool.Any(t => t.Tool.Origin == ToolOrigin.Rag && !rag.ContainsKey(t.Tool.Id)))
        {
            return NotInPool.ToProblem();
        }

        // And a lookup tool with no lookup this assistant can see.
        var lookups = await lookupResolver.ResolvePoolAsync(inPool, db.CurrentTenantId ?? Guid.Empty, id, cancellationToken);
        if (inPool.Any(t => t.Tool.Origin == ToolOrigin.Lookup && !lookups.ContainsKey(t.Tool.Id)))
        {
            return NotInPool.ToProblem();
        }

        // A manifest is for a lookup tool that is being activated here, and it is full or lean (spec 012 D-8).
        var manifests = new Dictionary<Guid, string>();
        foreach (var (toolId, value) in request.Manifests ?? new Dictionary<Guid, string>())
        {
            var lookupTool = inPool.FirstOrDefault(t => t.Tool.Id == toolId && t.Tool.Origin == ToolOrigin.Lookup);
            if (lookupTool is null || !wanted.Contains(toolId) || !TryManifest(value, out var manifest))
            {
                return ManifestNotSupported.ToProblem();
            }

            manifests[toolId] = manifest;
        }

        var current = await db.AssistantTools.Where(a => a.AssistantId == id).ToListAsync(cancellationToken);
        db.AssistantTools.RemoveRange(current.Where(a => !wanted.Contains(a.ToolId)));
        foreach (var activation in current.Where(a => wanted.Contains(a.ToolId) && manifests.ContainsKey(a.ToolId)))
        {
            activation.SetManifest(manifests[activation.ToolId]);
        }

        foreach (var toolId in wanted.Where(toolId => current.All(a => a.ToolId != toolId)))
        {
            var created = AssistantTool.Create(assistant.TenantId, id, toolId);
            if (manifests.TryGetValue(toolId, out var manifest))
            {
                created.SetManifest(manifest);
            }

            db.AssistantTools.Add(created);
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>The manifest as the administrator writes it, <c>full</c> or <c>lean</c>, in the stored form.</summary>
    private static bool TryManifest(string? value, out string manifest)
    {
        manifest = string.Empty;
        if (string.Equals(value, "full", StringComparison.OrdinalIgnoreCase))
        {
            manifest = LookupManifest.Full;
        }
        else if (string.Equals(value, "lean", StringComparison.OrdinalIgnoreCase))
        {
            manifest = LookupManifest.Lean;
        }

        return manifest.Length > 0;
    }
}

internal sealed record AssistantResponse(Guid Id, string Name);

/// <param name="Manifest">For a lookup tool: <c>Full</c> or <c>Lean</c> on this assistant; <see langword="null"/> for the other tools.</param>
internal sealed record PoolToolResponse(Guid ToolId, string Name, bool Active, string? Manifest = null);

internal sealed record AssistantToolsResponse(List<PoolToolResponse> Pool, IReadOnlyList<ToolWarning> Warnings);

/// <param name="ToolIds">The tools active on the assistant after the call.</param>
/// <param name="Manifests">For lookup tools, <c>full</c> or <c>lean</c> by tool id; a tool left out keeps its manifest.</param>
internal sealed record SetAssistantToolsRequest(List<Guid>? ToolIds, Dictionary<Guid, string>? Manifests = null);
