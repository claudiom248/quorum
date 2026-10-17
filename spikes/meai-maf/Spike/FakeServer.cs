using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

/// <summary>A scripted OpenAI-compatible server: each request pops the next "round" (a list of raw SSE data payloads).</summary>
sealed class FakeServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly Queue<string[]> _rounds = new();
    public List<string> Requests { get; } = [];
    public Uri BaseUrl { get; }

    private FakeServer(WebApplication app, Uri url) { _app = app; BaseUrl = url; }

    public void Enqueue(params string[] chunks) => _rounds.Enqueue(chunks);

    public static async Task<FakeServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        FakeServer? self = null;
        app.MapPost("/v1/chat/completions", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            self!.Requests.Add(await reader.ReadToEndAsync());
            ctx.Response.ContentType = "text/event-stream";
            var round = self._rounds.Count > 0 ? self._rounds.Dequeue() : [Delta("content", "(script exhausted)")];
            foreach (var chunk in round)
            {
                await ctx.Response.WriteAsync($"data: {chunk}\n\n");
                await ctx.Response.Body.FlushAsync();
            }
            await ctx.Response.WriteAsync("data: [DONE]\n\n");
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        self = new FakeServer(app, new Uri(address + "/v1"));
        return self;
    }

    public static string Chunk(string deltaJson, string? finish = null) =>
        $$"""{"id":"c1","object":"chat.completion.chunk","model":"fake","choices":[{"index":0,"delta":{{deltaJson}},"finish_reason":{{(finish is null ? "null" : $"\"{finish}\"")}}}]}""";

    public static string Delta(string field, string text) => Chunk($$"""{"{{field}}":{{System.Text.Json.JsonSerializer.Serialize(text)}}}""");

    public static string ToolFragment(object fragment) => Chunk("{\"tool_calls\":[" + System.Text.Json.JsonSerializer.Serialize(fragment) + "]}");

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
