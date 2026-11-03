using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.Tools;

public sealed class McpRedirectTests(SharedQuorumApp host) : IClassFixture<SharedQuorumApp>, IAsyncLifetime
{
    public ValueTask InitializeAsync() => new(host.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact] // AC-4
    public async Task ARedirectIsNotFollowed()
    {
        var client = host.Admin;
        await using var mcp = await McpTestServer.StartAsync(McpTestServer.NewTool("search"));

        var created = await ToolsApi.RegisterServerOkAsync(client, "redirecting", mcp.RedirectUrl);
        var server = await ToolsApi.GetServerAsync(client, created.Id);

        Assert.Equal("Unavailable", created.Status);
        Assert.Empty(created.Tools);
        Assert.Contains("30", server.LastError, StringComparison.Ordinal);
        Assert.Equal(0, mcp.RedirectTargetHits);
    }
}
