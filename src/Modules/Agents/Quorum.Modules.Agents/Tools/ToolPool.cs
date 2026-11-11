using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;

namespace Quorum.Modules.Agents.Tools;

/// <summary>A tool of a pool with what the model-facing name needs from its server.</summary>
internal sealed class PoolTool
{
    public required Tool Tool { get; init; }

    /// <summary>Null for built-in, rag and lookup tools.</summary>
    public string? ServerName { get; init; }

    /// <summary>The server of an MCP tool, loaded by the admin listings only (the pool an agent draws from does not need it).</summary>
    public McpServer? Server { get; init; }

    public string ExposedName => ExposedNameOf(Tool, ServerName);

    /// <summary>As stored. The schema of a rag or lookup tool is generated when it is used, so this one is only the administrator's description.</summary>
    public ToolDefinition Definition => new(ExposedName, Tool.Description, Tool.InputSchema);

    /// <summary>The name the model reads the tool by: from its origin and, for an MCP tool, its server.</summary>
    public static string ExposedNameOf(Tool tool, string? serverName) => tool.Origin switch
    {
        ToolOrigin.Builtin => ToolNaming.Builtin(tool.ExposedName),
        ToolOrigin.Rag => ToolNaming.Rag(tool.Name),
        ToolOrigin.Lookup => ToolNaming.Lookup(tool.Name),
        ToolOrigin.WebSearch => ToolNaming.WebSearch(tool.Name),
        ToolOrigin.WebFetch => ToolNaming.WebFetch(tool.Name),
        _ => ToolNaming.Mcp(serverName ?? throw new InvalidOperationException("An MCP tool has a server."), tool.Name),
    };
}

/// <summary>A tool an agent has activated and may use, with the server address and the bindings the runtime reads for it.</summary>
internal sealed class PoolRow
{
    public required Guid AgentId { get; init; }

    public required Tool Tool { get; init; }

    public string? ServerName { get; init; }

    public Guid? ServerId { get; init; }

    public string? ServerUrl { get; init; }

    public string? ProtectedHeaders { get; init; }

    /// <summary>The manifest is the activation's, so the same tool can be lean on one agent and full on another (spec 012 D-8).</summary>
    public string? Manifest { get; init; }

    public RagTool? Rag { get; init; }

    public LookupTool? Lookup { get; init; }

    public WebSearchTool? WebSearch { get; init; }

    public WebFetchTool? WebFetch { get; init; }

}

/// <summary>
/// The tools an agent of the current tenant can use (D-2, D-23): enabled built-in tools of the tenant, enabled tools of
/// available MCP servers whose scope covers the agent, and enabled rag tools whose scope covers it. Activation
/// (<see cref="AgentTool"/>) picks from this pool.
/// </summary>
internal static class ToolPool
{
    /// <summary>
    /// What the runtime needs of the tools the given agents have activated, in one query: the pool rule of <see cref="For"/> applied to
    /// each agent with the tenant that owns it (an agent another tenant shares runs with the tools its owner gave it, spec 013 D-21),
    /// together with the activation, the server's address and the bindings, so nothing is read a second time.
    /// The rule below must stay the same as the one in <see cref="For"/>.
    /// </summary>
    public static IQueryable<PoolRow> ActivatedFor(AgentsDbContext db, IReadOnlyCollection<Guid> agentIds)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(agentIds);

