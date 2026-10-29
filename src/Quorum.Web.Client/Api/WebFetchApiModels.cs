namespace Quorum.Web.Client.Api;

/// <summary>The web fetch tool an administrator saves (spec 006).</summary>
/// <param name="Shared"><see langword="true"/> for a tool of another tenant made visible by its scope: read-only, its binding is not shown.</param>
public sealed record WebFetchToolListItem(
    Guid Id,
    string Name,
    string Description,
    ToolScope Scope,
    string State,
    string Policy,
    bool Shared);

public sealed record WebFetchToolDefinition(string Name, string Description, System.Text.Json.JsonElement InputSchema, int EstimatedTokens);

/// <param name="TokenBudgetPerPage">The tool's own override, or <see langword="null"/> when it takes what the platform's web row says.</param>
public sealed record WebFetchToolDetail(
    Guid Id,
    string Name,
    string Description,
    int? TokenBudgetPerPage,
    int? TotalTokenBudget,
    int? MaxUrlsPerInvocation,
    int? Timeout,
    ToolScope Scope,
    string State,
    string Policy,
    WebFetchToolDefinition? Definition);

public sealed record WebFetchToolRequest(
    string Name,
    string Description,
    int? TokenBudgetPerPage,
    int? TotalTokenBudget,
    int? MaxUrlsPerInvocation,
    int? Timeout,
    ToolScope Scope,
    string? Policy);

public sealed record CreatedWebFetchTool(Guid Id, WebFetchToolDefinition? Definition);

/// <summary>Limits of a web fetch tool, as the server enforces them (spec 006).</summary>
public static class WebFetchToolLimits
{
    public const int MinTokenBudgetPerPage = 500;
    public const int MaxTokenBudgetPerPage = 20_000;
    public const int MinTotalTokenBudget = 2_000;
    public const int MaxTotalTokenBudget = 100_000;
    public const int MinMaxUrlsPerInvocation = 1;
    public const int MaxMaxUrlsPerInvocation = 10;
    public const int MinTimeoutSeconds = 5;
    public const int MaxTimeoutSeconds = 120;
    public const int MaxNameLength = 40;
    public const int MaxDescriptionLength = 1_000;
}
