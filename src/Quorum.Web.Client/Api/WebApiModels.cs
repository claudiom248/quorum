namespace Quorum.Web.Client.Api;

/// <summary>The one web configuration of the platform, shared by web search and web fetch (specs 005, 006).</summary>
/// <param name="SearchUrl">Where SearXNG answers; <see langword="null"/> while nobody has set one.</param>
/// <param name="SearchTimeoutSeconds">How many seconds one search may take, retries included.</param>
/// <param name="FetchTimeoutSeconds">How many seconds one URL fetch waits before it is called a failure.</param>
/// <param name="FetchMaxUrlsPerInvocation">The most URLs one invocation of the web fetch tool may fetch.</param>
/// <param name="FetchTokenBudgetPerPage">How much of one page's text the model reads at most.</param>
/// <param name="FetchTotalTokenBudget">How much every page of one invocation costs the model together, at most.</param>
/// <param name="UserAgent">The identity the platform announces to every web site it asks.</param>
/// <param name="FetchDelayMs">How long one answer waits between fetching one URL and the next.</param>
/// <param name="RespectRobotsTxt">Whether the platform reads a site's robots.txt before it fetches it.</param>
/// <param name="CacheEnabled">Whether fetched pages are remembered for a while.</param>
/// <param name="CacheTtlMinutes">How long a fetched page is remembered.</param>
public sealed record WebConfigurationInfo(
    string? SearchUrl,
    int SearchTimeoutSeconds,
    int FetchTimeoutSeconds,
    int FetchMaxUrlsPerInvocation,
    int FetchTokenBudgetPerPage,
    int FetchTotalTokenBudget,
    string UserAgent,
    int FetchDelayMs,
    bool RespectRobotsTxt,
    bool CacheEnabled,
    int CacheTtlMinutes);

/// <summary>Limits of the platform's one web configuration, as the server enforces them.</summary>
public static class WebConfigurationLimits
{
    // Search (spec 005).
    public const int DefaultSearchTimeoutSeconds = 90;
    public const int MinSearchTimeoutSeconds = 1;
    public const int MaxSearchTimeoutSeconds = 600;

    // Fetch defaults (spec 006).
    public const int DefaultFetchTimeoutSeconds = 30;
    public const int MinFetchTimeoutSeconds = 5;
    public const int MaxFetchTimeoutSeconds = 120;
    public const int DefaultFetchMaxUrlsPerInvocation = 5;
    public const int MinFetchMaxUrlsPerInvocation = 1;
    public const int MaxFetchMaxUrlsPerInvocation = 10;
    public const int DefaultFetchTokenBudgetPerPage = 4_000;
    public const int MinFetchTokenBudgetPerPage = 500;
    public const int MaxFetchTokenBudgetPerPage = 20_000;
    public const int DefaultFetchTotalTokenBudget = 20_000;
    public const int MinFetchTotalTokenBudget = 2_000;
    public const int MaxFetchTotalTokenBudget = 100_000;

    // Web properties (spec 006).
    public const string DefaultUserAgent = "Quorum/1.0";
    public const int MaxUserAgentLength = 128;
    public const int DefaultFetchDelayMs = 1_000;
    public const int MinFetchDelayMs = 0;
    public const int MaxFetchDelayMs = 10_000;
    public const int DefaultCacheTtlMinutes = 10;
    public const int MinCacheTtlMinutes = 1;
    public const int MaxCacheTtlMinutes = 60;

    /// <summary>The whole default configuration, ready to have a few fields changed (with <c>with</c>).</summary>
    public static WebConfigurationInfo Defaults { get; } = new(
        SearchUrl: null,
        SearchTimeoutSeconds: DefaultSearchTimeoutSeconds,
        FetchTimeoutSeconds: DefaultFetchTimeoutSeconds,
        FetchMaxUrlsPerInvocation: DefaultFetchMaxUrlsPerInvocation,
        FetchTokenBudgetPerPage: DefaultFetchTokenBudgetPerPage,
        FetchTotalTokenBudget: DefaultFetchTotalTokenBudget,
        UserAgent: DefaultUserAgent,
        FetchDelayMs: DefaultFetchDelayMs,
        RespectRobotsTxt: true,
        CacheEnabled: true,
        CacheTtlMinutes: DefaultCacheTtlMinutes);
}
