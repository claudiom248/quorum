using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.Tools;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;

namespace Quorum.Integration.Tests.WebSearch;

/// <summary>Who can use a web search tool: the scope rules of the pool, activation, and what another tenant's tool can do (spec 005, AC-1, AC-2, AC-10).</summary>
public sealed class WebSearchToolPoolTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-1
    public async Task AnAssistantScopeOnlyPutsTheToolInThePoolOfTheAssistantsItLists()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        await WebSearchApi.ConfigureAsync(admin);
        var assistant = await ToolsApi.DefaultAssistantIdAsync(admin);
        var other = await AssistantFixtures.AddAsync(app);
        await WebSearchApi.CreateOkAsync(admin, WebSearchApi.Body(name: "for-other", scope: new { kind = "Assistant", assistantIds = new[] { other } }));
        await WebSearchApi.CreateOkAsync(admin, WebSearchApi.Body(name: "for-default", scope: new { kind = "Assistant", assistantIds = new[] { assistant } }));
        await WebSearchApi.CreateOkAsync(admin, WebSearchApi.Body(name: "for-tenant"));

        var defaultPool = await PoolNamesAsync(admin, assistant);
        var otherPool = await PoolNamesAsync(admin, other);

        Assert.Equal(["web_search__for-default", "web_search__for-tenant"], defaultPool);
        Assert.Equal(["web_search__for-other", "web_search__for-tenant"], otherPool);
    }

    [Fact] // AC-2
    public async Task AWebSearchToolReachesTheModelOnlyWhenItIsActivated()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        await WebSearchApi.ConfigureAsync(admin);
        await WebSearchApi.CreateOkAsync(admin, WebSearchApi.Body(name: "research"));
        var assistant = await ToolsApi.DefaultAssistantIdAsync(admin);
        var chat = await ChatApi.CreateConversationAsync(admin);
        app.ModelServer.EnqueueRounds(FakeModelServer.ChatScript.Reply("one."), FakeModelServer.ChatScript.Reply("two."));
        await ToolFixtures.ActivateOnlyAsync(admin, "builtin__calculator");

        await ChatApi.SendAsync(admin, chat, "Hi");
        await ToolFixtures.ActivateOnlyAsync(admin, "builtin__calculator", "web_search__research");
        await ChatApi.SendAsync(admin, chat, "Again");

        string[] Offered(int request) =>
            [.. app.ModelServer.ChatRequests[request].GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("function").GetProperty("name").GetString()!)];
        Assert.Equal(["builtin__calculator"], Offered(0));
        Assert.Equal(["builtin__calculator", "web_search__research"], Offered(1));

        // What the model is offered is the generated definition: one argument, the query.
        var offered = app.ModelServer.ChatRequests[1].GetProperty("tools").EnumerateArray()
            .Single(t => t.GetProperty("function").GetProperty("name").GetString() == "web_search__research")
            .GetProperty("function").GetProperty("parameters");
        Assert.Equal(["query"], [.. offered.GetProperty("properties").EnumerateObject().Select(p => p.Name)]);
    }

    [Fact] // AC-1
    public async Task ADisabledWebSearchToolIsInNoPool()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        await WebSearchApi.ConfigureAsync(admin);
        var created = await WebSearchApi.CreateOkAsync(admin, WebSearchApi.Body(name: "research"));
        var assistant = await ToolsApi.DefaultAssistantIdAsync(admin);

        using var disable = await admin.PutAsJsonAsync(ToolsApi.Tool(created.Id), new { state = "Disabled", policy = "Auto" }, Ct);
        disable.EnsureSuccessStatusCode();

        Assert.DoesNotContain(await PoolNamesAsync(admin, assistant), name => name == "web_search__research");
    }

    [Fact] // AC-10
    public async Task AnotherTenantsWebSearchToolIsVisibleThroughItsScopeAndNeverWritable()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        await WebSearchApi.ConfigureAsync(admin);
        var stranger = Guid.CreateVersion7();
        var assistant = await ToolsApi.DefaultAssistantIdAsync(admin);
        var shared = await AddForeignToolAsync(app, stranger, "shared-global", ToolScopeKind.Global);
        await AddForeignToolAsync(app, stranger, "elsewhere", ToolScopeKind.Tenant, tenantIds: [Guid.NewGuid()]);

        var listed = await WebSearchApi.ListAsync(admin);
        var pool = await PoolNamesAsync(admin, assistant);
        using var read = await admin.GetAsync(WebSearchApi.WebSearchTool(shared), Ct);
        using var edit = await WebSearchApi.UpdateAsync(admin, shared, WebSearchApi.Body(name: "mine-now"));
        using var remove = await admin.DeleteAsync(WebSearchApi.WebSearchTool(shared), Ct);

        var sharedTool = Assert.Single(listed, t => t.Id == shared);
        Assert.True(sharedTool.Shared);
        Assert.Equal(["web_search__shared-global"], pool);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, edit.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, remove.StatusCode);
        // A shared tool says nothing of the configuration another tenant owns.
        Assert.Equal(0, sharedTool.TopN);
        Assert.Empty(sharedTool.Scope.TenantIds);
    }

    /// <summary>The web search names in the assistant's pool, in order.</summary>
    private static async Task<string[]> PoolNamesAsync(HttpClient admin, Guid assistantId) =>
        [.. (await ToolsApi.GetAssistantToolsAsync(admin, assistantId)).Pool
            .Select(t => t.Name)
            .Where(n => n.StartsWith("web_search__", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];

    /// <summary>A web search tool of another tenant, written straight to the database: the API has no way to create a second tenant yet.</summary>
    private static async Task<Guid> AddForeignToolAsync(
        QuorumApp app,
        Guid owner,
        string name,
        ToolScopeKind scope,
        IEnumerable<Guid>? tenantIds = null)
    {
        await using var services = app.Services.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<AgentsDbContext>();
        var configuration = new WebSearchToolConfiguration(5, null, null, 1, null, 5, scope, [.. tenantIds ?? []], []);
        var tool = Tool.CreateWebSearch(owner, name, "A tool of another tenant.", "hash-" + name, ToolPolicy.Auto);

        db.Tools.Add(tool);
        db.WebSearchTools.Add(WebSearchTool.Create(tool.Id, owner, configuration, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(Ct);
        return tool.Id;
    }
}