        var ids = agentIds.ToList();
        return
            (from activation in db.AgentTools.IgnoreQueryFilters()
             where ids.Contains(activation.AgentId)
             join agent in db.Agents.IgnoreQueryFilters() on activation.AgentId equals agent.Id
             join tool in db.Tools.IgnoreQueryFilters() on activation.ToolId equals tool.Id
             join server in db.McpServers.IgnoreQueryFilters() on tool.ServerId equals server.Id into servers
             from server in servers.DefaultIfEmpty()
             join rag in db.RagTools.IgnoreQueryFilters() on tool.Id equals rag.ToolId into rags
             from rag in rags.DefaultIfEmpty()
             join lookup in db.LookupTools.IgnoreQueryFilters() on tool.Id equals lookup.ToolId into lookups
             from lookup in lookups.DefaultIfEmpty()
             join webSearch in db.WebSearchTools.IgnoreQueryFilters() on tool.Id equals webSearch.ToolId into webSearches
             from webSearch in webSearches.DefaultIfEmpty()
             join webFetch in db.WebFetchTools.IgnoreQueryFilters() on tool.Id equals webFetch.ToolId into webFetches
             from webFetch in webFetches.DefaultIfEmpty()
             where tool.State == ToolState.Enabled
                   && ((tool.Origin == ToolOrigin.Builtin && tool.TenantId == agent.TenantId && tool.Name != BuiltinToolCatalog.LoadSkill)
                       || (server != null
                           && server.Status == McpServerStatus.Available
                           && (server.Scope == ToolScopeKind.Global
                               || (server.Scope == ToolScopeKind.Tenant && (server.TenantId == agent.TenantId || server.ScopeTenantIds.Contains(agent.TenantId)))
                               || (server.Scope == ToolScopeKind.Agent && server.TenantId == agent.TenantId && server.ScopeAgentIds.Contains(agent.Id))))
                       || (rag != null
                           && (rag.Scope == ToolScopeKind.Global
                               || (rag.Scope == ToolScopeKind.Tenant && (rag.TenantId == agent.TenantId || rag.ScopeTenantIds.Contains(agent.TenantId)))
                               || (rag.Scope == ToolScopeKind.Agent && rag.TenantId == agent.TenantId && rag.ScopeAgentIds.Contains(agent.Id))))
                       || (lookup != null
                           && (lookup.Scope == ToolScopeKind.Global
                               || (lookup.Scope == ToolScopeKind.Tenant && (lookup.TenantId == agent.TenantId || lookup.ScopeTenantIds.Contains(agent.TenantId)))
                               || (lookup.Scope == ToolScopeKind.Agent && lookup.TenantId == agent.TenantId && lookup.ScopeAgentIds.Contains(agent.Id))))
                       || (webSearch != null
                           && (webSearch.Scope == ToolScopeKind.Global
                               || (webSearch.Scope == ToolScopeKind.Tenant && (webSearch.TenantId == agent.TenantId || webSearch.ScopeTenantIds.Contains(agent.TenantId)))
                               || (webSearch.Scope == ToolScopeKind.Agent && webSearch.TenantId == agent.TenantId && webSearch.ScopeAgentIds.Contains(agent.Id))))
                       || (webFetch != null
                           && (webFetch.Scope == ToolScopeKind.Global
                               || (webFetch.Scope == ToolScopeKind.Tenant && (webFetch.TenantId == agent.TenantId || webFetch.ScopeTenantIds.Contains(agent.TenantId)))
                               || (webFetch.Scope == ToolScopeKind.Agent && webFetch.TenantId == agent.TenantId && webFetch.ScopeAgentIds.Contains(agent.Id)))))
             select new PoolRow
             {
                 AgentId = agent.Id,
                 Tool = tool,
                 ServerName = server == null ? null : server.Name,
                 ServerId = tool.ServerId,
                 ServerUrl = server == null ? null : server.Url,
                 ProtectedHeaders = server == null ? null : server.ProtectedHeaders,
                 Manifest = activation.Manifest,
                 Rag = rag,
                 Lookup = lookup,
                 WebSearch = webSearch,
                 WebFetch = webFetch,
             })
            .AsNoTracking();
    }

    /// <summary>
    /// The pool of an agent of the current tenant, for the admin listings. An agent another tenant shares (spec 013 D-21) runs with the tools
    /// its owner gave it, which the runtime reads through <see cref="ActivatedFor"/>. The rule below must stay the same as the one there:
    /// EF cannot translate the two as one query, so each is written out.
    /// </summary>
    public static IQueryable<PoolTool> Of(AgentsDbContext db, Guid agentId)
    {
        ArgumentNullException.ThrowIfNull(db);

        // Tools of a server or a binding another tenant owns are outside the tenant filter: the scope rule below is what lets them in.
        var tenantId = db.CurrentTenantId ?? Guid.Empty;

        return
            from tool in db.Tools.IgnoreQueryFilters()
            join server in db.McpServers.IgnoreQueryFilters() on tool.ServerId equals server.Id into servers
            from server in servers.DefaultIfEmpty()
            join rag in db.RagTools.IgnoreQueryFilters() on tool.Id equals rag.ToolId into rags
            from rag in rags.DefaultIfEmpty()
            join lookup in db.LookupTools.IgnoreQueryFilters() on tool.Id equals lookup.ToolId into lookups
            from lookup in lookups.DefaultIfEmpty()
            join webSearch in db.WebSearchTools.IgnoreQueryFilters() on tool.Id equals webSearch.ToolId into webSearches
            from webSearch in webSearches.DefaultIfEmpty()
            join webFetch in db.WebFetchTools.IgnoreQueryFilters() on tool.Id equals webFetch.ToolId into webFetches
            from webFetch in webFetches.DefaultIfEmpty()
            where tool.State == ToolState.Enabled
                  // load_skill is offered by the runtime to agents with skills and never activated (spec 017 D-4), so it is not in the pool.
                  && ((tool.Origin == ToolOrigin.Builtin && tool.TenantId == tenantId && tool.Name != BuiltinToolCatalog.LoadSkill)
                      || (server != null
                          && server.Status == McpServerStatus.Available
                          && (server.Scope == ToolScopeKind.Global
                              || (server.Scope == ToolScopeKind.Tenant && (server.TenantId == tenantId || server.ScopeTenantIds.Contains(tenantId)))
                              // An owner lists only its own agents; a foreign owner naming ours reaches nothing.
                              || (server.Scope == ToolScopeKind.Agent && server.TenantId == tenantId && server.ScopeAgentIds.Contains(agentId))))
                      || (rag != null
                          && (rag.Scope == ToolScopeKind.Global
                              || (rag.Scope == ToolScopeKind.Tenant && (rag.TenantId == tenantId || rag.ScopeTenantIds.Contains(tenantId)))
                              || (rag.Scope == ToolScopeKind.Agent && rag.TenantId == tenantId && rag.ScopeAgentIds.Contains(agentId))))
                      || (lookup != null
                          && (lookup.Scope == ToolScopeKind.Global
                              || (lookup.Scope == ToolScopeKind.Tenant && (lookup.TenantId == tenantId || lookup.ScopeTenantIds.Contains(tenantId)))
                              || (lookup.Scope == ToolScopeKind.Agent && lookup.TenantId == tenantId && lookup.ScopeAgentIds.Contains(agentId))))
                      || (webSearch != null
                          && (webSearch.Scope == ToolScopeKind.Global
                              || (webSearch.Scope == ToolScopeKind.Tenant && (webSearch.TenantId == tenantId || webSearch.ScopeTenantIds.Contains(tenantId)))
                              || (webSearch.Scope == ToolScopeKind.Agent && webSearch.TenantId == tenantId && webSearch.ScopeAgentIds.Contains(agentId))))
                      || (webFetch != null
                          && (webFetch.Scope == ToolScopeKind.Global
                              || (webFetch.Scope == ToolScopeKind.Tenant && (webFetch.TenantId == tenantId || webFetch.ScopeTenantIds.Contains(tenantId)))
                              || (webFetch.Scope == ToolScopeKind.Agent && webFetch.TenantId == tenantId && webFetch.ScopeAgentIds.Contains(agentId)))))
            select new PoolTool { Tool = tool, ServerName = server == null ? null : server.Name };
    }
}

