namespace Quorum.Modules.Agents.Domain;

/// <summary>
/// Everything the platform sets once for the open web (specs 005 and 006): the SearXNG instance a search asks, the defaults a web
/// fetch tool reads, and the identity, politeness and cache that every outbound call shares. One row for the whole installation,
/// with no tenant: the administrator of the default tenant edits it, every tenant reads it.
/// </summary>
internal sealed class WebConfiguration
{
    /// <summary>The id of the only row: a fixed key makes a second one impossible, however many administrators save at once.</summary>
    public static readonly Guid SingletonId = new("00000000-0000-0000-0000-000000000001");

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

    // Web properties, shared with the fetch (spec 006).
    public const string DefaultUserAgent = "Quorum/1.0";

    public const int MaxUserAgentLength = 128;

    public const int DefaultFetchDelayMs = 1_000;

    public const int MinFetchDelayMs = 0;

    public const int MaxFetchDelayMs = 10_000;

    public const bool DefaultRespectRobotsTxt = true;

    public const bool DefaultCacheEnabled = true;

    public const int DefaultCacheTtlMinutes = 10;

    public const int MinCacheTtlMinutes = 1;

    public const int MaxCacheTtlMinutes = 60;

    private WebConfiguration()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Where SearXNG answers, as the administrator wrote it; <see langword="null"/> until one is set (spec 005).</summary>
    public string? SearchUrl { get; private set; }

    /// <summary>How long a search waits before it is called a failure.</summary>
    public int SearchTimeoutSeconds { get; private set; }

    /// <summary>How long one URL fetch waits before it is called a failure.</summary>
    public int FetchTimeoutSeconds { get; private set; }

    /// <summary>The most URLs one invocation of the web fetch tool may fetch.</summary>
    public int FetchMaxUrlsPerInvocation { get; private set; }

    /// <summary>How much of one page's text the model reads at most.</summary>
    public int FetchTokenBudgetPerPage { get; private set; }

    /// <summary>How much every page of one invocation costs the model together, at most.</summary>
    public int FetchTotalTokenBudget { get; private set; }

    /// <summary>The identity the platform announces to every web site it asks.</summary>
    public string UserAgent { get; private set; } = null!;

    /// <summary>How long one answer waits between fetching one URL and the next.</summary>
    public int FetchDelayMs { get; private set; }

    /// <summary>Whether the platform reads a site's robots.txt before it fetches it.</summary>
    public bool RespectRobotsTxt { get; private set; }

    /// <summary>Whether fetched pages are remembered for a while so the same URL is not asked twice in a row.</summary>
    public bool CacheEnabled { get; private set; }

    /// <summary>How long a fetched page is remembered.</summary>
    public int CacheTtlMinutes { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>A fresh row with the platform's defaults, filled by the migration and by nobody else.</summary>
    public static WebConfiguration CreateDefault(DateTimeOffset now) => new()
    {
        Id = SingletonId,
        SearchTimeoutSeconds = DefaultSearchTimeoutSeconds,
        FetchTimeoutSeconds = DefaultFetchTimeoutSeconds,
        FetchMaxUrlsPerInvocation = DefaultFetchMaxUrlsPerInvocation,
        FetchTokenBudgetPerPage = DefaultFetchTokenBudgetPerPage,
        FetchTotalTokenBudget = DefaultFetchTotalTokenBudget,
        UserAgent = DefaultUserAgent,
        FetchDelayMs = DefaultFetchDelayMs,
        RespectRobotsTxt = DefaultRespectRobotsTxt,
        CacheEnabled = DefaultCacheEnabled,
        CacheTtlMinutes = DefaultCacheTtlMinutes,
        UpdatedAt = now,
    };

    /// <summary>Every field the administrator can set, as one PUT of the platform's web configuration carries it.</summary>
    public void Change(
        string? searchUrl,
        int searchTimeoutSeconds,
        int fetchTimeoutSeconds,
        int fetchMaxUrlsPerInvocation,
        int fetchTokenBudgetPerPage,
        int fetchTotalTokenBudget,
        string userAgent,
        int fetchDelayMs,
        bool respectRobotsTxt,
        bool cacheEnabled,
        int cacheTtlMinutes,
        DateTimeOffset now)
    {
        SearchUrl = searchUrl;
        SearchTimeoutSeconds = searchTimeoutSeconds;
        FetchTimeoutSeconds = fetchTimeoutSeconds;
        FetchMaxUrlsPerInvocation = fetchMaxUrlsPerInvocation;
        FetchTokenBudgetPerPage = fetchTokenBudgetPerPage;
        FetchTotalTokenBudget = fetchTotalTokenBudget;
        UserAgent = userAgent;
        FetchDelayMs = fetchDelayMs;
        RespectRobotsTxt = respectRobotsTxt;
        CacheEnabled = cacheEnabled;
        CacheTtlMinutes = cacheTtlMinutes;
        UpdatedAt = now;
    }
}
