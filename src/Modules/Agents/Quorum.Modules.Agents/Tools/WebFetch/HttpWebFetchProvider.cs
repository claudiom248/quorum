using System.Text;
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
internal sealed partial class HttpWebFetchProvider(IHttpClientFactory clients, RobotsCache robots, TimeProvider clock) : IWebFetchProvider
{
    /// <summary>The name of the <see cref="System.Net.Http.HttpClient"/> used here; the tests replace its handler with a fake.</summary>
    public const string HttpClientName = "quorum-web-fetch";

    /// <summary>
    /// The most bytes the platform takes from one address. What the model reads is cut to a token budget long before this, so the
    /// ceiling is not about the answer: it is about not holding somebody's gigabyte download in memory. A declared length is only
    /// a claim — a chunked answer says nothing — so the body is also counted while it is read.
    /// </summary>
    internal const int MaxResponseBytes = 5 * 1024 * 1024;

    /// <summary>What the platform says about a page it will not take whole.</summary>
    private static readonly string TooLargeMessage = $"The page is bigger than the {MaxResponseBytes / (1024 * 1024)} MB the platform reads.";

    private static readonly Regex ContentTypePattern = ContentTypeRegex();

    public async Task<WebFetchResponse> FetchAsync(WebFetchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = clock.GetUtcNow();
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return WebFetchResponse.Failure(WebFetchOutcome.Failed, "The URL must be an absolute http(s) address.", now);
        }

        if (request.RespectRobotsTxt && !await robots.IsAllowedAsync(request.Url, request.UserAgent, cancellationToken))
        {
            return WebFetchResponse.Failure(WebFetchOutcome.BlockedByRobots, "The site's robots.txt forbids this page.", now);
        }

        var client = clients.CreateClient(HttpClientName);
        if (client.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(request.UserAgent);
        }

        // The wait covers the whole read, body included: a site that drips bytes is as late as one that never answers, so the
        // token below is what the reading runs on, not just the arrival of the headers.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));

        try
        {
            using var response = await client.GetAsync(request.Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return await ReadAsync(request, response, now, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WebFetchResponse.Failure(WebFetchOutcome.TimedOut, $"The site did not answer within {request.TimeoutSeconds} seconds.", clock.GetUtcNow());
        }
        catch (HttpRequestException exception)
        {
            return WebFetchResponse.Failure(WebFetchOutcome.Unreachable, $"The site could not be reached: {exception.Message}", clock.GetUtcNow());
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

        if (response.Content.Headers.ContentLength > MaxResponseBytes)
        {
            return WebFetchResponse.Failure(WebFetchOutcome.Failed, TooLargeMessage, now);
        }

        var contentType = ContentTypeOf(response.Content.Headers.ContentType) ?? "text/html";
        var bytes = await ReadCappedAsync(response, cancellationToken);
        if (bytes is null)
        {
            return WebFetchResponse.Failure(WebFetchOutcome.Failed, TooLargeMessage, now);
        }

        if (contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
            || contentType.StartsWith("application/xhtml", StringComparison.OrdinalIgnoreCase))
        {
            var extracted = HtmlContentExtractor.Extract(Decode(response, bytes), request.Url);
            return extracted.IsSuccess
                ? WebFetchResponse.Ok(contentType, extracted.Value.Title, extracted.Value.Text, now)
                : WebFetchResponse.Failure(WebFetchOutcome.Failed, extracted.Error.Description, now);
        }

        if (contentType.StartsWith("application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            var text = Quorum.Shared.Extraction.PdfText.Extract(bytes, cancellationToken);
            return text.IsSuccess
                ? WebFetchResponse.Ok(contentType, TitleFromUrl(request.Url), text.Value, now)
                : WebFetchResponse.Failure(WebFetchOutcome.Failed, text.Error.Description, now);
        }

        // Plain text is what a log or a readme is: the model reads it whole.
        if (contentType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase))
        {
            return WebFetchResponse.Ok(contentType, TitleFromUrl(request.Url), Decode(response, bytes), now);
        }

        return WebFetchResponse.Failure(
            WebFetchOutcome.UnsupportedContentType,
            $"This platform reads HTML and PDF pages; the site answered with {contentType}.",
            now);
    }

    /// <summary>The body, or <see langword="null"/> once it grows past what the platform reads.</summary>
    private static async Task<byte[]?> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var body = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (body.Length + read > MaxResponseBytes)
            {
                return null;
            }

            body.Write(chunk, 0, read);
        }

        return body.ToArray();
    }

    /// <summary>The body as the site said to read it; a character set this platform does not know is taken for UTF-8.</summary>
    private static string Decode(HttpResponseMessage response, byte[] bytes)
    {
        var charset = response.Content.Headers.ContentType?.CharSet;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                return Encoding.GetEncoding(charset).GetString(bytes);
            }
            catch (ArgumentException)
            {
                // An unknown name is not a reason to lose the page: UTF-8 is what almost every site means.
            }
        }

        return Encoding.UTF8.GetString(bytes);
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
