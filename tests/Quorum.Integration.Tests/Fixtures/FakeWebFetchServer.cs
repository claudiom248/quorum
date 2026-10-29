using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Quorum.Integration.Tests.Fixtures;

/// <summary>
/// The site a web fetch reads: it records every URL it was asked for and answers with what the test scripted — an HTML page, a
/// PDF, another kind of content, an HTTP error, a robots.txt, or nothing at all (for a timeout).
/// </summary>
public sealed class FakeWebFetchServer : HttpMessageHandler
{
    private readonly Dictionary<string, Scripted> _routes = new(StringComparer.OrdinalIgnoreCase);
    private string? _robots;

    internal sealed record Scripted(HttpStatusCode Status, byte[] Body, string? ContentType, bool Hang);

    /// <summary>Every request that reached the site, oldest first.</summary>
    public sealed record Recorded(Uri Url, string UserAgent);

    public ConcurrentQueue<Recorded> Requests { get; } = new();

    /// <summary>How many URLs have been asked, robots.txt fetches included.</summary>
    public int Attempts => Requests.Count;

    /// <summary>The URLs asked, in order, without their query strings.</summary>
    public IReadOnlyList<string> Urls => [.. Requests.Select(r => r.Url.ToString())];

    /// <summary>The User-Agent of the nth request (0 is the first), as the site saw it.</summary>
    public string UserAgentOf(int index) => Requests.ToArray()[index].UserAgent;

    /// <summary>The HTML page the site gives for the given URL.</summary>
    public void ServeHtml(string url, string html, HttpStatusCode status = HttpStatusCode.OK) =>
        Put(url, status, Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8");

    /// <summary>The PDF the site gives for the given URL: bytes as the platform will see them.</summary>
    public void ServePdf(string url, byte[] pdf, HttpStatusCode status = HttpStatusCode.OK) =>
        Put(url, status, pdf, "application/pdf");

    /// <summary>Whatever the site gives for the given URL: a media type Quorum may refuse, a broken status.</summary>
    public void Serve(string url, HttpStatusCode status, byte[] body, string? contentType) =>
        Put(url, status, body, contentType);

    /// <summary>Never answers this URL: the request waits until the caller gives up on it.</summary>
    public void NeverAnswers(string url) => Put(url, HttpStatusCode.OK, [], "text/html", hang: true);

    /// <summary>The robots.txt the site answers at its origin, or nothing when the test does not set one.</summary>
    public void Robots(string? content) => _robots = content;

    /// <summary>
    /// Answers any URL that no specific test scripted. It lets a batch that mixes known and unknown addresses keep going
    /// without the test having to script every site the platform touches (robots fetches come first, for example).
    /// </summary>
    internal Func<Uri, Scripted?>? OnUnknown { get; set; }

    public void Reset()
    {
        _routes.Clear();
        _robots = null;
        OnUnknown = null;
        while (!Requests.IsEmpty)
        {
            Requests.TryDequeue(out _);
        }
    }

    private void Put(string url, HttpStatusCode status, byte[] body, string? contentType, bool hang = false)
    {
        var key = new Uri(url, UriKind.Absolute).ToString();
        _routes[key] = new Scripted(status, body, contentType, hang);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var userAgent = request.Headers.UserAgent.ToString();
        Requests.Enqueue(new Recorded(request.RequestUri!, userAgent));

        if (request.RequestUri!.AbsolutePath.Equals("/robots.txt", StringComparison.OrdinalIgnoreCase))
        {
            return _robots is null
                ? NotFound()
                : Text(HttpStatusCode.OK, _robots, "text/plain");
        }

        var key = request.RequestUri!.ToString();
        var script = _routes.TryGetValue(key, out var known) ? known : OnUnknown?.Invoke(request.RequestUri);
        if (script is null)
        {
            throw new InvalidOperationException($"The test has not scripted what the site answers for {key}.");
        }

        if (script.Hang)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        var response = new HttpResponseMessage(script.Status) { Content = new ByteArrayContent(script.Body) };
        if (script.ContentType is not null)
        {
            response.Content.Headers.TryAddWithoutValidation("Content-Type", script.ContentType);
        }

        return response;
    }

    private static HttpResponseMessage Text(HttpStatusCode status, string body, string contentType) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };

    private static HttpResponseMessage NotFound() =>
        new(HttpStatusCode.NotFound) { Content = new StringContent(string.Empty) };
}
