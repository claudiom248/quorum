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

namespace Quorum.Modules.Agents.Features.Agents;

/// <summary>Which tools of the pool each agent has active (spec 013 D-17); attaching them is a permission of its own (D-26).</summary>
internal sealed class AgentToolsEndpoints : IEndpointGroup
{
    private static readonly Error AgentNotFound = Error.NotFound("agent.not_found", "The agent does not exist.");

    private static readonly Error NotInPool = Error.Validation(
        "tool.not_in_pool",
        "Only enabled tools that are available to this agent can be activated. Enable the tool first, or remove it from the request.");

    private static readonly Error ManifestNotSupported = Error.Validation(
        "tool.manifest_not_supported",
        "A manifest can be set only on a lookup tool, and it is full or lean.");

    private static readonly Error EndpointWithoutTools = Error.Validation(
        "tool.endpoint_without_tools",
        "The model endpoint of this agent does not support tools. Choose another endpoint first.");

    private static readonly Error ModeNotSupported = Error.Validation(
        "tool.mode_not_supported",
        "A tool mode is required or optional.");

    private static readonly Error ClashWithSubAgent = Error.Validation(
        "tool.clashes_with_sub_agent",
        "A tool has the same name as a sub-agent of this agent: the model would see two tools with one name.");

    public void Map(IEndpointRouteBuilder app)
    {
        var agents = app.MapGroup("/api/agents").WithTags("Agents");

        agents.MapGet("/{id:guid}/tools", GetToolsAsync).RequireAuthorization(QuorumPolicies.ToolsAttach).WithName("GetAgentTools");
        agents.MapPut("/{id:guid}/tools", SetToolsAsync).RequireAuthorization(QuorumPolicies.ToolsAttach).WithName("SetAgentTools");
    }

    private static async Task<Results<Ok<AgentToolsResponse>, ProblemHttpResult>> GetToolsAsync(
        Guid id,
        AgentsDbContext db,
        RagToolResolver resolver,
        LookupToolResolver lookupResolver,
        CancellationToken cancellationToken)
    {
        if (await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken) is not { } agent)
        {
            return AgentNotFound.ToProblem();
        }

        // An agent used only as a sub-agent has no endpoint of its own to measure the definitions against.
        var budget = agent.ModelEndpointId is { } endpointId
            ? await db.ModelEndpoints.Where(e => e.Id == endpointId).Select(e => e.ContextTokenBudget).SingleOrDefaultAsync(cancellationToken)
            : 0;
        var pool = await ToolPool.Of(db, id).AsNoTracking().ToListAsync(cancellationToken);
        var (rag, lookups) = await ResolveAsync(db, resolver, lookupResolver, pool, id, cancellationToken);
        pool = [.. pool.Where(t => (t.Tool.Origin != ToolOrigin.Rag || rag.ContainsKey(t.Tool.Id)) && (t.Tool.Origin != ToolOrigin.Lookup || lookups.ContainsKey(t.Tool.Id)))];
        var attached = await db.AgentTools.AsNoTracking().Where(a => a.AgentId == id).ToListAsync(cancellationToken);
        var activations = attached.ToDictionary(a => a.ToolId, a => a.Manifest);
        var modes = attached.ToDictionary(a => a.ToolId, a => a.Mode);
        var active = activations.Keys.ToHashSet();

        // The size a rag tool takes is that of the definition the model gets, not of the stored one.
        var activeDefinitions = pool
            .Where(t => active.Contains(t.Tool.Id))
            .SelectMany(t => rag.TryGetValue(t.Tool.Id, out var resolved)
                ? [new ToolDefinition(t.ExposedName, resolved.Definition.Description, resolved.Definition.InputSchema)]
                : lookups.TryGetValue(t.Tool.Id, out var lookup)
                    // A lookup tool is offered as the functions of its manifest on this agent, and each takes its own part of the context.
                    ? FunctionsOn(t.Tool, lookup, activations.GetValueOrDefault(t.Tool.Id)).Select(f => new ToolDefinition(ToolNaming.LookupFunction(t.Tool.Name, f.Operation), f.Description, f.InputSchema)).ToList()
                    : [t.Definition])
            .ToList();
        var warnings = ToolWarnings.Evaluate(
            activeDefinitions.Count,
            ToolDefinitionRenderer.Length(activeDefinitions),
            budget * ContextUsage.CharactersPerToken);

