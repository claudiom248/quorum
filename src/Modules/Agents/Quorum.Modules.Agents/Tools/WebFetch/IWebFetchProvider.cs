namespace Quorum.Modules.Agents.Tools.WebFetch;

/// <summary>What the web fetch tool asks: one URL with the timeout and the identity the platform announces.</summary>
/// <param name="Url">The page to read.</param>
/// <param name="TimeoutSeconds">How long this URL may take before it is called a failure.</param>
/// <param name="UserAgent">What the platform announces to the site.</param>
/// <param name="RespectRobotsTxt">Whether the site's robots.txt has to be checked before the URL is fetched.</param>
internal sealed record WebFetchRequest(string Url, int TimeoutSeconds, string UserAgent, bool RespectRobotsTxt);

/// <summary>What happened when a URL was asked.</summary>
public enum WebFetchOutcome
{
    /// <summary>The page came back and its content was read.</summary>
    Ok,

    /// <summary>The page did not answer within the timeout.</summary>
    TimedOut,

    /// <summary>The page could not be reached at all.</summary>
    Unreachable,

    /// <summary>The site's robots.txt says this platform may not read this URL.</summary>
    BlockedByRobots,

    /// <summary>The answer is not HTML or PDF, so Quorum has nothing to read from it.</summary>
    UnsupportedContentType,

    /// <summary>Something else: the HTTP status is not a success, or the text could not be extracted.</summary>
    Failed,
}

/// <summary>What the platform got from one URL: the content that the model reads, and the metadata for citing it.</summary>
/// <param name="Outcome">Whether the fetch succeeded, and if not, why.</param>
/// <param name="ContentType">The media type of the answer, as the site gave it (with no parameters).</param>
/// <param name="Title">The page's own title, or the last part of the URL when the page has none.</param>
/// <param name="Content">The text the model reads; empty when <paramref name="Outcome"/> is not <see cref="WebFetchOutcome.Ok"/>.</param>
/// <param name="FetchedAt">When the text was fetched: shown to the model when the answer came from the cache.</param>
/// <param name="FailureMessage">What to say to the model when <paramref name="Outcome"/> is not <see cref="WebFetchOutcome.Ok"/>.</param>
internal sealed record WebFetchResponse(
    WebFetchOutcome Outcome,
    string? ContentType,
    string? Title,
    string Content,
    DateTimeOffset FetchedAt,
    string? FailureMessage = null)
{
    public static WebFetchResponse Ok(string contentType, string title, string content, DateTimeOffset fetchedAt) =>
        new(WebFetchOutcome.Ok, contentType, title, content, fetchedAt);

    public static WebFetchResponse Failure(WebFetchOutcome outcome, string message, DateTimeOffset fetchedAt) =>
        new(outcome, null, null, string.Empty, fetchedAt, message);
}

/// <summary>How the platform reads a page (or a PDF) from an URL.</summary>
internal interface IWebFetchProvider
{
    Task<WebFetchResponse> FetchAsync(WebFetchRequest request, CancellationToken cancellationToken);
}
