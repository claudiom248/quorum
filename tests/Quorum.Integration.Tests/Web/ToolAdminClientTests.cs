using Quorum.Integration.Tests.Fixtures;
using Quorum.Web.Client.Api;

namespace Quorum.Integration.Tests.Web;

/// <summary>The typed client of the tools admin page against the real server and a real MCP server (spec 004, AC-48).</summary>
public sealed class ToolAdminClientTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnAdminRegistersReviewsRefreshesAndDeletesAServerThroughTheClient()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var http = await app.SetUpAsync();
        var api = new QuorumApi(http);
        await using var mcp = await McpTestServer.StartAsync(McpTestServer.NewTool("read", readOnly: true), McpTestServer.NewTool("search"));
        var tenantScope = new ToolScope(ScopeKinds.Tenant, [], []);

        var tested = await api.TestToolServerAsync(mcp.Url, headers: null, Ct);
        var created = await api.CreateToolServerAsync(new ToolServerRequest("files", mcp.Url, new Dictionary<string, string> { ["X-Test"] = "1" }, tenantScope), Ct);
        var server = await api.GetToolServerAsync(created.Value.Id, Ct);
        var listed = await api.ListToolServersAsync(Ct);

        Assert.Equal(["read", "search"], tested.Value.Tools.Select(t => t.Name).Order().ToArray());
        Assert.Equal(["read-only"], Quorum.Web.Client.ToolAdminView.DescribeAnnotations(tested.Value.Tools.Single(t => t.Name == "read").Annotations));
        Assert.Equal("Available", created.Value.Status);
        Assert.Equal(("Available", true, 2, 2, false), (server.Value.Status, server.Value.HasHeaders, server.Value.ToolCount, server.Value.PendingReviewCount, server.Value.Shared));
        Assert.Equal(ScopeKinds.Tenant, server.Value.Scope.Kind);
        Assert.All(server.Value.Tools!, tool => Assert.Equal((ToolStates.PendingReview, ToolPolicies.Confirm), (tool.State, tool.Policy)));
        Assert.Null(Assert.Single(listed.Value).Tools);

        var approved = server.Value.Tools!.Single(t => t.Name == "read");
        Assert.True((await api.UpdateToolAsync(approved.Id, ToolStates.Enabled, ToolPolicies.Auto, Ct)).IsSuccess);
        var afterApproval = (await api.GetToolServerAsync(server.Value.Id, Ct)).Value;
        Assert.Equal(1, afterApproval.PendingReviewCount);
        var read = afterApproval.Tools!.Single(t => t.Name == "read");
        Assert.Equal((ToolStates.Enabled, ToolPolicies.Auto), (read.State, read.Policy));

        mcp.Tools.RemoveAll(t => t.Name == "search");
        mcp.Tools.Add(McpTestServer.NewTool("list"));
        var refreshed = await api.RefreshToolServerAsync(server.Value.Id, Ct);
        Assert.Equal(["list"], refreshed.Value.Added);
        Assert.Equal(["search"], refreshed.Value.Removed);

        var builtin = (await api.ListToolsAsync(Ct)).Value.Where(t => t.Origin == "Builtin").Select(t => t.Name).Order().ToArray();
        Assert.Contains("calculator", builtin);

        var renamed = await api.UpdateToolServerAsync(server.Value.Id, new ToolServerRequest("files2", mcp.Url, Headers: null, tenantScope), Ct);
        Assert.True(renamed.IsSuccess);
        Assert.Equal("files2", (await api.GetToolServerAsync(server.Value.Id, Ct)).Value.Name);

        Assert.True((await api.DeleteToolServerAsync(server.Value.Id, Ct)).IsSuccess);
        Assert.Empty((await api.ListToolServersAsync(Ct)).Value);
    }

    [Fact]
    public async Task ARejectedRegistrationCarriesTheServersCode()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var http = await app.SetUpAsync();
        var api = new QuorumApi(http);

        var result = await api.CreateToolServerAsync(new ToolServerRequest("Bad Name", "http://x.test/mcp", Headers: null, new ToolScope(ScopeKinds.Tenant, [], [])), Ct);

        Assert.False(result.IsSuccess);
        Assert.Equal("tool.invalid_name", result.Problem!.Code);
    }
}
