using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

/// <summary>Q4: ModelContextProtocol client tools (McpClientTool : AIFunction) against our requirements.</summary>
static class Experiment4
{
    [McpServerToolType]
    public sealed class DemoTools
    {
        public static List<string> Seen { get; } = [];
        public static int Cancelled;

        [McpServerTool(Name = "echo", ReadOnly = true), Description("Echoes the text.")]
        public static string Echo([Description("Text to echo")] string text) => text;

        [McpServerTool(Name = "big"), Description("Returns n characters.")]
        public static string Big(int n) => new('y', n);

        [McpServerTool(Name = "slow"), Description("Waits ms milliseconds.")]
        public static async Task<string> Slow(int ms, CancellationToken ct)
        {
            try { await Task.Delay(ms, ct); return "done"; }
            catch (OperationCanceledException) { Interlocked.Increment(ref Cancelled); throw; }
        }

        [McpServerTool(Name = "fail"), Description("Always fails.")]
        public static string Fail() => throw new InvalidOperationException("boom from the server");

        [McpServerTool(Name = "who"), Description("Reports the caller's headers and declared capabilities.")]
        public static string Who(McpServer server, Microsoft.AspNetCore.Http.IHttpContextAccessor accessor)
        {
            var caps = server.ClientCapabilities;
            var auth = accessor.HttpContext?.Request.Headers.Authorization.ToString();
            return $"auth='{auth}' sampling={caps?.Sampling is not null} roots={caps?.Roots is not null} elicitation={caps?.Elicitation is not null} client={server.ClientInfo?.Name}";
        }
    }

