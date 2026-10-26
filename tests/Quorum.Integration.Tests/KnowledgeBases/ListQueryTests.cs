using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.KnowledgeBases.Lookups;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Persistence;

namespace Quorum.Integration.Tests.KnowledgeBases;

/// <summary>The list query (spec 010 AC-5, AC-7, AC-17) on the three knowledge lists: shape, filters, cursor and isolation.</summary>
public sealed class ListQueryTests(PostgresFixture postgres)
{
    private static readonly Guid AssistantA = Guid.NewGuid();
    private static readonly Guid AssistantB = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-17
    public async Task KnowledgeBaseItemsCarryTheScopeObjectAndAnotherTenantsItemsHideTheOwnersTenants()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var mine = await TenantScope.TenantIdAsync(app);
        var friend = Guid.NewGuid();
        await AddAsync(app, mine, "assistants", KnowledgeBaseScope.Assistant, assistants: [AssistantA]);
        await AddAsync(app, mine, "shared-out", KnowledgeBaseScope.Tenant, tenants: [friend]);
        await AddAsync(app, Guid.NewGuid(), "from-elsewhere", KnowledgeBaseScope.Tenant, tenants: [mine, friend]);

        var items = (await GetPageAsync<KnowledgeBaseApi.ListItemBody>(admin, "/api/knowledge-bases")).Items;

