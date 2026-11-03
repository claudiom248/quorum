using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;

namespace Quorum.Integration.Tests.Tools;

public sealed class McpToolScopeTests(SharedQuorumApp host) : IClassFixture<SharedQuorumApp>, IAsyncLifetime
{
    public ValueTask InitializeAsync() => new(host.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-11
    public async Task AnAgentScopeOnlyReachesTheAgentsItLists()
    {
        var app = host.App;
        var client = host.Admin;
        await using var mcp = await McpTestServer.StartAsync(McpTestServer.NewTool("search"));
        var defaultAssistant = await ToolsApi.DefaultAssistantIdAsync(client);
        var (otherAssistant, otherAgent) = await AssistantFixtures.AddWithAgentAsync(app);
        var defaultAgent = await AssistantFixtures.RootAgentIdAsync(app, defaultAssistant);

        var forOther = await ToolsApi.RegisterServerOkAsync(client, "other", mcp.Url, scope: new { kind = "Agent", agentIds = new[] { otherAgent } });
        var forDefault = await ToolsApi.RegisterServerOkAsync(client, "mine", mcp.Url, scope: new { kind = "Agent", agentIds = new[] { defaultAgent } });
        await EnableAllAsync(client, forOther, forDefault);

        var defaultPool = (await ToolsApi.GetAssistantToolsAsync(client, defaultAssistant)).Pool.Select(t => t.Name).ToArray();
        var otherPool = (await ToolsApi.GetAssistantToolsAsync(client, otherAssistant)).Pool.Select(t => t.Name).ToArray();

        Assert.Contains("mine__search", defaultPool);
        Assert.DoesNotContain("other__search", defaultPool);
        Assert.Contains("other__search", otherPool);
        Assert.DoesNotContain("mine__search", otherPool);
    }

    [Theory] // AC-12
    [InlineData("Global")]
    [InlineData("Tenant")]
    public async Task AGlobalOrTenantScopePutsTheEnabledToolsInThePoolButNotActive(string kind)
    {
        var client = host.Admin;
        await using var mcp = await McpTestServer.StartAsync(McpTestServer.NewTool("search"), McpTestServer.NewTool("pending"));
        var created = await ToolsApi.RegisterServerOkAsync(client, "files", mcp.Url, scope: new { kind });
        using var enable = await client.PutAsJsonAsync(ToolsApi.Tool(created.Tools.Single(t => t.Name == "search").Id), new { state = "Enabled", policy = "Auto" }, Ct);
        var assistantId = await ToolsApi.DefaultAssistantIdAsync(client);

        var pool = (await ToolsApi.GetAssistantToolsAsync(client, assistantId)).Pool;

        var tool = Assert.Single(pool, t => t.Name == "files__search");
        Assert.False(tool.Active);
        Assert.DoesNotContain(pool, t => t.Name == "files__pending");
    }

    [Fact] // AC-13
    public async Task TheAgentScopeIsRejected()
    {
        var client = host.Admin;
        await using var mcp = await McpTestServer.StartAsync();

        using var response = await ToolsApi.RegisterServerAsync(client, "files", mcp.Url, scope: new { kind = "Agent" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("tool.invalid_scope", await ToolsApi.ProblemCodeAsync(response));
        Assert.Empty(await ToolsApi.ListServersAsync(client));
    }

    [Theory]
    [InlineData("Everyone", false, false)]
    [InlineData("5", false, false)]
    [InlineData("Global", true, false)]
    [InlineData("Tenant", false, true)]
    [InlineData("Agent", false, false)]
    [InlineData("Agent", true, false)]
    public async Task AnIncoherentScopeIsRejected(string kind, bool withTenants, bool withAgents)
    {
        var client = host.Admin;
        await using var mcp = await McpTestServer.StartAsync();
        var assistantId = await ToolsApi.DefaultAssistantIdAsync(client);

        using var response = await ToolsApi.RegisterServerAsync(
            client,
            "files",
            mcp.Url,
            scope: new { kind, tenantIds = withTenants ? new[] { Guid.NewGuid() } : null, agentIds = withAgents ? new[] { assistantId } : null });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("tool.invalid_scope", await ToolsApi.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task AnAgentThatDoesNotExistCannotBeInTheScope()
    {
        var client = host.Admin;
        await using var mcp = await McpTestServer.StartAsync();

        using var response = await ToolsApi.RegisterServerAsync(client, "files", mcp.Url, scope: new { kind = "Agent", agentIds = new[] { Guid.NewGuid() } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("tool.invalid_scope", await ToolsApi.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task TheScopeOfAServerCanBeChanged()
    {
        var app = host.App;
        var client = host.Admin;
        await using var mcp = await McpTestServer.StartAsync(McpTestServer.NewTool("search"));
        var defaultAssistant = await ToolsApi.DefaultAssistantIdAsync(client);
        var (otherAssistant, otherAgent) = await AssistantFixtures.AddWithAgentAsync(app);
        var defaultAgent = await AssistantFixtures.RootAgentIdAsync(app, defaultAssistant);
        var created = await ToolsApi.RegisterServerOkAsync(client, "files", mcp.Url);
        await EnableAllAsync(client, created);

        using var narrow = await client.PutAsJsonAsync(
            ToolsApi.Server(created.Id),
            new { name = "files", url = mcp.Url, scope = new { kind = "Agent", agentIds = new[] { otherAgent } } },
            Ct);
        var server = await ToolsApi.GetServerAsync(client, created.Id);
        var defaultPool = (await ToolsApi.GetAssistantToolsAsync(client, defaultAssistant)).Pool;

        Assert.Equal(HttpStatusCode.NoContent, narrow.StatusCode);
        Assert.Equal("Agent", server.Scope.Kind);
        Assert.Equal([otherAgent], server.Scope.AgentIds);
        Assert.DoesNotContain(defaultPool, t => t.Name == "files__search");
    }

    [Fact] // AC-18
    public async Task AnotherTenantsServerIsVisibleOnlyThroughItsScopeAndNeverWritable()
    {
        var app = host.App;
        var client = host.Admin;
        var tenantId = await ToolFixtures.ScalarAsync<Guid>(app, "SELECT \"TenantId\" FROM agents.assistants LIMIT 1");
        var assistantId = await ToolsApi.DefaultAssistantIdAsync(client);
        var stranger = Guid.CreateVersion7();
        var global = await AddForeignServerAsync(app, stranger, "shared-global", ToolScopeKind.Global);
        var listingUs = await AddForeignServerAsync(app, stranger, "shared-us", ToolScopeKind.Tenant, tenantIds: [tenantId]);
        await AddForeignServerAsync(app, stranger, "tenant-private", ToolScopeKind.Tenant, tenantIds: [Guid.CreateVersion7()]);
        await AddForeignServerAsync(app, stranger, "agent-private", ToolScopeKind.Agent, agentIds: [Guid.CreateVersion7()]);
        await AddForeignServerAsync(app, stranger, "agent-ours", ToolScopeKind.Agent, agentIds: [await AssistantFixtures.RootAgentIdAsync(app, assistantId)]);
        await AddForeignServerAsync(app, stranger, "down", ToolScopeKind.Global, status: McpServerStatus.Unavailable);

        var servers = await ToolsApi.ListServersAsync(client);
        var tools = (await ToolsApi.ListToolsAsync(client)).Where(t => t.Origin == "Mcp").Select(t => t.ServerName).ToArray();
        var pool = (await ToolsApi.GetAssistantToolsAsync(client, assistantId)).Pool.Where(t => t.Origin == "mcp").Select(t => t.Name).ToArray();
        var detail = await ToolsApi.GetServerAsync(client, global.ServerId);
        using var hidden = await client.GetAsync(ToolsApi.Server(Guid.Parse(await ServerIdAsync(app, "tenant-private"))), Ct);
        using var edit = await client.PutAsJsonAsync(ToolsApi.Server(global.ServerId), new { name = "mine-now", url = "http://[::1]:1/mcp", scope = ToolsApi.TenantScope }, Ct);
        using var delete = await client.DeleteAsync(ToolsApi.Server(global.ServerId), Ct);
        using var refresh = await client.PostAsync(ToolsApi.ServerRefresh(global.ServerId), content: null, Ct);
        using var changeTool = await client.PutAsJsonAsync(ToolsApi.Tool(global.ToolId), new { state = "Disabled", policy = "Auto" }, Ct);

        Assert.Equal(["down", "shared-global", "shared-us"], servers.Select(s => s.Name).Order().ToArray());
        Assert.All(servers, s => Assert.True(s.Shared));
        Assert.Equal(["down", "shared-global", "shared-us"], tools.Order().ToArray());
        Assert.Equal(["shared-global__tool", "shared-us__tool"], pool.Order().ToArray());
        Assert.Equal(1, detail.ToolCount);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.All([edit, delete, refresh, changeTool], response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
        Assert.NotEqual(Guid.Empty, listingUs.ServerId);
    }

    private static async Task EnableAllAsync(HttpClient client, params ToolsApi.ServerCreatedBody[] servers)
    {
        foreach (var tool in servers.SelectMany(s => s.Tools))
        {
            using var enable = await client.PutAsJsonAsync(ToolsApi.Tool(tool.Id), new { state = "Enabled", policy = "Auto" }, Ct);
            enable.EnsureSuccessStatusCode();
        }
    }

    /// <summary>A server of another tenant with one enabled tool called <c>tool</c>, written straight to the database.</summary>
    private static async Task<(Guid ServerId, Guid ToolId)> AddForeignServerAsync(
        QuorumApp app,
        Guid owner,
        string name,
        ToolScopeKind scope,
        IEnumerable<Guid>? tenantIds = null,
        IEnumerable<Guid>? agentIds = null,
        McpServerStatus status = McpServerStatus.Available)
    {
        await using var scopeFactory = app.Services.CreateAsyncScope();
        var db = scopeFactory.ServiceProvider.GetRequiredService<AgentsDbContext>();
        var now = DateTimeOffset.UtcNow;
        var server = McpServer.Create(owner, name, "http://[::1]:1/mcp", protectedHeaders: null, scope, tenantIds ?? [], agentIds ?? [], now);
        if (status == McpServerStatus.Available)
        {
            server.MarkAvailable(now);
        }
        else
        {
            server.MarkUnavailable("down", now);
        }

        var tool = Tool.CreateMcp(owner, server.Id, "tool", "A tool.", """{"type":"object"}""", "hash-" + name, annotations: null);
        tool.Review(ToolState.Enabled, ToolPolicy.Confirm);
        db.McpServers.Add(server);
        db.Tools.Add(tool);
        await db.SaveChangesAsync(Ct);
        return (server.Id, tool.Id);
    }

    private static async Task<string> ServerIdAsync(QuorumApp app, string name) =>
        (await ToolFixtures.ScalarAsync<Guid>(app, $"SELECT \"Id\" FROM agents.mcp_servers WHERE \"Name\" = '{name}'")).ToString();
}
