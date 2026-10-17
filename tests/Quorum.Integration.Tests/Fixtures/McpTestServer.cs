using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Quorum.Integration.Tests.Fixtures;

/// <summary>
/// A real MCP server (Streamable HTTP) hosted in this process on a loopback port, with a tool list the test can change
/// and a few misbehaving endpoints: a redirect, an error, a malformed answer and a server that never answers.
/// </summary>
public sealed class McpTestServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private McpTestServer(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; }

    /// <summary>The MCP endpoint.</summary>
    public string Url => BaseUrl + "/mcp";

    public string RedirectUrl => BaseUrl + "/redirect";

    public string ErrorUrl => BaseUrl + "/error";

    public string NotMcpUrl => BaseUrl + "/not-mcp";

    public string SilentUrl => BaseUrl + "/silent";

    /// <summary>What the server lists. Change it between a registration and a refresh.</summary>
    public List<Tool> Tools { get; } = [];

    /// <summary>What <c>tools/call</c> answers; by default <c>called &lt;name&gt;</c>. Tests replace it to return big results, errors or to never answer.</summary>
    public Func<CallToolRequestParams, CancellationToken, ValueTask<CallToolResult>> CallHandler { get; set; } = (request, _) =>
        ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = $"called {request.Name}" }] });

    /// <summary>The tool name and the arguments (as JSON text) of every <c>tools/call</c> received, oldest first.</summary>
    public ConcurrentQueue<(string Name, string Arguments)> Calls { get; } = new();

    /// <summary>The headers of every request that reached the MCP endpoint, as <c>Name: value</c>.</summary>
    public ConcurrentQueue<string> ReceivedHeaders { get; } = new();

    /// <summary>How many requests reached the target a redirect points to.</summary>
    public int RedirectTargetHits => _redirectTargetHits;

    private int _redirectTargetHits;

    public static async Task<McpTestServer> StartAsync(params Tool[] tools)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        McpTestServer? self = null;
        builder.Services
            .AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult { Tools = [.. self!.Tools] }))
            .WithCallToolHandler((context, cancellationToken) =>
            {
                var request = context.Params!;
                self!.Calls.Enqueue((request.Name, JsonSerializer.Serialize(request.Arguments)));
                return self.CallHandler(request, cancellationToken);
            });

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/mcp", StringComparison.Ordinal))
            {
                foreach (var header in context.Request.Headers)
                {
                    self!.ReceivedHeaders.Enqueue($"{header.Key}: {header.Value}");
                }
            }

            await next();
        });
        app.MapMcp("/mcp");
        app.MapMethods("/redirect", ["GET", "POST"], (HttpContext context) =>
            Results.Redirect($"{context.Request.Scheme}://{context.Request.Host}/redirect-target", permanent: false, preserveMethod: true));
        app.MapMethods("/redirect-target", ["GET", "POST"], () =>
        {
            Interlocked.Increment(ref self!._redirectTargetHits);
            return Results.Ok();
        });
        app.MapMethods("/error", ["GET", "POST"], () => Results.StatusCode(StatusCodes.Status500InternalServerError));
        app.MapMethods("/not-mcp", ["GET", "POST"], () => Results.Text("{ this is not json-rpc", "application/json"));
        app.MapMethods("/silent", ["GET", "POST"], async (HttpContext context) => await Task.Delay(Timeout.Infinite, context.RequestAborted));

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        self = new McpTestServer(app, address.TrimEnd('/'));
        self.Tools.AddRange(tools);
        return self;
    }

    public static Tool NewTool(string name, string description = "A tool.", string? schema = null, bool? readOnly = null, bool? destructive = null) => new()
    {
        Name = name,
        Description = description,
        InputSchema = JsonDocument.Parse(schema ?? """{"type":"object","properties":{"text":{"type":"string"}}}""").RootElement.Clone(),
        Annotations = readOnly is null && destructive is null
            ? null
            : new ToolAnnotations { ReadOnlyHint = readOnly, DestructiveHint = destructive },
    };

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