        var assistantScoped = Assert.Single(items, k => k.Name == "assistants").Scope!;
        Assert.Equal("Assistant", assistantScoped.Kind);
        Assert.Equal([AssistantA], assistantScoped.AssistantIds);
        Assert.Equal([friend], Assert.Single(items, k => k.Name == "shared-out").Scope!.TenantIds);
        var foreign = Assert.Single(items, k => k.Name == "from-elsewhere");
        Assert.True(foreign.Shared);
        Assert.Equal("Tenant", foreign.Scope!.Kind);
        Assert.Empty(foreign.Scope.TenantIds);
        Assert.Empty(foreign.Scope.AssistantIds);
    }

    [Fact] // AC-17
    public async Task DictionaryFieldsAndManualLookupsCarryTheScopeObject()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var friend = Guid.NewGuid();
        await DictionaryApi.CreateOkAsync(admin, "year", scope: "Tenant");
        await DictionaryFixtures.AddFieldOfAnotherTenantAsync(app, "global_field", DictionaryFieldScope.Global);
        using var created = await LookupApi.CreateManualAsync(admin, LookupApi.ManualBody(scope: "Tenant", scopeTenantIds: [friend]));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await LookupApi.SeedManualOfAnotherTenantAsync(app, "theirs", LookupScope.Tenant, await TenantScope.TenantIdAsync(app));

        var fields = await DictionaryApi.ListAsync(admin);
        var lookups = await LookupApi.ListManualAsync(admin);

        Assert.Equal("Tenant", Assert.Single(fields, f => f.Name == "year").Scope.Kind);
        var global = Assert.Single(fields, f => f.Name == "global_field");
        Assert.Equal("Global", global.Scope.Kind);
        Assert.Empty(global.Scope.TenantIds);
        Assert.Empty(global.Scope.AssistantIds);
        Assert.Equal([friend], Assert.Single(lookups, l => !l.Shared).Scope.TenantIds);
        Assert.Empty(Assert.Single(lookups, l => l.Shared).Scope.TenantIds);
    }

    [Fact] // AC-5
    public async Task EachKnowledgeBaseFilterWorksAloneAndTogether()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var mine = await TenantScope.TenantIdAsync(app);
        var friend = Guid.NewGuid();
        await AddAsync(app, mine, "global-handbook", KnowledgeBaseScope.Global);
        await AddAsync(app, mine, "tenant-wiki", KnowledgeBaseScope.Tenant, tenants: [friend]);
        await AddAsync(app, mine, "for-a", KnowledgeBaseScope.Assistant, assistants: [AssistantA]);
        await AddAsync(app, mine, "for-b", KnowledgeBaseScope.Assistant, assistants: [AssistantB]);
        await AddAsync(app, Guid.NewGuid(), "their-global", KnowledgeBaseScope.Global);

        Assert.Equal(["for-a", "for-b"], await NamesAsync(admin, "scope=assistant"));
        Assert.Equal(["global-handbook", "their-global"], await NamesAsync(admin, "scope=Global"));
        Assert.Equal(["for-a", "for-b", "global-handbook"], await NamesAsync(admin, "scope=assistant,global&tenant=" + mine));
        Assert.Equal(["tenant-wiki"], await NamesAsync(admin, "tenant=" + friend));
        Assert.Equal(["global-handbook", "their-global"], await NamesAsync(admin, "q=GLOBAL"));
        Assert.Equal(["global-handbook", "tenant-wiki", "their-global"], await NamesAsync(admin, "q=h&scope=global,tenant&type=qdrant&status=available"));
        Assert.Empty(await NamesAsync(admin, "status=unavailable"));
        Assert.Empty(await NamesAsync(admin, "type=qdrant&q=nothing-like-this"));
    }

    [Fact] // AC-7
    public async Task TheAssistantFilterKeepsItsOwnScopedItemsAndEverythingNotScopedToAssistants()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var mine = await TenantScope.TenantIdAsync(app);
        await AddAsync(app, mine, "global-handbook", KnowledgeBaseScope.Global);
        await AddAsync(app, mine, "for-a", KnowledgeBaseScope.Assistant, assistants: [AssistantA]);
        await AddAsync(app, mine, "for-b", KnowledgeBaseScope.Assistant, assistants: [AssistantB]);

        Assert.Equal(["for-a", "global-handbook"], await NamesAsync(admin, "assistant=" + AssistantA));
        Assert.Equal(["for-a", "for-b", "global-handbook"], await NamesAsync(admin, $"assistant={AssistantA},{AssistantB}"));
    }

    [Fact] // AC-5
    public async Task AnotherTenantsPrivateKnowledgeBaseIsNeverListedWhateverTheFilter()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var elsewhere = Guid.NewGuid();
        await AddAsync(app, elsewhere, "private-one", KnowledgeBaseScope.Tenant);
        await AddAsync(app, elsewhere, "private-assistant", KnowledgeBaseScope.Assistant, assistants: [AssistantA]);

        Assert.Empty(await NamesAsync(admin, string.Empty));
        Assert.Empty(await NamesAsync(admin, "tenant=" + elsewhere));
        Assert.Empty(await NamesAsync(admin, "q=private"));
    }

    [Fact] // AC-5
    public async Task ThePageIsInNameOrderAndTheCursorWalksEveryItemOnceWhateverTheLimit()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var mine = await TenantScope.TenantIdAsync(app);
        foreach (var name in new[] { "echo", "alpha", "delta", "bravo", "charlie" })
        {
            await AddAsync(app, mine, name, KnowledgeBaseScope.Global);
        }

        var first = await GetPageAsync<KnowledgeBaseApi.ListItemBody>(admin, "/api/knowledge-bases?limit=2");
        var second = await GetPageAsync<KnowledgeBaseApi.ListItemBody>(admin, $"/api/knowledge-bases?limit=2&cursor={first.Cursor}");
        var third = await GetPageAsync<KnowledgeBaseApi.ListItemBody>(admin, $"/api/knowledge-bases?limit=2&cursor={second.Cursor}");

        Assert.Equal(["alpha", "bravo"], first.Items.Select(k => k.Name));
        Assert.Equal(["charlie", "delta"], second.Items.Select(k => k.Name));
        Assert.Equal(["echo"], third.Items.Select(k => k.Name));
        Assert.Null(third.Cursor);
    }

    [Fact] // AC-5
    public async Task ACursorKeepsWorkingWhenTheFilterChanges()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var mine = await TenantScope.TenantIdAsync(app);
        await AddAsync(app, mine, "alpha", KnowledgeBaseScope.Global);
        await AddAsync(app, mine, "bravo", KnowledgeBaseScope.Tenant);
        await AddAsync(app, mine, "charlie", KnowledgeBaseScope.Tenant);
        var first = await GetPageAsync<KnowledgeBaseApi.ListItemBody>(admin, "/api/knowledge-bases?limit=1");

        var other = await GetPageAsync<KnowledgeBaseApi.ListItemBody>(admin, $"/api/knowledge-bases?scope=tenant&cursor={first.Cursor}");

        Assert.Equal(["bravo", "charlie"], other.Items.Select(k => k.Name));
    }

    [Theory] // AC-5
    [InlineData("limit=0")]
    [InlineData("limit=201")]
    [InlineData("limit=many")]
    public async Task ALimitOutOfRangeIsRejected(string query)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        using var response = await admin.GetAsync(new Uri("/api/knowledge-bases?" + query, UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("list.invalid_limit", await KnowledgeBaseApi.ProblemCodeAsync(response));
    }

    [Fact] // AC-5
    public async Task AMalformedCursorOrOneFromAnotherListIsRejected()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var mine = await TenantScope.TenantIdAsync(app);
        await AddAsync(app, mine, "alpha", KnowledgeBaseScope.Global);
        await AddAsync(app, mine, "bravo", KnowledgeBaseScope.Global);
        await DictionaryApi.CreateOkAsync(admin, "year");
        await DictionaryApi.CreateOkAsync(admin, "zone");
        var dictionary = await GetPageAsync<DictionaryApi.FieldBody>(admin, "/api/knowledge-bases/dictionary?limit=1");

        using var garbage = await admin.GetAsync(new Uri("/api/knowledge-bases?cursor=not-a-cursor", UriKind.Relative), Ct);
        using var foreign = await admin.GetAsync(new Uri($"/api/knowledge-bases?cursor={dictionary.Cursor}", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
        Assert.Equal("list.invalid_cursor", await KnowledgeBaseApi.ProblemCodeAsync(garbage));
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Equal("list.invalid_cursor", await KnowledgeBaseApi.ProblemCodeAsync(foreign));
    }

    [Theory] // AC-5
    [InlineData("/api/knowledge-bases?scope=nope")]
    [InlineData("/api/knowledge-bases?tenant=not-an-id")]
    [InlineData("/api/knowledge-bases?status=gone")]
    [InlineData("/api/knowledge-bases/dictionary?status=Available")]
    [InlineData("/api/knowledge-bases/dictionary?type=Colour")]
    [InlineData("/api/knowledge-bases/manual-lookups?type=Text")]
    public async Task AnUnknownFilterValueIsRejected(string uri)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        using var response = await admin.GetAsync(new Uri(uri, UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact] // AC-5
    public async Task DictionaryFieldsFilterByScopeTypeTenantAndSearchAndPage()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var mine = await TenantScope.TenantIdAsync(app);
        await DictionaryApi.CreateOkAsync(admin, "year", "Number", "The year.", "Tenant");
        await DictionaryApi.CreateOkAsync(admin, "country", "Text", "Where.", "Global");
        await DictionaryApi.CreateOkAsync(admin, "city", "Text", "A city.", "Tenant");
        await DictionaryFixtures.AddFieldOfAnotherTenantAsync(app, "their_field", DictionaryFieldScope.Global);

        Assert.Equal(["country", "their_field"], await NamesAsync(admin, "scope=global", "/api/knowledge-bases/dictionary"));
        Assert.Equal(["city", "year"], await NamesAsync(admin, "scope=tenant", "/api/knowledge-bases/dictionary"));
        Assert.Equal(["year"], await NamesAsync(admin, "type=number", "/api/knowledge-bases/dictionary"));
        Assert.Equal(["city", "country", "year"], await NamesAsync(admin, "tenant=" + mine, "/api/knowledge-bases/dictionary"));
        Assert.Equal(["city"], await NamesAsync(admin, "q=a%20city", "/api/knowledge-bases/dictionary"));

        var first = await GetPageAsync<DictionaryApi.FieldBody>(admin, "/api/knowledge-bases/dictionary?limit=2");
        var rest = await GetPageAsync<DictionaryApi.FieldBody>(admin, $"/api/knowledge-bases/dictionary?limit=2&cursor={first.Cursor}");
        Assert.Equal(["city", "country"], first.Items.Select(f => f.Name));
        Assert.Equal(["their_field", "year"], rest.Items.Select(f => f.Name));
        Assert.Null(rest.Cursor);
    }

    [Fact] // AC-5
    public async Task SearchTextIsNotAPattern()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        await DictionaryApi.CreateOkAsync(admin, "plain");
        await DictionaryApi.CreateOkAsync(admin, "with_underscore");

        Assert.Equal(["with_underscore"], await NamesAsync(admin, "q=h_u", "/api/knowledge-bases/dictionary"));
        Assert.Empty(await NamesAsync(admin, "q=%25", "/api/knowledge-bases/dictionary"));
    }

    [Fact] // AC-5
    public async Task ManualLookupsFilterByScopeAndTenantAndPage()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var mine = await TenantScope.TenantIdAsync(app);
        var friend = Guid.NewGuid();
        foreach (var name in new[] { "alpha_list", "bravo_list" })
        {
            using var created = await LookupApi.CreateManualAsync(admin, LookupApi.ManualBody(name: name, scope: "Tenant", scopeTenantIds: [friend]));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        await LookupApi.SeedManualOfAnotherTenantAsync(app, "charlie_list", LookupScope.Global);

        Assert.Equal(["charlie_list"], await NamesAsync(admin, "scope=global", "/api/knowledge-bases/manual-lookups"));
        Assert.Equal(["alpha_list", "bravo_list"], await NamesAsync(admin, "tenant=" + friend, "/api/knowledge-bases/manual-lookups"));
        Assert.Equal(["alpha_list", "bravo_list"], await NamesAsync(admin, "tenant=" + mine, "/api/knowledge-bases/manual-lookups"));

        var first = await GetPageAsync<LookupApi.ManualLookupBody>(admin, "/api/knowledge-bases/manual-lookups?limit=2");
        var rest = await GetPageAsync<LookupApi.ManualLookupBody>(admin, $"/api/knowledge-bases/manual-lookups?limit=2&cursor={first.Cursor}");
        Assert.Equal(["alpha_list", "bravo_list"], first.Items.Select(l => l.Name));
        Assert.Equal(["charlie_list"], rest.Items.Select(l => l.Name));
        Assert.All(first.Items, l => Assert.True(l.ValueCount > 0));
    }

    private static async Task<IReadOnlyList<string>> NamesAsync(HttpClient client, string query, string route = "/api/knowledge-bases")
    {
        var page = await GetPageAsync<NamedBody>(client, $"{route}?{query}");
        return [.. page.Items.Select(i => i.Name).Order(StringComparer.Ordinal)];
    }

    private static async Task<KnowledgeBaseApi.PageBody<T>> GetPageAsync<T>(HttpClient client, string uri) =>
        (await client.GetFromJsonAsync<KnowledgeBaseApi.PageBody<T>>(new Uri(uri, UriKind.Relative), Ct))!;

    private static async Task AddAsync(
        QuorumApp app,
        Guid tenantId,
        string name,
        KnowledgeBaseScope scope,
        Guid[]? tenants = null,
        Guid[]? assistants = null)
    {
        await using var services = app.Services.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
        db.KnowledgeBases.Add(KnowledgeBaseFixtures.Create(
            tenantId, name, "http://qdrant.test:6334", $"collection_{name}", scope, tenants ?? [], scopeAssistantIds: assistants));
        await db.SaveChangesAsync(Ct);
    }

    private sealed record NamedBody(string Name);
}
