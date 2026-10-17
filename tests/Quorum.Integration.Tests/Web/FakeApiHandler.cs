using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace Quorum.Integration.Tests.Web;

/// <summary>Stands in for the Quorum API for component tests: answers requests from a script and remembers them.</summary>
internal sealed class FakeApiHandler : HttpMessageHandler
{
    private readonly List<(HttpMethod Method, string Path)> _requests = [];
    private readonly List<(HttpMethod Method, string Path, string Body)> _bodies = [];

    /// <summary>The requests received, oldest first.</summary>
    public IReadOnlyList<(HttpMethod Method, string Path)> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public Func<HttpMethod, string, HttpResponseMessage> Respond { get; set; } = (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound);

    /// <summary>The body of the last request that matched, or <see langword="null"/> when there was none.</summary>
    public string? LastBody(HttpMethod method, string pathContains)
    {
        lock (_requests)
        {
            return _bodies.LastOrDefault(r => r.Method == method && r.Path.Contains(pathContains, StringComparison.Ordinal)).Body;
        }
    }

    public HttpClient CreateClient() => new(this) { BaseAddress = new Uri("http://quorum.test/") };

    public int Count(HttpMethod method, string pathContains) =>
        Requests.Count(r => r.Method == method && r.Path.Contains(pathContains, StringComparison.Ordinal));

    public static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(body) };

    /// <summary>A chat answer: the given text as one delta, then the done event.</summary>
    public static HttpResponseMessage Answer(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $"event: delta\ndata: {{\"text\":\"{text}\"}}\n\nevent: done\ndata: {{\"messageId\":\"{Guid.NewGuid()}\",\"status\":\"Completed\",\"error\":null}}\n\n",
            Encoding.UTF8,
            "text/event-stream"),
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (_requests)
        {
            _requests.Add((request.Method, path));
            _bodies.Add((request.Method, path, body));
        }

        return Respond(request.Method, path);
    }
}
