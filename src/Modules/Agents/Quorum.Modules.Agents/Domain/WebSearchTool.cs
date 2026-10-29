namespace Quorum.Modules.Agents.Domain;

/// <summary>
/// The binding of a web search tool to the platform's SearXNG instance (spec 005): how many results a search returns, which engines
/// answer it, in which language and with which limits, and who may use the tool. The tool itself (name, description, state, policy,
/// activation) is a <see cref="Tool"/> of origin <see cref="ToolOrigin.WebSearch"/>; this is its configuration, one row per tool.
/// </summary>
internal sealed class WebSearchTool
{
    public const int DefaultTopN = 5;

    public const int MinTopN = 1;

    public const int MaxTopN = 20;

    /// <summary>0 for no filter, 1 for moderate, 2 for strict, as SearXNG numbers its safe search levels.</summary>
    public const int DefaultSafeSearch = 1;

    public const int MaxSafeSearch = 2;

    public const int DefaultMaxInvocationsPerConversation = 5;

    public const int MinMaxInvocationsPerConversation = 1;

    public const int MaxMaxInvocationsPerConversation = 20;

    public const int MaxSearchEngines = 10;

    /// <summary>The time filters SearXNG answers with; anything else would be silently ignored by it.</summary>
    public static readonly IReadOnlyList<string> TimeRanges = ["day", "week", "month", "year"];

    private WebSearchTool()
    {
    }

    public Guid ToolId { get; private set; }

    /// <summary>The owner: the only tenant that can change or delete the tool, whatever its scope.</summary>
    public Guid TenantId { get; private set; }

    public int TopN { get; private set; }

    /// <summary>The engines to ask, by their name on the instance; <see langword="null"/> asks all of the ones it enables.</summary>
    public List<string>? SearchEngines { get; private set; }

    /// <summary>The language of the results, as a two-letter code with an optional region; <see langword="null"/> leaves it to the instance.</summary>
    public string? Language { get; private set; }

    public int SafeSearch { get; private set; }

    /// <summary>How far back the results may be: one of <see cref="TimeRanges"/>, or <see langword="null"/> for no limit.</summary>
    public string? TimeRange { get; private set; }

    /// <summary>How many times one conversation may call the tool; after that the model is told it stopped.</summary>
    public int MaxInvocationsPerConversation { get; private set; }

    public ToolScopeKind Scope { get; private set; }

    /// <summary>With <see cref="ToolScopeKind.Tenant"/>: the tenants besides the owner that may use the tool.</summary>
    public List<Guid> ScopeTenantIds { get; private set; } = [];

    /// <summary>With <see cref="ToolScopeKind.Assistant"/>: the owner's assistants that may use the tool.</summary>
    public List<Guid> ScopeAssistantIds { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static WebSearchTool Create(Guid toolId, Guid tenantId, WebSearchToolConfiguration configuration, DateTimeOffset now)
    {
        var tool = new WebSearchTool { ToolId = toolId, TenantId = tenantId, CreatedAt = now };
        tool.Apply(configuration, now);
        return tool;
    }

    public void Apply(WebSearchToolConfiguration configuration, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        TopN = configuration.TopN;
        SearchEngines = configuration.SearchEngines is { Count: > 0 } ? [.. configuration.SearchEngines] : null;
        Language = configuration.Language;
        SafeSearch = configuration.SafeSearch;
        TimeRange = configuration.TimeRange;
        MaxInvocationsPerConversation = configuration.MaxInvocationsPerConversation;
        Scope = configuration.Scope;
        ScopeTenantIds = [.. configuration.ScopeTenantIds];
        ScopeAssistantIds = [.. configuration.ScopeAssistantIds];
        UpdatedAt = now;
    }
}

/// <summary>What an administrator configures on a web search tool, validated: the content of a <see cref="WebSearchTool"/>.</summary>
internal sealed record WebSearchToolConfiguration(
    int TopN,
    IReadOnlyList<string>? SearchEngines,
    string? Language,
    int SafeSearch,
    string? TimeRange,
    int MaxInvocationsPerConversation,
    ToolScopeKind Scope,
    IReadOnlyList<Guid> ScopeTenantIds,
    IReadOnlyList<Guid> ScopeAssistantIds);
