using System.Text.RegularExpressions;

namespace Quorum.Modules.Agents.Tools.WebFetch;

/// <summary>
/// The platform's reader of the open web (spec 006): it asks a site with <c>IHttpClientFactory</c>'s client, obeys its
/// <c>robots.txt</c> when the platform is told to, and turns HTML or a PDF into the text the model reads. Everything that is
/// a policy of the platform — the wait between calls, whether to look at <c>robots.txt</c>, who the platform says it is, how
/// long the answer is remembered — comes from the request and not from this class.
/// </summary>
/// <remarks>
/// The cache is not here: the tool that knows the platform's configuration wraps the call with it. The provider is the pure
/// read of one URL, so a test can drive it without a cache in the way.
/// </remarks>
internal sealed partial class HttpWebFetchProvider(IHttpClientFactory clients, RobotsCache robots) : IWebFetchProvider
{
    /// <summary>The name of the <see cref="System.Net.Http.HttpClient"/> used here; the tests replace its handler with a fake.</summary>
    public const string HttpClientName = "quorum-web-fetch";

    private static readonly Regex ContentTypePattern = ContentTypeRegex();

    /// <summary>What the platform waits for a site's robots.txt before it treats the site as permitting everything.</summary>
    private static readonly TimeSpan RobotsTimeout = TimeSpan.FromSeconds(5);

    public async Task<WebFetchResponse> FetchAsync(WebFetchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return WebFetchResponse.Failure(WebFetchOutcome.Failed, "The URL must be an absolute http(s) address.", DateTimeOffset.UtcNow);
        }

        var now = DateTimeOffset.UtcNow;
        if (request.RespectRobotsTxt && !await robots.IsAllowedAsync(request.Url, request.UserAgent, cancellationToken))
        {
            return WebFetchResponse.Failure(WebFetchOutcome.BlockedByRobots, "The site's robots.txt forbids this page.", now);
        }

        var client = clients.CreateClient(HttpClientName);
        if (client.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(request.UserAgent);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));

        try
        {
            using var response = await client.GetAsync(request.Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return await ReadAsync(request, response, now, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WebFetchResponse.Failure(WebFetchOutcome.TimedOut, $"The site did not answer within {request.TimeoutSeconds} seconds.", DateTimeOffset.UtcNow);
        }
        catch (HttpRequestException exception)
        {
            return WebFetchResponse.Failure(WebFetchOutcome.Unreachable, $"The site could not be reached: {exception.Message}", DateTimeOffset.UtcNow);
        }
    }

    private static async Task<WebFetchResponse> ReadAsync(
        WebFetchRequest request,
        HttpResponseMessage response,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            return WebFetchResponse.Failure(WebFetchOutcome.Failed, $"The site answered with HTTP {(int)response.StatusCode}.", now);
        }

        var contentType = ContentTypeOf(response.Content.Headers.ContentType) ?? "text/html";

        if (contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
            || contentType.StartsWith("application/xhtml", StringComparison.OrdinalIgnoreCase))
        {
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var extracted = HtmlContentExtractor.Extract(html, request.Url);
            return extracted.IsSuccess
                ? WebFetchResponse.Ok(contentType, extracted.Value.Title, extracted.Value.Text, now)
                : WebFetchResponse.Failure(WebFetchOutcome.Failed, extracted.Error.Description, now);
        }

        if (contentType.StartsWith("application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var text = Quorum.Shared.Extraction.PdfText.Extract(bytes, cancellationToken);
            return text.IsSuccess
                ? WebFetchResponse.Ok(contentType, TitleFromUrl(request.Url), text.Value, now)
                : WebFetchResponse.Failure(WebFetchOutcome.Failed, text.Error.Description, now);
        }

        // Plain text is what a log or a readme is: the model reads it whole.
        if (contentType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase))
        {
            var plain = await response.Content.ReadAsStringAsync(cancellationToken);
            return WebFetchResponse.Ok(contentType, TitleFromUrl(request.Url), plain, now);
        }

        return WebFetchResponse.Failure(
            WebFetchOutcome.UnsupportedContentType,
            $"This platform reads HTML and PDF pages; the site answered with {contentType}.",
            now);
    }

    /// <summary>What the site's <c>Content-Type</c> header says the body is; the parameters are dropped.</summary>
    private static string? ContentTypeOf(System.Net.Http.Headers.MediaTypeHeaderValue? header)
    {
        var raw = header?.MediaType;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var match = ContentTypePattern.Match(raw);
        return (match.Success ? match.Groups["type"].Value : raw).ToLowerInvariant();
    }

    private static string TitleFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return url;
        }

        var last = parsed.Segments.Length > 1 ? parsed.Segments[^1].Trim('/') : parsed.Host;
        return string.IsNullOrEmpty(last) ? parsed.Host : last;
    }

    [GeneratedRegex(@"^(?<type>[^;\s]+)", RegexOptions.ExplicitCapture)]
    private static partial Regex ContentTypeRegex();
}
