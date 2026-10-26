using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.KnowledgeBases;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;

namespace Quorum.Integration.Tests.Tools;

/// <summary>The list query on servers and tools (spec 010 AC-5, AC-7, AC-8, AC-9, server side).</summary>
public sealed class ToolListQueryTests(PostgresFixture postgres)
{
    private const string Servers = "/api/tools/servers";
    private const string Rag = "/api/tools/rag";
    private const string Lookup = "/api/tools/lookup";

    private static readonly Guid AssistantA = Guid.NewGuid();
    private static readonly Guid AssistantB = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-5
    public async Task ServersFilterByScopeTenantAssistantStatusAndSearch()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var mine = await TenantScope.TenantIdAsync(app);
        var friend = Guid.NewGuid();
        await AddServerAsync(app, mine, "files", ToolScopeKind.Global);
        await AddServerAsync(app, mine, "wiki", ToolScopeKind.Tenant, tenants: [friend], status: McpServerStatus.Unavailable);
        await AddServerAsync(app, mine, "for-a", ToolScopeKind.Assistant, assistants: [AssistantA]);
        await AddServerAsync(app, mine, "for-b", ToolScopeKind.Assistant, assistants: [AssistantB]);
        await AddServerAsync(app, Guid.NewGuid(), "their-global", ToolScopeKind.Global);
        await AddServerAsync(app, Guid.NewGuid(), "their-private", ToolScopeKind.Tenant);

