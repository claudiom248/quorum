namespace Quorum.Modules.Agents.Domain;

/// <summary>
/// The SearXNG instance the platform searches with (spec 005). One row for the whole installation, with no tenant: the administrator
/// of the default tenant sets it and every tenant's web search tools read it. A tool cannot be saved while the URL is missing.
/// </summary>
internal sealed class WebSearchConfiguration
{
    /// <summary>The id of the only row: a fixed key makes a second one impossible, however many administrators save at once.</summary>
    public static readonly Guid SingletonId = new("00000000-0000-0000-0000-000000000001");

    public const int DefaultTimeoutSeconds = 90;

    public const int MinTimeoutSeconds = 1;

    public const int MaxTimeoutSeconds = 600;

    private WebSearchConfiguration()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Where SearXNG answers, as the administrator wrote it; <see langword="null"/> until one is set.</summary>
    public string? Url { get; private set; }

    /// <summary>How long a search waits before it is called a failure.</summary>
    public int TimeoutSeconds { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static WebSearchConfiguration Create(string url, int timeoutSeconds, DateTimeOffset now) => new()
    {
        Id = SingletonId,
        Url = url,
        TimeoutSeconds = timeoutSeconds,
        UpdatedAt = now,
    };

    public void Change(string url, int timeoutSeconds, DateTimeOffset now)
    {
        Url = url;
        TimeoutSeconds = timeoutSeconds;
        UpdatedAt = now;
    }
}
