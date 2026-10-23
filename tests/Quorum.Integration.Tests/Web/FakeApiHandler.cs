using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace Quorum.Integration.Tests.Web;

/// <summary>Stands in for the Quorum API for component tests: answers requests from a script and remembers them.</summary>
internal sealed class FakeApiHandler : HttpMessageHandler
{
    private readonly List<(HttpMethod Method, string Path)> _requests = [];
    private readonly List<(HttpMethod Method, string Path, string Body)> _bodies = [];
    private readonly List<string> _queries = [];

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

    /// <summary>The query strings received, oldest first (the paths in <see cref="Requests"/> have none).</summary>
    public IReadOnlyList<string> Queries
    {
        get
        {
            lock (_requests)
            {
                return [.. _queries];
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
            _queries.Add(request.RequestUri.Query);
        }

        var response = Respond(request.Method, path);
        return request.Method == HttpMethod.Get && ListRoutes.Contains(path) && response.IsSuccessStatusCode
            ? await AsPageAsync(response, cancellationToken)
            : response;
    }

    private static readonly HashSet<string> ListRoutes =
        ["/api/knowledge-bases", "/api/knowledge-bases/dictionary", "/api/knowledge-bases/manual-lookups", "/api/tools/servers", "/api/tools/rag", "/api/tools/lookup"];

    /// <summary>
    /// The scripts below answer these lists with a bare array and a scope written as text: the server now sends a page and a scope
    /// object, so the stand-in turns one into the other and the scripts keep saying only what a test is about.
    /// </summary>
    private static async Task<HttpResponseMessage> AsPageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var items = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (items is not JsonArray array)
        {
            return response;
        }

        foreach (var item in array.OfType<JsonObject>())
        {
            if (item["scope"] is JsonValue scope && scope.TryGetValue<string>(out var kind))
            {
                var tenants = item["scopeTenantIds"]?.DeepClone() ?? new JsonArray();
                item.Remove("scopeTenantIds");
                item["scope"] = new JsonObject { ["kind"] = kind, ["tenantIds"] = tenants, ["assistantIds"] = new JsonArray() };
            }
        }

        return Json(new JsonObject { ["items"] = array, ["cursor"] = null }, response.StatusCode);
    }
}
