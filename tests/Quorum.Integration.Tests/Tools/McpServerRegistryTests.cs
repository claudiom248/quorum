using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.Tools;

public sealed class McpServerRegistryTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-1
    public async Task ARegisteredReachableServerIsAvailableAndItsToolsArePendingReview()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await using var mcp = await McpTestServer.StartAsync(McpTestServer.NewTool("search"), McpTestServer.NewTool("read"));

        var created = await ToolsApi.RegisterServerOkAsync(client, "files", mcp.Url);
        var server = await ToolsApi.GetServerAsync(client, created.Id);
        var listed = (await ToolsApi.ListToolsAsync(client)).Where(t => t.Origin == "Mcp").ToList();

        Assert.Equal("Available", created.Status);
        Assert.Null(created.Warning);
        Assert.Equal(["read", "search"], created.Tools.Select(t => t.Name).Order().ToArray());
        Assert.All(created.Tools, tool => Assert.Equal(("PendingReview", "Confirm"), (tool.State, tool.Policy)));
        Assert.Equal(("Available", 2, 2), (server.Status, server.ToolCount, server.PendingReviewCount));
        Assert.Equal(["read", "search"], listed.Select(t => t.Name).Order().ToArray());
        Assert.All(listed, tool => Assert.Equal("files", tool.ServerName));
        var inList = Assert.Single(await ToolsApi.ListServersAsync(client));
        Assert.Equal((created.Id, 2, 2, false), (inList.Id, inList.ToolCount, inList.PendingReviewCount, inList.Shared));
        Assert.Null(inList.Tools);
    }

    [Fact] // AC-2
    public async Task AnUnreachableServerIsSavedAsUnavailableWithAWarning()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();

        var created = await ToolsApi.RegisterServerOkAsync(client, "gone", $"http://127.0.0.1:{UnusedPort()}/mcp");
        var server = await ToolsApi.GetServerAsync(client, created.Id);

        Assert.Equal("Unavailable", created.Status);
        Assert.False(string.IsNullOrWhiteSpace(created.Warning));
        Assert.Empty(created.Tools);
        Assert.Equal("Unavailable", server.Status);
        Assert.False(string.IsNullOrWhiteSpace(server.LastError));
        Assert.Equal(0, server.ToolCount);
    }

    [Theory] // The server answers, but not as an MCP server: also unavailable, not an error of the registration.
    [InlineData("error", "500")]
    [InlineData("not-mcp", null)]
    public async Task AServerThatAnswersBadlySavesAsUnavailable(string endpoint, string? expectedInError)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await using var mcp = await McpTestServer.StartAsync();
        var url = endpoint == "error" ? mcp.ErrorUrl : mcp.NotMcpUrl;

        var created = await ToolsApi.RegisterServerOkAsync(client, "broken", url);
        var server = await ToolsApi.GetServerAsync(client, created.Id);

        Assert.Equal("Unavailable", created.Status);
        Assert.False(string.IsNullOrWhiteSpace(server.LastError));
        if (expectedInError is not null)
        {
            Assert.Contains(expectedInError, server.LastError, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ATestReturnsTheToolsWithoutSavingAnything()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await using var mcp = await McpTestServer.StartAsync(McpTestServer.NewTool("search", readOnly: true));

        using var response = await client.PostAsJsonAsync(ToolsApi.ServerTest, new { url = mcp.Url }, Ct);
        var body = await response.Content.ReadFromJsonAsync<TestBody>(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body!.Reachable);
        Assert.Equal(["search"], body.Tools.Select(t => t.Name).ToArray());
        Assert.Empty(await ToolsApi.ListServersAsync(client));
    }

    [Fact]
    public async Task ATestOfAnUnreachableServerReturns422()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();

        using var response = await client.PostAsJsonAsync(ToolsApi.ServerTest, new { url = $"http://127.0.0.1:{UnusedPort()}/mcp" }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("tool.server_unreachable", await ToolsApi.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task AServerThatNeverAnswersIsUnreachableAfterTheConnectionTimeout()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await using var mcp = await McpTestServer.StartAsync();

        var started = DateTimeOffset.UtcNow;
        using var response = await client.PostAsJsonAsync(ToolsApi.ServerTest, new { url = mcp.SilentUrl }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("tool.server_unreachable", await ToolsApi.ProblemCodeAsync(response));
        Assert.InRange(DateTimeOffset.UtcNow - started, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(30));
    }

    [Theory] // AC-5
    [InlineData("")]
    [InlineData("Upper")]
    [InlineData("has space")]
    [InlineData("dotted.name")]
    [InlineData("double__underscore")]
    [InlineData("native")]
    [InlineData("-leading")]
    [InlineData("this-name-is-longer-than-thirty-two-characters")]
    public async Task AnInvalidServerNameIsRejected(string name)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await using var mcp = await McpTestServer.StartAsync();

        using var response = await ToolsApi.RegisterServerAsync(client, name, mcp.Url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("tool.invalid_name", await ToolsApi.ProblemCodeAsync(response));
        Assert.Empty(await ToolsApi.ListServersAsync(client));
    }

    [Fact] // AC-5
    public async Task ADuplicateServerNameIsRejectedWith409()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await using var mcp = await McpTestServer.StartAsync();
        await ToolsApi.RegisterServerOkAsync(client, "files", mcp.Url);

        using var response = await ToolsApi.RegisterServerAsync(client, "files", mcp.Url);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("tool.server_name_taken", await ToolsApi.ProblemCodeAsync(response));
        Assert.Single(await ToolsApi.ListServersAsync(client));
    }

    [Theory]
    [InlineData("ftp://tools.internal/mcp")]
    [InlineData("/relative")]
    [InlineData("https://user:secret@mcp.example.com/mcp")]
    [InlineData("")]
    public async Task AnUnusableUrlIsRejected(string url)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();

        using var response = await ToolsApi.RegisterServerAsync(client, "files", url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("tool.invalid_url", await ToolsApi.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task HeadersThatTheConnectionOwnsAreRejected()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await using var mcp = await McpTestServer.StartAsync();

        using var response = await ToolsApi.RegisterServerAsync(client, "files", mcp.Url, new Dictionary<string, string> { ["Host"] = "evil.example" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("tool.invalid_headers", await ToolsApi.ProblemCodeAsync(response));
    }

    [Fact] // AC-6
    public async Task HeadersAreSentToTheServerStoredEncryptedAndNeverReturnedOrLogged()
    {
        const string secret = "Bearer s3cret-token-value";
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await using var mcp = await McpTestServer.StartAsync(McpTestServer.NewTool("search"));

        var created = await ToolsApi.RegisterServerOkAsync(client, "files", mcp.Url, new Dictionary<string, string> { ["Authorization"] = secret });
        using var detailResponse = await client.GetAsync(ToolsApi.Server(created.Id), Ct);
        var detail = await detailResponse.Content.ReadAsStringAsync(Ct);
        using var listResponse = await client.GetAsync(ToolsApi.Servers, Ct);
        var list = await listResponse.Content.ReadAsStringAsync(Ct);
        var stored = await ToolFixtures.ScalarAsync<string>(app, "SELECT \"ProtectedHeaders\" FROM agents.mcp_servers");

        Assert.Contains($"Authorization: {secret}", mcp.ReceivedHeaders);
        Assert.True((await ToolsApi.GetServerAsync(client, created.Id)).HasHeaders);
        Assert.DoesNotContain("s3cret", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", list, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", stored, StringComparison.Ordinal);
        Assert.DoesNotContain(app.Logs.Messages, message => message.Contains("s3cret", StringComparison.Ordinal));
    }

    [Fact] // AC-6
    public async Task AnUpdateWithoutHeadersKeepsTheStoredOnesAndAnEmptyObjectRemovesThem()
    {
        const string secret = "Bearer keep-me";
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await using var mcp = await McpTestServer.StartAsync(McpTestServer.NewTool("search"));
        var created = await ToolsApi.RegisterServerOkAsync(client, "files", mcp.Url, new Dictionary<string, string> { ["Authorization"] = secret });

        using var rename = await client.PutAsJsonAsync(ToolsApi.Server(created.Id), new { name = "docs", url = mcp.Url, scope = ToolsApi.TenantScope }, Ct);
        mcp.ReceivedHeaders.Clear();
        await ToolsApi.RefreshServerOkAsync(client, created.Id);
        var kept = await ToolsApi.GetServerAsync(client, created.Id);
        using var clear = await client.PutAsJsonAsync(ToolsApi.Server(created.Id), new { name = "docs", url = mcp.Url, headers = new { }, scope = ToolsApi.TenantScope }, Ct);
        var cleared = await ToolsApi.GetServerAsync(client, created.Id);

        Assert.Equal(HttpStatusCode.NoContent, rename.StatusCode);
        Assert.Contains($"Authorization: {secret}", mcp.ReceivedHeaders);
        Assert.Equal(("docs", true), (kept.Name, kept.HasHeaders));
        Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);
        Assert.False(cleared.HasHeaders);
    }

    [Fact] // AC-9
    public async Task AnnotationsAreShownAsInformationAndDoNotChangeThePolicy()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await using var mcp = await McpTestServer.StartAsync(
            McpTestServer.NewTool("read", readOnly: true, destructive: false),
            McpTestServer.NewTool("wipe", destructive: true),
            McpTestServer.NewTool("plain"));

        var created = await ToolsApi.RegisterServerOkAsync(client, "files", mcp.Url);

        var read = created.Tools.Single(t => t.Name == "read");
        var wipe = created.Tools.Single(t => t.Name == "wipe");
        var plain = created.Tools.Single(t => t.Name == "plain");
        Assert.True(read.Annotations!.Value.GetProperty("readOnlyHint").GetBoolean());
        Assert.False(read.Annotations!.Value.GetProperty("destructiveHint").GetBoolean());
        Assert.True(wipe.Annotations!.Value.GetProperty("destructiveHint").GetBoolean());
        Assert.Null(plain.Annotations);
        Assert.All(created.Tools, tool => Assert.Equal(("PendingReview", "Confirm"), (tool.State, tool.Policy)));
    }

    [Fact]
    public async Task DeletingAServerRemovesItsToolsAndTheirActivations()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await using var mcp = await McpTestServer.StartAsync(McpTestServer.NewTool("search"));
        var created = await ToolsApi.RegisterServerOkAsync(client, "files", mcp.Url);
        var tool = created.Tools.Single();
        using var enable = await client.PutAsJsonAsync(ToolsApi.Tool(tool.Id), new { state = "Enabled", policy = "Auto" }, Ct);
        var assistantId = await ToolsApi.DefaultAssistantIdAsync(client);
        using var activate = await client.PutAsJsonAsync(ToolsApi.AssistantTools(assistantId), new { toolIds = new[] { tool.Id } }, Ct);
        var activationsBefore = await ToolFixtures.ScalarAsync<long>(app, "SELECT count(*) FROM agents.assistant_tools WHERE \"ToolId\" = '" + tool.Id + "'");

        using var delete = await client.DeleteAsync(ToolsApi.Server(created.Id), Ct);
        using var after = await client.GetAsync(ToolsApi.Server(created.Id), Ct);

        Assert.Equal(1L, activationsBefore);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
        Assert.DoesNotContain(await ToolsApi.ListToolsAsync(client), t => t.Origin == "Mcp");
        Assert.Equal(0L, await ToolFixtures.ScalarAsync<long>(app, "SELECT count(*) FROM agents.assistant_tools WHERE \"ToolId\" = '" + tool.Id + "'"));
        Assert.Equal(0L, await ToolFixtures.ScalarAsync<long>(app, "SELECT count(*) FROM agents.tools WHERE \"Origin\" = 'Mcp'"));
    }

    [Fact]
    public async Task TwoServersMayOfferAToolWithTheSameName()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await using var first = await McpTestServer.StartAsync(McpTestServer.NewTool("search"));
        await using var second = await McpTestServer.StartAsync(McpTestServer.NewTool("search"));

        await ToolsApi.RegisterServerOkAsync(client, "first", first.Url);
        await ToolsApi.RegisterServerOkAsync(client, "second", second.Url);

        var tools = (await ToolsApi.ListToolsAsync(client)).Where(t => t.Origin == "Mcp").ToList();
        Assert.Equal([("first", "search"), ("second", "search")], tools.Select(t => (t.ServerName!, t.Name)).Order().ToArray());
    }

    [Fact] // AC-10
    public async Task EveryServerRouteNeedsTheManagePermission()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        using var member = await app.CreateMemberClientAsync();
        using var anonymous = app.CreateUiClient();
        var id = Guid.NewGuid();
        var body = new { name = "files", url = "http://127.0.0.1:1/mcp", scope = ToolsApi.TenantScope };

        var requests = new List<Func<HttpClient, Task<HttpResponseMessage>>>
        {
            client => client.PostAsJsonAsync(ToolsApi.ServerTest, new { url = "http://127.0.0.1:1/mcp" }, Ct),
            client => client.GetAsync(ToolsApi.Servers, Ct),
            client => client.PostAsJsonAsync(ToolsApi.Servers, body, Ct),
            client => client.GetAsync(ToolsApi.Server(id), Ct),
            client => client.PutAsJsonAsync(ToolsApi.Server(id), body, Ct),
            client => client.DeleteAsync(ToolsApi.Server(id), Ct),
            client => client.PostAsync(ToolsApi.ServerRefresh(id), content: null, Ct),
        };

        foreach (var send in requests)
        {
            using var asMember = await send(member);
            using var asAnonymous = await send(anonymous);

            Assert.Equal(HttpStatusCode.Forbidden, asMember.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, asAnonymous.StatusCode);
        }
    }

    [Fact]
    public async Task AnUnknownServerReturns404()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var id = Guid.NewGuid();

        using var get = await client.GetAsync(ToolsApi.Server(id), Ct);
        using var put = await client.PutAsJsonAsync(ToolsApi.Server(id), new { name = "files", url = "http://127.0.0.1:1/mcp", scope = ToolsApi.TenantScope }, Ct);
        using var delete = await client.DeleteAsync(ToolsApi.Server(id), Ct);
        using var refresh = await client.PostAsync(ToolsApi.ServerRefresh(id), content: null, Ct);

        Assert.All([get, put, delete, refresh], response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
    }

    /// <summary>A loopback port nothing listens on.</summary>
    internal static int UnusedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed record TestBody(bool Reachable, List<TestedTool> Tools);

    private sealed record TestedTool(string Name, string Description);
}
