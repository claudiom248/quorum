namespace Quorum.Modules.Agents.Domain;

/// <summary>
/// The binding of a web fetch tool to the platform's web configuration (spec 006): how much a page costs the model, how many
/// URLs one call may take, how long a fetch waits, and who may use the tool. The tool itself (name, description, state, policy,
/// activation) is a <see cref="Tool"/> of origin <see cref="ToolOrigin.WebFetch"/>; this is its configuration, one row per tool.
/// Every value here is an override of the platform's default in <see cref="WebConfiguration"/>.
/// </summary>
internal sealed class WebFetchTool
{
    public const int MinTokenBudgetPerPage = 500;

    public const int MaxTokenBudgetPerPage = 20_000;

    public const int MinTotalTokenBudget = 2_000;

    public const int MaxTotalTokenBudget = 100_000;

    public const int MinMaxUrlsPerInvocation = 1;

    public const int MaxMaxUrlsPerInvocation = 10;

    public const int MinTimeoutSeconds = 5;

    public const int MaxTimeoutSeconds = 120;

    private WebFetchTool()
    {
    }

    public Guid ToolId { get; private set; }

    /// <summary>The owner: the only tenant that can change or delete the tool, whatever its scope.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>How much of one page's text the model reads at most; overrides the platform's default.</summary>
    public int TokenBudgetPerPage { get; private set; }

    /// <summary>How much every page of one invocation costs the model together, at most; overrides the platform's default.</summary>
    public int TotalTokenBudget { get; private set; }

    /// <summary>The most URLs one invocation of the tool may fetch; overrides the platform's default.</summary>
    public int MaxUrlsPerInvocation { get; private set; }

    /// <summary>How long one URL fetch waits before it is called a failure; overrides the platform's default.</summary>
    public int Timeout { get; private set; }

    public ToolScopeKind Scope { get; private set; }

    /// <summary>With <see cref="ToolScopeKind.Tenant"/>: the tenants besides the owner that may use the tool.</summary>
    public List<Guid> ScopeTenantIds { get; private set; } = [];

    /// <summary>With <see cref="ToolScopeKind.Assistant"/>: the owner's assistants that may use the tool.</summary>
    public List<Guid> ScopeAssistantIds { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static WebFetchTool Create(Guid toolId, Guid tenantId, WebFetchToolConfiguration configuration, DateTimeOffset now)
    {
        var tool = new WebFetchTool { ToolId = toolId, TenantId = tenantId, CreatedAt = now };
        tool.Apply(configuration, now);
        return tool;
    }

    public void Apply(WebFetchToolConfiguration configuration, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        TokenBudgetPerPage = configuration.TokenBudgetPerPage;
        TotalTokenBudget = configuration.TotalTokenBudget;
        MaxUrlsPerInvocation = configuration.MaxUrlsPerInvocation;
        Timeout = configuration.Timeout;
        Scope = configuration.Scope;
        ScopeTenantIds = [.. configuration.ScopeTenantIds];
        ScopeAssistantIds = [.. configuration.ScopeAssistantIds];
        UpdatedAt = now;
    }
}

/// <summary>What an administrator configures on a web fetch tool, validated: the content of a <see cref="WebFetchTool"/>.</summary>
internal sealed record WebFetchToolConfiguration(
    int TokenBudgetPerPage,
    int TotalTokenBudget,
    int MaxUrlsPerInvocation,
    int Timeout,
    ToolScopeKind Scope,
    IReadOnlyList<Guid> ScopeTenantIds,
    IReadOnlyList<Guid> ScopeAssistantIds);