/// <summary>What the current tenant can see of servers and tools: its own, and what other tenants share with it through a scope.</summary>
internal static class ToolVisibility
{
    public static IQueryable<McpServer> Servers(AgentsDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        var tenantId = db.CurrentTenantId ?? Guid.Empty;

        return db.McpServers.IgnoreQueryFilters().Where(server =>
            server.TenantId == tenantId
            || server.Scope == ToolScopeKind.Global
            || (server.Scope == ToolScopeKind.Tenant && server.ScopeTenantIds.Contains(tenantId)));
    }

    /// <summary>The tenant's own tools in any state, and the enabled tools of the servers and rag bindings shared with it (their review belongs to the owner).</summary>
    public static IQueryable<PoolTool> Tools(AgentsDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        var tenantId = db.CurrentTenantId ?? Guid.Empty;

        return
            from tool in db.Tools.IgnoreQueryFilters()
            join server in db.McpServers.IgnoreQueryFilters() on tool.ServerId equals server.Id into servers
            from server in servers.DefaultIfEmpty()
            join rag in db.RagTools.IgnoreQueryFilters() on tool.Id equals rag.ToolId into rags
            from rag in rags.DefaultIfEmpty()
            join lookup in db.LookupTools.IgnoreQueryFilters() on tool.Id equals lookup.ToolId into lookups
            from lookup in lookups.DefaultIfEmpty()
            where tool.TenantId == tenantId
                  || (tool.State == ToolState.Enabled
                      && ((server != null
                           && (server.Scope == ToolScopeKind.Global
                               || (server.Scope == ToolScopeKind.Tenant && server.ScopeTenantIds.Contains(tenantId))))
                          || (rag != null
                              && (rag.Scope == ToolScopeKind.Global
                                  || (rag.Scope == ToolScopeKind.Tenant && rag.ScopeTenantIds.Contains(tenantId))))
                          || (lookup != null
                              && (lookup.Scope == ToolScopeKind.Global
                                  || (lookup.Scope == ToolScopeKind.Tenant && lookup.ScopeTenantIds.Contains(tenantId))))))
            select new PoolTool { Tool = tool, ServerName = server == null ? null : server.Name, Server = server };
    }

