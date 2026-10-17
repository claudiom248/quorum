using System.Net;
using System.Net.Http.Json;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.Tools;

public sealed class McpUrlGuardTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory] // AC-3
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://169.254.0.7:8080/mcp")]
    [InlineData("http://[fe80::1]/mcp")]
    [InlineData("http://100.100.100.200/mcp")]
    [InlineData("http://[fd00:ec2::254]/mcp")]
    [InlineData("http://[::ffff:169.254.169.254]/mcp")]
    [InlineData("http://0.0.0.0/mcp")]
    public async Task ABlockedAddressIsRefusedWhenRegisteringAndTesting(string url)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();

        using var register = await ToolsApi.RegisterServerAsync(client, "meta", url);
        using var test = await client.PostAsJsonAsync(ToolsApi.ServerTest, new { url }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, register.StatusCode);
        Assert.Equal("tool.url_blocked", await ToolsApi.ProblemCodeAsync(register));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, test.StatusCode);
        Assert.Equal("tool.url_blocked", await ToolsApi.ProblemCodeAsync(test));
        Assert.Empty(await ToolsApi.ListServersAsync(client));
    }

    [Fact] // AC-3
    public async Task AHostNameThatResolvesToABlockedAddressIsRefused()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.McpHosts.Script("metadata.test", ["169.254.169.254"]);

        using var register = await ToolsApi.RegisterServerAsync(client, "meta", "http://metadata.test/mcp");

        Assert.Equal(HttpStatusCode.BadRequest, register.StatusCode);
        Assert.Equal("tool.url_blocked", await ToolsApi.ProblemCodeAsync(register));
    }

    [Fact] // AC-3: the name passes the up-front checks and resolves to a blocked address only when the connection is made
    public async Task ANameThatChangesItsAddressAfterTheChecksIsRefusedAtConnectionTime()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await using var mcp = await McpTestServer.StartAsync(McpTestServer.NewTool("search"));
        var port = new Uri(mcp.Url).Port;
        // The registration checks the name twice (the request, then the client); the connection resolves it a third time.
        app.McpHosts.Script("rebind.test", ["127.0.0.1"], ["127.0.0.1"], ["169.254.169.254"]);

        using var register = await ToolsApi.RegisterServerAsync(client, "rebind", $"http://rebind.test:{port}/mcp");

        Assert.Equal(HttpStatusCode.BadRequest, register.StatusCode);
        Assert.Equal("tool.url_blocked", await ToolsApi.ProblemCodeAsync(register));
        Assert.Empty(await ToolsApi.ListServersAsync(client));
    }

    [Fact] // D-18: the internal network stays reachable
    public async Task AnInternalHostNameIsAccepted()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await using var mcp = await McpTestServer.StartAsync(McpTestServer.NewTool("search"));
        app.McpHosts.Script("tools.internal.test", ["127.0.0.1"]);

        var created = await ToolsApi.RegisterServerOkAsync(client, "internal", $"http://tools.internal.test:{new Uri(mcp.Url).Port}/mcp");

        Assert.Equal("Available", created.Status);
        Assert.Single(created.Tools);
    }

    [Fact] // AC-4
    public async Task ARedirectIsNotFollowed()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await using var mcp = await McpTestServer.StartAsync(McpTestServer.NewTool("search"));

        var created = await ToolsApi.RegisterServerOkAsync(client, "redirecting", mcp.RedirectUrl);
        var server = await ToolsApi.GetServerAsync(client, created.Id);

        Assert.Equal("Unavailable", created.Status);
        Assert.Empty(created.Tools);
        Assert.Contains("30", server.LastError, StringComparison.Ordinal);
        Assert.Equal(0, mcp.RedirectTargetHits);
    }
}
