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

    /// <summary>The server of an MCP tool, loaded by the admin listings only (the pool an assistant draws from does not need it).</summary>
    public McpServer? Server { get; init; }

    public string ExposedName => Tool.Origin switch
    {
        ToolOrigin.Builtin => ToolNaming.Builtin(Tool.Name),
        ToolOrigin.Rag => ToolNaming.Rag(Tool.Name),
        ToolOrigin.Lookup => ToolNaming.Lookup(Tool.Name),
        ToolOrigin.WebSearch => ToolNaming.WebSearch(Tool.Name),
        _ => ToolNaming.Mcp(ServerName ?? throw new InvalidOperationException("An MCP tool has a server."), Tool.Name),
    };

    /// <summary>As stored. The schema of a rag or lookup tool is generated when it is used, so this one is only the administrator's description.</summary>
    public ToolDefinition Definition => new(ExposedName, Tool.Description, Tool.InputSchema);
}

/// <summary>
/// The tools an assistant of the current tenant can use (D-2, D-23): enabled built-in tools of the tenant, enabled tools of
/// available MCP servers whose scope covers the assistant, and enabled rag tools whose scope covers it. Activation
/// (<see cref="AssistantTool"/>) picks from this pool.
/// </summary>
internal static class ToolPool
{
    public static IQueryable<PoolTool> For(AgentsDbContext db, Guid assistantId)
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
            where tool.State == ToolState.Enabled
                  && ((tool.Origin == ToolOrigin.Builtin && tool.TenantId == tenantId)
                      || (server != null
                          && server.Status == McpServerStatus.Available
                          && (server.Scope == ToolScopeKind.Global
                              || (server.Scope == ToolScopeKind.Tenant && (server.TenantId == tenantId || server.ScopeTenantIds.Contains(tenantId)))
                              // An owner lists only its own assistants; a foreign owner naming ours reaches nothing.
                              || (server.Scope == ToolScopeKind.Assistant && server.TenantId == tenantId && server.ScopeAssistantIds.Contains(assistantId))))
                      || (rag != null
                          && (rag.Scope == ToolScopeKind.Global
                              || (rag.Scope == ToolScopeKind.Tenant && (rag.TenantId == tenantId || rag.ScopeTenantIds.Contains(tenantId)))
                              || (rag.Scope == ToolScopeKind.Assistant && rag.TenantId == tenantId && rag.ScopeAssistantIds.Contains(assistantId))))
                      || (lookup != null
                          && (lookup.Scope == ToolScopeKind.Global
                              || (lookup.Scope == ToolScopeKind.Tenant && (lookup.TenantId == tenantId || lookup.ScopeTenantIds.Contains(tenantId)))
                              || (lookup.Scope == ToolScopeKind.Assistant && lookup.TenantId == tenantId && lookup.ScopeAssistantIds.Contains(assistantId))))
                      || (webSearch != null
                          && (webSearch.Scope == ToolScopeKind.Global
                              || (webSearch.Scope == ToolScopeKind.Tenant && (webSearch.TenantId == tenantId || webSearch.ScopeTenantIds.Contains(tenantId)))
                              || (webSearch.Scope == ToolScopeKind.Assistant && webSearch.TenantId == tenantId && webSearch.ScopeAssistantIds.Contains(assistantId)))))
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
