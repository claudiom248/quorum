using System.Net;
using System.Net.Http.Json;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.Tools;

public sealed class McpToolReviewTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-7
    public async Task ARefreshSendsChangedAndNewToolsToReviewAndRemovesTheOnesThatVanished()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        const string schema = """{"type":"object","properties":{"a":{"type":"string"},"b":{"type":"number"}}}""";
        await using var mcp = await McpTestServer.StartAsync(
            McpTestServer.NewTool("changes", "Before."),
            McpTestServer.NewTool("stays", "Same.", schema),
            McpTestServer.NewTool("vanishes"));
        var created = await ToolsApi.RegisterServerOkAsync(client, "files", mcp.Url);
        var tools = created.Tools.ToDictionary(t => t.Name);
        foreach (var name in new[] { "changes", "stays", "vanishes" })
        {
            using var approve = await client.PutAsJsonAsync(ToolsApi.Tool(tools[name].Id), new { state = "Enabled", policy = "Auto" }, Ct);
            approve.EnsureSuccessStatusCode();
        }

        var assistantId = await ToolsApi.DefaultAssistantIdAsync(client);
        var calculator = (await ToolsApi.GetAssistantToolsAsync(client, assistantId)).Pool.Single(t => t.Name == "native__calculator");
        using var activate = await client.PutAsJsonAsync(
            ToolsApi.AssistantTools(assistantId),
            new { toolIds = new[] { calculator.ToolId, tools["changes"].Id, tools["stays"].Id, tools["vanishes"].Id } },
            Ct);

        mcp.Tools.Clear();
        mcp.Tools.AddRange([
            McpTestServer.NewTool("changes", "After."),
            // Same definition with its keys in another order: not a change.
            McpTestServer.NewTool("stays", "Same.", """{"properties":{"b":{"type":"number"},"a":{"type":"string"}},"type":"object"}"""),
            McpTestServer.NewTool("brand-new")]);
        var refresh = await ToolsApi.RefreshServerOkAsync(client, created.Id);

        var after = (await ToolsApi.GetServerAsync(client, created.Id)).Tools!.ToDictionary(t => t.Name);
        var pool = (await ToolsApi.GetAssistantToolsAsync(client, assistantId)).Pool;
        Assert.Equal("Available", refresh.Status);
        Assert.Equal(["brand-new"], refresh.Added);
        Assert.Equal(["changes"], refresh.Changed);
        Assert.Equal(["vanishes"], refresh.Removed);
        Assert.Equal(["brand-new", "changes", "stays"], after.Keys.Order().ToArray());
        Assert.Equal(("PendingReview", "Confirm"), (after["changes"].State, after["changes"].Policy));
        Assert.Equal("After.", after["changes"].Description);
        Assert.Equal(("PendingReview", "Confirm"), (after["brand-new"].State, after["brand-new"].Policy));
        Assert.Equal(("Enabled", "Auto"), (after["stays"].State, after["stays"].Policy));
        Assert.Equal(["files__stays", "native__calculator"], pool.Where(t => t.Active).Select(t => t.Name).Order().ToArray());
        Assert.DoesNotContain(pool, t => t.Name is "files__changes" or "files__brand-new" or "files__vanishes");
        Assert.Equal(0L, await ToolFixtures.ScalarAsync<long>(app, "SELECT count(*) FROM agents.assistant_tools WHERE \"ToolId\" = '" + tools["vanishes"].Id + "'"));
    }

    [Fact] // AC-7, AC-8: a reviewed tool that changed is offered again only after the admin enables it again
    public async Task AChangedToolKeepsItsActivationAndReturnsToThePoolOnceReviewed()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await using var mcp = await McpTestServer.StartAsync(McpTestServer.NewTool("search", "Before."));
        var created = await ToolsApi.RegisterServerOkAsync(client, "files", mcp.Url);
        var tool = created.Tools.Single();
        using var approve = await client.PutAsJsonAsync(ToolsApi.Tool(tool.Id), new { state = "Enabled", policy = "Auto" }, Ct);
        var assistantId = await ToolsApi.DefaultAssistantIdAsync(client);
        using var activate = await client.PutAsJsonAsync(ToolsApi.AssistantTools(assistantId), new { toolIds = new[] { tool.Id } }, Ct);

        mcp.Tools[0] = McpTestServer.NewTool("search", "After.");
        await ToolsApi.RefreshServerOkAsync(client, created.Id);
        var whilePending = (await ToolsApi.GetAssistantToolsAsync(client, assistantId)).Pool;
        using var review = await client.PutAsJsonAsync(ToolsApi.Tool(tool.Id), new { state = "Enabled", policy = "Confirm" }, Ct);
        var afterReview = (await ToolsApi.GetAssistantToolsAsync(client, assistantId)).Pool;

        Assert.DoesNotContain(whilePending, t => t.Name == "files__search");
        var back = Assert.Single(afterReview, t => t.Name == "files__search");
        Assert.True(back.Active);
    }

    [Fact] // AC-8
    public async Task APendingToolCanBeActivatedOnlyAfterTheAdminEnablesIt()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await using var mcp = await McpTestServer.StartAsync(McpTestServer.NewTool("search"));
        var created = await ToolsApi.RegisterServerOkAsync(client, "files", mcp.Url);
        var tool = created.Tools.Single();
        var assistantId = await ToolsApi.DefaultAssistantIdAsync(client);

        using var beforeReview = await client.PutAsJsonAsync(ToolsApi.AssistantTools(assistantId), new { toolIds = new[] { tool.Id } }, Ct);
        using var enable = await client.PutAsJsonAsync(ToolsApi.Tool(tool.Id), new { state = "Enabled", policy = "Confirm" }, Ct);
        using var afterReview = await client.PutAsJsonAsync(ToolsApi.AssistantTools(assistantId), new { toolIds = new[] { tool.Id } }, Ct);
        var pool = (await ToolsApi.GetAssistantToolsAsync(client, assistantId)).Pool;

        Assert.Equal(HttpStatusCode.BadRequest, beforeReview.StatusCode);
        Assert.Equal("tool.not_in_pool", await ToolsApi.ProblemCodeAsync(beforeReview));
        Assert.Equal(HttpStatusCode.NoContent, enable.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, afterReview.StatusCode);
        Assert.True(Assert.Single(pool, t => t.Name == "files__search").Active);
    }

    [Fact] // D-25
    public async Task ARefreshOfAServerThatIsDownReturns422AndTakesItsToolsOutOfThePool()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var mcp = await McpTestServer.StartAsync(McpTestServer.NewTool("search"));
        var created = await ToolsApi.RegisterServerOkAsync(client, "files", mcp.Url);
        using var approve = await client.PutAsJsonAsync(ToolsApi.Tool(created.Tools.Single().Id), new { state = "Enabled", policy = "Auto" }, Ct);
        var assistantId = await ToolsApi.DefaultAssistantIdAsync(client);
        var before = (await ToolsApi.GetAssistantToolsAsync(client, assistantId)).Pool;
        await mcp.DisposeAsync();

        using var refresh = await client.PostAsync(ToolsApi.ServerRefresh(created.Id), content: null, Ct);
        var server = await ToolsApi.GetServerAsync(client, created.Id);
        var after = (await ToolsApi.GetAssistantToolsAsync(client, assistantId)).Pool;

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refresh.StatusCode);
        Assert.Equal("tool.server_unreachable", await ToolsApi.ProblemCodeAsync(refresh));
        Assert.Equal("Unavailable", server.Status);
        Assert.False(string.IsNullOrWhiteSpace(server.LastError));
        Assert.Contains(before, t => t.Name == "files__search");
        Assert.DoesNotContain(after, t => t.Name == "files__search");
        Assert.Equal(1, server.ToolCount);
    }
}