    /// <summary>
    /// The rag tools the tenant can see with their bindings: its own and those shared with it (enabled only, as in <see cref="Tools"/>).
    /// </summary>
    public static IQueryable<RagToolView> RagTools(AgentsDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        var tenantId = db.CurrentTenantId ?? Guid.Empty;

        return
            from tool in db.Tools.IgnoreQueryFilters()
            join rag in db.RagTools.IgnoreQueryFilters() on tool.Id equals rag.ToolId
            where tool.TenantId == tenantId
                  || (tool.State == ToolState.Enabled
                      && (rag.Scope == ToolScopeKind.Global
                          || (rag.Scope == ToolScopeKind.Tenant && rag.ScopeTenantIds.Contains(tenantId))))
            select new RagToolView { Tool = tool, Binding = rag };
    }

    /// <summary>
    /// The lookup tools the tenant can see with their bindings: its own and those shared with it (enabled only, as in <see cref="Tools"/>).
    /// </summary>
    public static IQueryable<LookupToolView> LookupTools(AgentsDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        var tenantId = db.CurrentTenantId ?? Guid.Empty;

        return
            from tool in db.Tools.IgnoreQueryFilters()
            join lookup in db.LookupTools.IgnoreQueryFilters() on tool.Id equals lookup.ToolId
            where tool.TenantId == tenantId
                  || (tool.State == ToolState.Enabled
                      && (lookup.Scope == ToolScopeKind.Global
                          || (lookup.Scope == ToolScopeKind.Tenant && lookup.ScopeTenantIds.Contains(tenantId))))
            select new LookupToolView { Tool = tool, Binding = lookup };
    }

    /// <summary>
    /// The web search tools the tenant can see with their bindings: its own and those shared with it (enabled only, as in <see cref="Tools"/>).
    /// </summary>
    public static IQueryable<WebSearchToolView> WebSearchTools(AgentsDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        var tenantId = db.CurrentTenantId ?? Guid.Empty;

        return
            from tool in db.Tools.IgnoreQueryFilters()
            join webSearch in db.WebSearchTools.IgnoreQueryFilters() on tool.Id equals webSearch.ToolId
            where tool.TenantId == tenantId
                  || (tool.State == ToolState.Enabled
                      && (webSearch.Scope == ToolScopeKind.Global
                          || (webSearch.Scope == ToolScopeKind.Tenant && webSearch.ScopeTenantIds.Contains(tenantId))))
            select new WebSearchToolView { Tool = tool, Binding = webSearch };
    }

    /// <summary>
    /// The web fetch tools the tenant can see with their bindings: its own and those shared with it (enabled only, as in <see cref="Tools"/>).
    /// </summary>
    public static IQueryable<WebFetchToolView> WebFetchTools(AgentsDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        var tenantId = db.CurrentTenantId ?? Guid.Empty;

        return
            from tool in db.Tools.IgnoreQueryFilters()
            join webFetch in db.WebFetchTools.IgnoreQueryFilters() on tool.Id equals webFetch.ToolId
            where tool.TenantId == tenantId
                  || (tool.State == ToolState.Enabled
                      && (webFetch.Scope == ToolScopeKind.Global
                          || (webFetch.Scope == ToolScopeKind.Tenant && webFetch.ScopeTenantIds.Contains(tenantId))))
            select new WebFetchToolView { Tool = tool, Binding = webFetch };
    }
}

/// <summary>A rag tool with its binding.</summary>
internal sealed class RagToolView
{
    public required Tool Tool { get; init; }

    public required RagTool Binding { get; init; }
}

/// <summary>A lookup tool with its binding.</summary>
internal sealed class LookupToolView
{
    public required Tool Tool { get; init; }

    public required LookupTool Binding { get; init; }
}

/// <summary>A web search tool with its binding.</summary>
internal sealed class WebSearchToolView
{
    public required Tool Tool { get; init; }

    public required WebSearchTool Binding { get; init; }
}

/// <summary>A web fetch tool with its binding.</summary>
internal sealed class WebFetchToolView
{
    public required Tool Tool { get; init; }

    public required WebFetchTool Binding { get; init; }
}