        return TypedResults.Ok(new AgentToolsResponse(
            pool.Select(t => new PoolToolResponse(
                    t.Tool.Id,
                    t.ExposedName,
                    active.Contains(t.Tool.Id),
                    t.Tool.Origin == ToolOrigin.Lookup ? activations.GetValueOrDefault(t.Tool.Id, LookupManifest.Full) : null,
                    OriginName(t.Tool.Origin),
                    t.ServerName,
                    modes.GetValueOrDefault(t.Tool.Id, ToolMode.Required).ToString()))
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ToList(),
            warnings));
    }

    private static string OriginName(ToolOrigin origin) => origin switch
    {
        ToolOrigin.Builtin => "builtin",
        ToolOrigin.Rag => "rag",
        ToolOrigin.Lookup => "lookup",
        ToolOrigin.WebSearch => "web_search",
        ToolOrigin.WebFetch => "web_fetch",
        _ => "mcp",
    };

    /// <summary>The functions a lookup tool is offered as on one agent: its full ones, or the lean pair of spec 012 D-8.</summary>
    private static IReadOnlyList<GeneratedLookupFunction> FunctionsOn(Tool tool, ResolvedLookupTool lookup, string? manifest) =>
        manifest == LookupManifest.Lean ? LookupLeanDefinition.Build(tool.Name, tool.Description, lookup).Functions : lookup.Definition.Functions;

    /// <summary>
    /// The rag and lookup tools of a pool the agent can be offered. Their knowledge bases and lists are scoped to assistants (spec 013 D-41),
    /// so the agent is judged by the assistants that answer through it; one that none does sees only what is shared with the whole tenant.
    /// </summary>
    private static async Task<(Dictionary<Guid, ResolvedRagTool> Rag, Dictionary<Guid, ResolvedLookupTool> Lookups)> ResolveAsync(
        AgentsDbContext db,
        RagToolResolver resolver,
        LookupToolResolver lookupResolver,
        IReadOnlyList<PoolTool> pool,
        Guid agentId,
        CancellationToken cancellationToken)
    {
        var tenantId = db.CurrentTenantId ?? Guid.Empty;
        var assistantIds = await db.Assistants.Where(a => a.RootAgentId == agentId).Select(a => a.Id).ToListAsync(cancellationToken);
        if (assistantIds.Count == 0)
        {
            assistantIds.Add(Guid.Empty);
        }

        var rag = new Dictionary<Guid, ResolvedRagTool>();
        var lookups = new Dictionary<Guid, ResolvedLookupTool>();
        foreach (var assistantId in assistantIds)
        {
            foreach (var (toolId, resolved) in await resolver.ResolvePoolAsync(pool, tenantId, assistantId, cancellationToken))
            {
                rag.TryAdd(toolId, resolved);
            }

            foreach (var (toolId, resolved) in await lookupResolver.ResolvePoolAsync(pool, tenantId, assistantId, cancellationToken))
            {
                lookups.TryAdd(toolId, resolved);
            }
        }

        return (rag, lookups);
    }

    /// <summary>Replaces the active set: what is not in the request is deactivated. A manifest not sent keeps the one the tool has.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> SetToolsAsync(
        Guid id,
        SetAgentToolsRequest request,
        AgentsDbContext db,
        RagToolResolver resolver,
        LookupToolResolver lookupResolver,
        CancellationToken cancellationToken)
    {
        if (await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken) is not { } agent)
        {
            return AgentNotFound.ToProblem();
        }

        // The tools with their modes (spec 018 D-3) supersede the plain list; a plain list keeps the modes the tools have.
        var wanted = (request.Tools is { } withModes ? withModes.Select(t => t.ToolId) : request.ToolIds ?? []).Distinct().ToList();
        var modes = new Dictionary<Guid, ToolMode>();
        foreach (var tool in request.Tools ?? [])
        {
            if (!TryMode(tool.Mode, out var mode))
            {
                return ModeNotSupported.ToProblem();
            }

            modes[tool.ToolId] = mode;
        }

        if (wanted.Count > 0
            && agent.ModelEndpointId is { } endpointId
            && await db.ModelEndpoints.AnyAsync(e => e.Id == endpointId && !e.SupportsTools, cancellationToken))
        {
            return EndpointWithoutTools.ToProblem();
        }

        var inPool = await ToolPool.Of(db, id).AsNoTracking().Where(t => wanted.Contains(t.Tool.Id)).ToListAsync(cancellationToken);
        if (inPool.Count != wanted.Count)
        {
            return NotInPool.ToProblem();
        }

        // A rag tool with no knowledge base this agent's assistants can see is not in its pool (spec 003 AC-23), nor a lookup tool with no lookup.
        var (rag, lookups) = await ResolveAsync(db, resolver, lookupResolver, inPool, id, cancellationToken);
        if (inPool.Any(t => (t.Tool.Origin == ToolOrigin.Rag && !rag.ContainsKey(t.Tool.Id)) || (t.Tool.Origin == ToolOrigin.Lookup && !lookups.ContainsKey(t.Tool.Id))))
        {
            return NotInPool.ToProblem();
        }

        // The model sees the sub-agents of the agent as tools of the same list (spec 013 AC-26).
        var subAgentSlugs = await (
            from edge in db.AgentSubAgents
            join child in db.Agents on edge.ChildAgentId equals child.Id
            where edge.ParentAgentId == id
            select child.Slug)
            .ToListAsync(cancellationToken);
        if (inPool.Any(t => AgentRules.ClashesWithTool(t.ExposedName, subAgentSlugs)))
        {
            return ClashWithSubAgent.ToProblem();
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

        var current = await db.AgentTools.Where(a => a.AgentId == id).ToListAsync(cancellationToken);
        db.AgentTools.RemoveRange(current.Where(a => !wanted.Contains(a.ToolId)));
        foreach (var activation in current.Where(a => wanted.Contains(a.ToolId) && manifests.ContainsKey(a.ToolId)))
        {
            activation.SetManifest(manifests[activation.ToolId]);
        }

        foreach (var activation in current.Where(a => wanted.Contains(a.ToolId) && modes.ContainsKey(a.ToolId)))
        {
            activation.SetMode(modes[activation.ToolId]);
        }

        foreach (var toolId in wanted.Where(toolId => current.All(a => a.ToolId != toolId)))
        {
            var created = AgentTool.Create(agent.TenantId, id, toolId);
            if (manifests.TryGetValue(toolId, out var manifest))
            {
                created.SetManifest(manifest);
            }

            if (modes.TryGetValue(toolId, out var mode))
            {
                created.SetMode(mode);
            }

            db.AgentTools.Add(created);
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>The mode as the administrator writes it, <c>required</c> or <c>optional</c>; left out means required.</summary>
    private static bool TryMode(string? value, out ToolMode mode)
    {
        mode = ToolMode.Required;
        if (string.IsNullOrEmpty(value) || string.Equals(value, "required", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, "optional", StringComparison.OrdinalIgnoreCase))
        {
            mode = ToolMode.Optional;
            return true;
        }

        return false;
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

/// <param name="Name">What the model calls the tool: its own name, or <c>server__tool</c> for an MCP tool.</param>
/// <param name="Manifest">For a lookup tool: <c>Full</c> or <c>Lean</c> on this agent; <see langword="null"/> for the other tools.</param>
/// <param name="Origin">Where the tool comes from (<c>builtin</c>, <c>rag</c>, <c>lookup</c>, <c>web_search</c>, <c>web_fetch</c> or <c>mcp</c>), which the name no longer says.</param>
/// <param name="Server">The MCP server of an MCP tool; <see langword="null"/> for the others.</param>
/// <param name="Mode"><c>Required</c> or <c>Optional</c> on this agent (spec 018 D-3); <c>Required</c> for a tool not attached.</param>
internal sealed record PoolToolResponse(Guid ToolId, string Name, bool Active, string? Manifest = null, string Origin = "mcp", string? Server = null, string Mode = "Required");

internal sealed record AgentToolsResponse(List<PoolToolResponse> Pool, IReadOnlyList<ToolWarning> Warnings);

/// <param name="ToolIds">The tools active on the agent after the call; a tool left out keeps its mode when it was attached.</param>
/// <param name="Manifests">For lookup tools, <c>full</c> or <c>lean</c> by tool id; a tool left out keeps its manifest.</param>
/// <param name="Tools">The tools active after the call with their modes (spec 018 D-3); supersedes <paramref name="ToolIds"/> when sent.</param>
internal sealed record SetAgentToolsRequest(List<Guid>? ToolIds, Dictionary<Guid, string>? Manifests = null, List<AgentToolModeRequest>? Tools = null);

/// <param name="Mode"><c>required</c> (default) or <c>optional</c>.</param>
internal sealed record AgentToolModeRequest(Guid ToolId, string? Mode);
