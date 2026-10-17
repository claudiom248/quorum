namespace Quorum.Modules.Agents.Domain;

internal enum McpServerStatus
{
    Available,

    /// <summary>Could not be reached at the last attempt: it offers no tools to any model until a refresh succeeds.</summary>
    Unavailable,
}

/// <summary>Who may use a server's tools (D-2). <see cref="Agent"/> is reserved for spec 005 and rejected until then.</summary>
internal enum ToolScopeKind
{
    Global,
    Tenant,
    Assistant,
    Agent,
}

/// <summary>An MCP server over Streamable HTTP registered by an admin. Its tools are discovered, reviewed and then activated per assistant.</summary>
internal sealed class McpServer
{
    private McpServer()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>The owner: the only tenant that can change or delete the server, whatever its scope.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>Part of the name the model sees (<c>&lt;name&gt;__&lt;tool&gt;</c>).</summary>
    public string Name { get; private set; } = null!;

    public string Url { get; private set; } = null!;

    /// <summary>The static request headers as one JSON object, encrypted; <see langword="null"/> when there are none.</summary>
    public string? ProtectedHeaders { get; private set; }

    public McpServerStatus Status { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset? LastRefreshedAt { get; private set; }

    public ToolScopeKind Scope { get; private set; }

    /// <summary>With <see cref="ToolScopeKind.Tenant"/>: the tenants besides the owner that may use the server's tools.</summary>
    public List<Guid> ScopeTenantIds { get; private set; } = [];

    /// <summary>With <see cref="ToolScopeKind.Assistant"/>: the assistants that may use the server's tools.</summary>
    public List<Guid> ScopeAssistantIds { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static McpServer Create(
        Guid tenantId,
        string name,
        string url,
        string? protectedHeaders,
        ToolScopeKind scope,
        IEnumerable<Guid> scopeTenantIds,
        IEnumerable<Guid> scopeAssistantIds,
        DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(now),
            TenantId = tenantId,
            Name = name,
            Url = url,
            ProtectedHeaders = protectedHeaders,
            Status = McpServerStatus.Unavailable,
            Scope = scope,
            ScopeTenantIds = [.. scopeTenantIds],
            ScopeAssistantIds = [.. scopeAssistantIds],
            CreatedAt = now,
            UpdatedAt = now,
        };

    /// <param name="protectedHeaders">The new encrypted headers, or <see langword="null"/> to remove them; callers keep the old ones by passing <see cref="ProtectedHeaders"/>.</param>
    public void Edit(
        string name,
        string url,
        string? protectedHeaders,
        ToolScopeKind scope,
        IEnumerable<Guid> scopeTenantIds,
        IEnumerable<Guid> scopeAssistantIds,
        DateTimeOffset now)
    {
        Name = name;
        Url = url;
        ProtectedHeaders = protectedHeaders;
        Scope = scope;
        ScopeTenantIds = [.. scopeTenantIds];
        ScopeAssistantIds = [.. scopeAssistantIds];
        UpdatedAt = now;
    }

    public void MarkAvailable(DateTimeOffset now)
    {
        Status = McpServerStatus.Available;
        LastError = null;
        LastRefreshedAt = now;
    }

    public void MarkUnavailable(string error, DateTimeOffset now)
    {
        Status = McpServerStatus.Unavailable;
        LastError = error;
        LastRefreshedAt = now;
    }
}