    static async Task<(Microsoft.AspNetCore.Builder.WebApplication App, Uri Url)> StartServerAsync()
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<DemoTools>();
        var app = builder.Build();
        app.MapMcp("/mcp");
        app.MapMethods("/redirect", ["GET", "POST", "DELETE"], (HttpContext c) => Results.Redirect("http://localhost:" + c.Connection.LocalPort + "/mcp", false, true));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (app, new Uri(address));
    }

    /// <summary>The URL guard of plan 004 (D-18): resolves, rejects blocked addresses and connects to the validated IP.</summary>
    static SocketsHttpHandler GuardedHandler(Func<string, IPAddress[]>? resolver = null) => new()
    {
        AllowAutoRedirect = false,
        ConnectCallback = async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = resolver is not null ? resolver(host) : await Dns.GetHostAddressesAsync(host, ct);
            var allowed = addresses.Where(a => !IsBlocked(a)).ToArray();
            if (allowed.Length == 0) throw new HttpRequestException($"url_blocked: {host} resolves to a blocked address");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(allowed[0], context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        },
    };

    // For the spike 127.0.0.2 stands for 169.254.169.254 (a second loopback address is reachable on any machine).
    static bool IsBlocked(IPAddress a) => a.Equals(IPAddress.Parse("127.0.0.2")) || a.ToString().StartsWith("169.254.");

    static async Task<McpClient> ConnectAsync(HttpClient http, Uri endpoint, Dictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = headers,
            ConnectionTimeout = TimeSpan.FromSeconds(10),
        }, http, ownsHttpClient: false);
        return await McpClient.CreateAsync(transport, new McpClientOptions { ClientInfo = new Implementation { Name = "quorum", Version = "0.0" } }, cancellationToken: ct);
    }

    static string Hash(AIFunctionDeclaration f)
    {
        // Definition hash of D-14: name + description + canonical schema.
        static string Canon(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canon(p.Value))) + "}",
            JsonValueKind.Array => "[" + string.Join(",", e.EnumerateArray().Select(Canon)) + "]",
            _ => e.GetRawText(),
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{f.Name}\n{f.Description}\n{Canon(f.JsonSchema)}")))[..16];
    }

    public static async Task RunAsync()
    {
        var (app, url) = await StartServerAsync();
        var endpoint = new Uri(url, "/mcp");
        Console.WriteLine("MCP server at " + endpoint);

        // 1. Basic use through OUR HttpClient (guarded handler), headers and declared capabilities.
        using var http = new HttpClient(GuardedHandler());
        await using (var client = await ConnectAsync(http, endpoint, new() { ["Authorization"] = "Bearer secret-token" }))
        {
            Console.WriteLine($"1. connected; server={client.ServerInfo?.Name} protocol={client.NegotiatedProtocolVersion}");
            var tools = await client.ListToolsAsync();
            foreach (var t in tools)
                Console.WriteLine($"   tool {t.Name} (type {t.GetType().Name}): hash={Hash(t)} readOnlyHint={t.ProtocolTool.Annotations?.ReadOnlyHint} schema={t.JsonSchema.GetRawText()}");

            var who = tools.First(t => t.Name == "who");
            Console.WriteLine("   who -> " + (await who.InvokeAsync()));
            var echo = tools.First(t => t.Name == "echo");
            var raw = await echo.InvokeAsync(new AIFunctionArguments { ["text"] = "hello" });
            Console.WriteLine($"   echo result runtime type={raw?.GetType().Name} value={raw}");
            var fail = tools.First(t => t.Name == "fail");
            try { var r = await fail.InvokeAsync(); Console.WriteLine($"   fail -> returned {r}"); }
            catch (Exception e) { Console.WriteLine($"   fail -> threw {e.GetType().Name}: {e.Message}"); }
            var typed = (McpClientTool)fail; var call = await typed.CallAsync(new Dictionary<string, object?>());
            Console.WriteLine($"   fail via CallAsync -> IsError={call.IsError} blocks={call.Content.Count} first={(call.Content[0] as TextContentBlock)?.Text}");
            var ok = await ((McpClientTool)echo).CallAsync(new Dictionary<string, object?> { ["text"] = "hey" });
            Console.WriteLine($"   echo via CallAsync -> IsError={ok.IsError} text={(ok.Content[0] as TextContentBlock)?.Text}");
        }

        // 2. Redirect to another host must not be followed.
        try
        {
            await using var c = await ConnectAsync(http, new Uri(url, "/redirect"));
            Console.WriteLine("2. redirect: connected?!");
        }
        catch (Exception e) { Console.WriteLine($"2. redirect not followed: {e.GetType().Name}: {e.Message.Split('\n')[0]}"); }

        using (var following = new HttpClient())
        {
            try { await using var c = await ConnectAsync(following, new Uri(url, "/redirect")); Console.WriteLine("2b. control: default HttpClient FOLLOWS the redirect and connects (so the test is meaningful)"); }
            catch (Exception e) { Console.WriteLine($"2b. control failed: {Root(e)}"); }
        }

        // 3. Blocked address, directly and through DNS (rebinding): the ConnectCallback decides at connection time.
        try
        {
            await using var c = await ConnectAsync(http, new Uri($"http://127.0.0.2:{url.Port}/mcp"));
            Console.WriteLine("3a. blocked IP: connected?!");
        }
        catch (Exception e) { Console.WriteLine($"3a. blocked IP refused: {Root(e)}"); }

        var rebinding = new HttpClient(GuardedHandler(host => host == "rebind.test" ? [IPAddress.Parse("127.0.0.2")] : host == "ok.test" ? [IPAddress.Loopback] : Dns.GetHostAddresses(host)));
        try
        {
            await using var c = await ConnectAsync(rebinding, new Uri($"http://rebind.test:{url.Port}/mcp"));
            Console.WriteLine("3b. hostname resolving to blocked: connected?!");
        }
        catch (Exception e) { Console.WriteLine($"3b. hostname resolving to a blocked IP refused: {Root(e)}"); }
        await using (var c = await ConnectAsync(rebinding, new Uri($"http://ok.test:{url.Port}/mcp")))
            Console.WriteLine($"3c. hostname resolved by OUR callback (not by DNS) connects: {(await c.ListToolsAsync()).Count} tools");

        // 4. Unreachable server and connection timeout.
        try { await using var c = await ConnectAsync(http, new Uri("http://127.0.0.1:1/mcp")); }
        catch (Exception e) { Console.WriteLine($"4. unreachable: {e.GetType().Name}: {Root(e)}"); }

        // 5. Inside the FICC pipeline of Experiment2: prefix, policy at call time, audit, timeout (and cancel reaches the server), size cap.
        await using (var client = await ConnectAsync(http, endpoint))
        {
            var mcpTools = await client.ListToolsAsync();
            var wrapped = mcpTools.Select(t => (AIFunction)new PrefixedFunction(t, "demo__" + t.Name)).ToArray();
            await using var server = await FakeServer.StartAsync();
            server.Enqueue(CallOf("demo__slow", "{\"ms\":5000}", "a"));
            server.Enqueue(CallOf("demo__big", "{\"n\":500}", "b"));
            server.Enqueue(CallOf("demo__echo", "{\"text\":\"hi\"}", "c"));
            server.Enqueue(CallOf("demo__fail", "{}", "d"));
            server.Enqueue([FakeServer.Delta("content", "finished")]);
            var store = new FakeStore();
            foreach (var t in wrapped) store.Tools[t.Name] = Policy.Auto;
            var channel = System.Threading.Channels.Channel.CreateUnbounded<AnswerEvent>();
            var ctx = new AnswerContext(new ToolLimits(8, TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(600), 100, TimeSpan.FromMinutes(5)), store, new PendingInteractions(), channel, TimeProvider.System);
            await foreach (var e in AnswerRunner.RunAsync(new QuorumChatClient(new HttpClient(), server.BaseUrl, "fake"), ctx, channel,
                               [new ChatMessage(ChatRole.User, "go")], new ChatOptions { Tools = [.. wrapped] }))
                Console.WriteLine("5. sse> " + e switch { ToolCallEvent c => $"tool_call {c.Tool}", ToolResultEvent r => $"tool_result {r.Status} '{r.Content[..Math.Min(80, r.Content.Length)]}'", TextEvent t => "text " + t.Text, var x => x.ToString() });
            await Task.Delay(300);
            Console.WriteLine($"   audit: {string.Join(" | ", store.Log)}");
            Console.WriteLine($"   server saw cancellation of the slow call: {DemoTools.Cancelled > 0}");
        }

        await app.StopAsync();
    }

    static string[] CallOf(string name, string args, string id) =>
        [FakeServer.ToolFragment(new { index = 0, id, type = "function", function = new { name, arguments = args } })];

    static string Root(Exception e) { while (e.InnerException is not null) e = e.InnerException; return e.GetType().Name + ": " + e.Message.Split('\n')[0]; }
}