        Assert.Equal(["files", "for-a", "for-b", "their-global", "wiki"], await NamesAsync(admin, Servers, string.Empty));
        Assert.Equal(["files", "their-global"], await NamesAsync(admin, Servers, "scope=global"));
        Assert.Equal(["wiki"], await NamesAsync(admin, Servers, "tenant=" + friend));
        Assert.Equal(["files", "for-a", "their-global", "wiki"], await NamesAsync(admin, Servers, "assistant=" + AssistantA));
        Assert.Equal(["wiki"], await NamesAsync(admin, Servers, "status=unavailable"));
        Assert.Equal(["files", "for-a", "for-b", "their-global"], await NamesAsync(admin, Servers, "status=Available"));
        Assert.Equal(["files", "their-global"], await NamesAsync(admin, Servers, "q=I&scope=global"));
        Assert.Equal(["for-a", "for-b"], await NamesAsync(admin, Servers, "q=for-"));
    }

    [Fact] // AC-5
    public async Task ServersArePagedInNameOrderWithTheirToolCounts()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var mine = await TenantScope.TenantIdAsync(app);
        await AddServerAsync(app, mine, "charlie", ToolScopeKind.Tenant, tools: [("a", ToolState.Enabled), ("b", ToolState.PendingReview), ("c", ToolState.PendingReview)]);
        await AddServerAsync(app, mine, "alpha", ToolScopeKind.Tenant);
        await AddServerAsync(app, mine, "bravo", ToolScopeKind.Tenant);

        var first = await GetPageAsync<ServerBody>(admin, $"{Servers}?limit=2");
        var rest = await GetPageAsync<ServerBody>(admin, $"{Servers}?limit=2&cursor={first.Cursor}");

        Assert.Equal(["alpha", "bravo"], first.Items.Select(s => s.Name));
        var charlie = Assert.Single(rest.Items);
        Assert.Equal(("charlie", 3, 2), (charlie.Name, charlie.ToolCount, charlie.PendingReviewCount));
        Assert.Null(rest.Cursor);
    }

    [Theory] // AC-5
    [InlineData(Servers)]
    [InlineData(Rag)]
    [InlineData(Lookup)]
    public async Task TheTypeFilterMeansNothingHereAndIsRefused(string route)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        using var response = await admin.GetAsync(new Uri($"{route}?type=Qdrant", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("list.invalid_filter", await KnowledgeBaseApi.ProblemCodeAsync(response));
    }

    [Theory] // AC-5
    [InlineData(Servers + "?scope=nope")]
    [InlineData(Servers + "?status=gone")]
    [InlineData(Rag + "?limit=500")]
    [InlineData(Lookup + "?cursor=junk")]
    public async Task AnInvalidFilterLimitOrCursorIsRefused(string uri)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        using var response = await admin.GetAsync(new Uri(uri, UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact] // AC-5
    public async Task RagToolsFilterByScopeStateTenantAndAssistantAndPage()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var mine = await TenantScope.TenantIdAsync(app);
        var friend = Guid.NewGuid();
        await AddRagToolAsync(app, mine, "global_docs", ToolScopeKind.Global);
        await AddRagToolAsync(app, mine, "tenant_docs", ToolScopeKind.Tenant, tenants: [friend], state: ToolState.PendingReview);
        await AddRagToolAsync(app, mine, "for_a", ToolScopeKind.Assistant, assistants: [AssistantA], state: ToolState.Disabled);
        await AddRagToolAsync(app, mine, "for_b", ToolScopeKind.Assistant, assistants: [AssistantB]);
        await AddRagToolAsync(app, Guid.NewGuid(), "their_global", ToolScopeKind.Global);
        await AddRagToolAsync(app, Guid.NewGuid(), "their_disabled", ToolScopeKind.Global, state: ToolState.Disabled);

        Assert.Equal(["for_a", "for_b", "global_docs", "tenant_docs", "their_global"], await NamesAsync(admin, Rag, string.Empty));
        Assert.Equal(["global_docs", "their_global"], await NamesAsync(admin, Rag, "scope=global"));
        Assert.Equal(["tenant_docs"], await NamesAsync(admin, Rag, "tenant=" + friend));
        Assert.Equal(["tenant_docs"], await NamesAsync(admin, Rag, "status=pendingreview"));
        Assert.Equal(["tenant_docs"], await NamesAsync(admin, Rag, "status=To%20review"));
        Assert.Equal(["for_a"], await NamesAsync(admin, Rag, "status=disabled"));
        Assert.Equal(["for_a", "global_docs", "tenant_docs", "their_global"], await NamesAsync(admin, Rag, "assistant=" + AssistantA));
        Assert.Equal(["for_a", "for_b"], await NamesAsync(admin, Rag, "q=FOR_"));

        var first = await GetPageAsync<NamedBody>(admin, $"{Rag}?limit=2");
        var second = await GetPageAsync<NamedBody>(admin, $"{Rag}?limit=2&cursor={first.Cursor}");
        Assert.Equal(["for_a", "for_b"], first.Items.Select(t => t.Name));
        Assert.Equal(["global_docs", "tenant_docs"], second.Items.Select(t => t.Name));
        Assert.NotNull(second.Cursor);
    }

    [Fact] // AC-5
    public async Task LookupToolsFilterByScopeStateAndSearchAndShowOnlyWhatTheTenantCanSee()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var mine = await TenantScope.TenantIdAsync(app);
        await AddLookupToolAsync(app, mine, "countries", ToolScopeKind.Global);
        await AddLookupToolAsync(app, mine, "regions", ToolScopeKind.Tenant, state: ToolState.Disabled);
        await AddLookupToolAsync(app, Guid.NewGuid(), "their_private", ToolScopeKind.Tenant);
        await AddLookupToolAsync(app, Guid.NewGuid(), "their_global", ToolScopeKind.Global);

        Assert.Equal(["countries", "regions", "their_global"], await NamesAsync(admin, Lookup, string.Empty));
        Assert.Equal(["regions"], await NamesAsync(admin, Lookup, "status=disabled"));
        Assert.Equal(["countries", "their_global"], await NamesAsync(admin, Lookup, "scope=global"));
        Assert.Equal(["countries"], await NamesAsync(admin, Lookup, "q=count"));
    }

    [Fact] // AC-8, AC-9
    public async Task TheToolsOfAServerArePagedAndFilteredByStateAndSearch()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var mine = await TenantScope.TenantIdAsync(app);
        var server = await AddServerAsync(app, mine, "files", ToolScopeKind.Tenant, tools: [("read", ToolState.Enabled), ("write", ToolState.PendingReview), ("search", ToolState.Enabled), ("delete", ToolState.PendingReview)]);

        var all = await GetPageAsync<ToolBody>(admin, $"{Servers}/{server}/tools");
        var review = await GetPageAsync<ToolBody>(admin, $"{Servers}/{server}/tools?status=pendingreview");
        var searched = await GetPageAsync<ToolBody>(admin, $"{Servers}/{server}/tools?q=ea");
        var first = await GetPageAsync<ToolBody>(admin, $"{Servers}/{server}/tools?limit=3");
        var rest = await GetPageAsync<ToolBody>(admin, $"{Servers}/{server}/tools?limit=3&cursor={first.Cursor}");

        Assert.Equal(["delete", "read", "search", "write"], all.Items.Select(t => t.Name));
        Assert.Equal(["delete", "write"], review.Items.Select(t => t.Name));
        Assert.Equal(["read", "search"], searched.Items.Select(t => t.Name));
        Assert.Equal(["delete", "read", "search"], first.Items.Select(t => t.Name));
        Assert.Equal(["write"], rest.Items.Select(t => t.Name));
        Assert.All(all.Items, t => Assert.Equal(server, t.ServerId));
        Assert.All(all.Items, t => Assert.Equal("Tenant", t.ServerScope!.Kind));
        Assert.All(all.Items, t => Assert.False(t.Shared));
    }

    [Fact] // AC-8, AC-9
    public async Task ASharedServersToolsAreReadOnlyAndTheOwnersTenantsAreHidden()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var server = await AddServerAsync(app, Guid.NewGuid(), "theirs", ToolScopeKind.Global, tools: [("read", ToolState.Enabled), ("hidden", ToolState.PendingReview)]);

        var page = await GetPageAsync<ToolBody>(admin, $"{Servers}/{server}/tools");

        var tool = Assert.Single(page.Items);
        Assert.Equal("read", tool.Name);
        Assert.True(tool.Shared);
        Assert.Equal("Global", tool.ServerScope!.Kind);
        Assert.Empty(tool.ServerScope.TenantIds);
    }

    [Fact] // AC-8
    public async Task TheToolsOfAServerTheTenantCannotSeeAreNotFound()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var hidden = await AddServerAsync(app, Guid.NewGuid(), "private", ToolScopeKind.Tenant);

        using var missing = await admin.GetAsync(new Uri($"{Servers}/{hidden}/tools", UriKind.Relative), Ct);
        using var unknown = await admin.GetAsync(new Uri($"{Servers}/{Guid.NewGuid()}/tools", UriKind.Relative), Ct);
        using var typed = await admin.GetAsync(new Uri($"{Servers}/{hidden}/tools?type=x", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, typed.StatusCode);
    }

    [Fact] // AC-9
    public async Task TheBuiltInListTakesAStateAndASearchAndRefusesAScope()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var mine = await TenantScope.TenantIdAsync(app);
        await AddServerAsync(app, mine, "files", ToolScopeKind.Tenant, tools: [("read", ToolState.Enabled), ("write", ToolState.PendingReview)]);

        var all = await admin.GetFromJsonAsync<List<ToolBody>>(new Uri("/api/tools", UriKind.Relative), Ct);
        var review = await admin.GetFromJsonAsync<List<ToolBody>>(new Uri("/api/tools?status=pendingreview", UriKind.Relative), Ct);
        var searched = await admin.GetFromJsonAsync<List<ToolBody>>(new Uri("/api/tools?q=WRI", UriKind.Relative), Ct);
        using var scoped = await admin.GetAsync(new Uri("/api/tools?scope=global", UriKind.Relative), Ct);

        Assert.Contains(all!, t => t.Name == "read" && t.ServerId is not null && t.ServerScope!.Kind == "Tenant");
        Assert.Equal(["write"], review!.Select(t => t.Name));
        Assert.Contains("write", searched!.Select(t => t.Name));
        Assert.DoesNotContain("read", searched!.Select(t => t.Name));
        Assert.Equal(HttpStatusCode.BadRequest, scoped.StatusCode);
    }

    [Fact] // AC-5
    public async Task AnonymousAndNonAdminCannotList()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        using var member = await app.CreateMemberClientAsync();
        using var anonymous = app.CreateUiClient();

        using var forbidden = await member.GetAsync(new Uri(Servers, UriKind.Relative), Ct);
        using var unauthorized = await anonymous.GetAsync(new Uri(Servers, UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
    }

    private static async Task<IReadOnlyList<string>> NamesAsync(HttpClient client, string route, string query) =>
        [.. (await GetPageAsync<NamedBody>(client, $"{route}?{query}")).Items.Select(i => i.Name).Order(StringComparer.Ordinal)];

    private static async Task<KnowledgeBaseApi.PageBody<T>> GetPageAsync<T>(HttpClient client, string uri) =>
        (await client.GetFromJsonAsync<KnowledgeBaseApi.PageBody<T>>(new Uri(uri, UriKind.Relative), Ct))!;

    private static async Task<Guid> AddServerAsync(
        QuorumApp app,
        Guid owner,
        string name,
        ToolScopeKind scope,
        Guid[]? tenants = null,
        Guid[]? assistants = null,
        McpServerStatus status = McpServerStatus.Available,
        (string Name, ToolState State)[]? tools = null)
    {
        await using var services = app.Services.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<AgentsDbContext>();
        var now = DateTimeOffset.UtcNow;
        var server = McpServer.Create(owner, name, $"http://{name}.test/mcp", protectedHeaders: null, scope, tenants ?? [], assistants ?? [], now);
        if (status == McpServerStatus.Available)
        {
            server.MarkAvailable(now);
        }
        else
        {
            server.MarkUnavailable("down", now);
        }

        db.McpServers.Add(server);
        foreach (var (toolName, state) in tools ?? [])
        {
            var tool = Tool.CreateMcp(owner, server.Id, toolName, $"The {toolName} tool.", """{"type":"object"}""", $"hash-{name}-{toolName}", annotations: null);
            if (state != ToolState.PendingReview)
            {
                tool.Review(state, ToolPolicy.Auto);
            }

            db.Tools.Add(tool);
        }

        await db.SaveChangesAsync(Ct);
        return server.Id;
    }

    private static async Task AddRagToolAsync(
        QuorumApp app,
        Guid owner,
        string name,
        ToolScopeKind scope,
        Guid[]? tenants = null,
        Guid[]? assistants = null,
        ToolState state = ToolState.Enabled)
    {
        await using var services = app.Services.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<AgentsDbContext>();
        var configuration = new RagConfiguration([], [], "{}", [], 5, null, 6000, scope, [.. tenants ?? []], [.. assistants ?? []]);
        var tool = Tool.CreateRag(owner, name, $"The {name} tool.", "hash-" + name, ToolPolicy.Auto);
        if (state != ToolState.Enabled)
        {
            tool.Review(state, ToolPolicy.Auto);
        }

        db.Tools.Add(tool);
        db.RagTools.Add(RagTool.Create(tool.Id, owner, configuration, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(Ct);
    }

    private static async Task AddLookupToolAsync(
        QuorumApp app,
        Guid owner,
        string name,
        ToolScopeKind scope,
        ToolState state = ToolState.Enabled)
    {
        await using var services = app.Services.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<AgentsDbContext>();
        var configuration = new LookupConfiguration([], null, [], [], null, "{}", [], scope, [], []);
        var tool = Tool.CreateLookup(owner, name, $"The {name} tool.", "hash-" + name, ToolPolicy.Auto);
        if (state != ToolState.Enabled)
        {
            tool.Review(state, ToolPolicy.Auto);
        }

        db.Tools.Add(tool);
        db.LookupTools.Add(LookupTool.Create(tool.Id, owner, configuration, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(Ct);
    }

    private sealed record NamedBody(string Name);

    private sealed record ServerBody(Guid Id, string Name, int ToolCount, int PendingReviewCount);

    private sealed record ToolBody(Guid Id, string Name, Guid? ServerId, KnowledgeBaseApi.ScopeBody? ServerScope, bool Shared);
}
