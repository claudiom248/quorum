using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.Tools;

public sealed class McpRedirectTests(PostgresFixture postgres)
{
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
