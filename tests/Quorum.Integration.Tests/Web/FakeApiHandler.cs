using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace Quorum.Integration.Tests.Web;

/// <summary>Stands in for the Quorum API for component tests: answers requests from a script and remembers them.</summary>
internal sealed class FakeApiHandler : HttpMessageHandler
{
    private readonly List<(HttpMethod Method, string Path)> _requests = [];

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

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        lock (_requests)
        {
            _requests.Add((request.Method, path));
        }

        return Task.FromResult(Respond(request.Method, path));
    }
}
