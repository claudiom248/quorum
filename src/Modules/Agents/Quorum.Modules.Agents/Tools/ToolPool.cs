using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;

namespace Quorum.Modules.Agents.Tools;

/// <summary>A tool of a pool with what the model-facing name needs from its server.</summary>
internal sealed class PoolTool
{
    public required Tool Tool { get; init; }

    /// <summary>Null for native tools.</summary>
    public string? ServerName { get; init; }

    public string ExposedName => Tool.Origin == ToolOrigin.Native
        ? ToolNaming.Native(Tool.Name)
        : ToolNaming.Mcp(ServerName ?? throw new InvalidOperationException("An MCP tool has a server."), Tool.Name);

    public ToolDefinition Definition => new(ExposedName, Tool.Description, Tool.InputSchema);
}

/// <summary>
/// The tools an assistant of the current tenant can use (D-2, D-23): enabled native tools of the tenant, and enabled tools of
/// available MCP servers whose scope covers the assistant. Activation (<see cref="AssistantTool"/>) picks from this pool.
/// </summary>
internal static class ToolPool
{
    public static IQueryable<PoolTool> For(AgentsDbContext db, Guid assistantId)
    {
        ArgumentNullException.ThrowIfNull(db);

        // Tools of a server another tenant owns are outside the tenant filter: the scope rule below is what lets them in.
        var tenantId = db.CurrentTenantId ?? Guid.Empty;

        return
            from tool in db.Tools.IgnoreQueryFilters()
            join server in db.McpServers.IgnoreQueryFilters() on tool.ServerId equals server.Id into servers
            from server in servers.DefaultIfEmpty()
            where tool.State == ToolState.Enabled
                  && ((tool.ServerId == null && tool.TenantId == tenantId)
                      || (server != null
                          && server.Status == McpServerStatus.Available
                          && (server.Scope == ToolScopeKind.Global
                              || (server.Scope == ToolScopeKind.Tenant && (server.TenantId == tenantId || server.ScopeTenantIds.Contains(tenantId)))
                              // An owner lists only its own assistants; a foreign owner naming ours reaches nothing.
                              || (server.Scope == ToolScopeKind.Assistant && server.TenantId == tenantId && server.ScopeAssistantIds.Contains(assistantId)))))
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

    /// <summary>The tenant's own tools in any state, and the enabled tools of the servers shared with it (their review belongs to the owner).</summary>
    public static IQueryable<PoolTool> Tools(AgentsDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        var tenantId = db.CurrentTenantId ?? Guid.Empty;

        return
            from tool in db.Tools.IgnoreQueryFilters()
            join server in db.McpServers.IgnoreQueryFilters() on tool.ServerId equals server.Id into servers
            from server in servers.DefaultIfEmpty()
            where tool.TenantId == tenantId
                  || (tool.State == ToolState.Enabled
                      && server != null
                      && (server.Scope == ToolScopeKind.Global
                          || (server.Scope == ToolScopeKind.Tenant && server.ScopeTenantIds.Contains(tenantId))))
            select new PoolTool { Tool = tool, ServerName = server == null ? null : server.Name };
    }
}
