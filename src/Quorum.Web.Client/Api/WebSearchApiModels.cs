namespace Quorum.Web.Client.Api;

/// <summary>The web search tool an administrator saves (spec 005).</summary>
/// <param name="Shared"><see langword="true"/> for a tool of another tenant made visible by its scope: read-only, its binding is not shown.</param>
public sealed record WebSearchToolListItem(
    Guid Id,
    string Name,
    string Description,
    ToolScope Scope,
    string State,
    string Policy,
    bool Shared);

public sealed record WebSearchToolDefinition(string Name, string Description, System.Text.Json.JsonElement InputSchema, int EstimatedTokens);

public sealed record WebSearchToolDetail(
    Guid Id,
    string Name,
    string Description,
    int TopN,
    IReadOnlyList<string>? SearchEngines,
    string? Language,
    int SafeSearch,
    string? TimeRange,
    int MaxInvocationsPerConversation,
    ToolScope Scope,
    string State,
    string Policy,
    WebSearchToolDefinition? Definition);

public sealed record WebSearchToolRequest(
    string Name,
    string Description,
    int? TopN,
    IReadOnlyList<string>? SearchEngines,
    string? Language,
    int? SafeSearch,
    string? TimeRange,
    int? MaxInvocationsPerConversation,
    ToolScope Scope,
    string? Policy);

public sealed record CreatedWebSearchTool(Guid Id, WebSearchToolDefinition? Definition);

/// <summary>Limits of a web search tool, as the server enforces them.</summary>
public static class WebSearchToolLimits
{
    public const int DefaultTopN = 5;
    public const int MinTopN = 1;
    public const int MaxTopN = 20;
    public const int DefaultSafeSearch = 1;
    public const int MaxSafeSearch = 2;
    public const int DefaultMaxInvocations = 5;
    public const int MinMaxInvocations = 1;
    public const int MaxMaxInvocations = 20;
    public const int MaxSearchEngines = 10;
    public const int MaxNameLength = 40;
    public const int MaxDescriptionLength = 1_000;
    public const int MaxUrlLength = 2_048;
    public const int DefaultTimeoutSeconds = 90;
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 600;

    /// <summary>The time filters an instance answers with, and what the administrator sees for each.</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> TimeRanges =
        [
            ("day", "One day"),
            ("week", "One week"),
            ("month", "One month"),
            ("year", "One year"),
        ];
}
