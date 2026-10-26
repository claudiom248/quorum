using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Features.Lookups;
using Quorum.Modules.KnowledgeBases.Persistence;

namespace Quorum.Integration.Tests.KnowledgeBases.Lookups;

/// <summary>The aggregate list of lookups (spec 010 AC-19, server side): manual and derived in one page.</summary>
public sealed class LookupListTests(PostgresFixture postgres)
{
    private const string Route = "/api/knowledge-bases/lookups";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-19
    public async Task ManualAndDerivedLookupsAreListedTogetherWithTheirSource()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var mine = await TenantScope.TenantIdAsync(app);
        var friend = Guid.NewGuid();
        await LookupApi.CreateManualOkAsync(admin, LookupApi.ManualBody(name: "nationality", scope: "Tenant", scopeTenantIds: [friend]));
        var field = await DictionaryApi.CreateOkAsync(admin, "department");
        var (knowledgeBase, _) = await SeedDerivedAsync(app, mine, "policies", field, [("hr", 3), ("legal", 2)], KnowledgeBaseScope.Global);

        var items = (await GetAsync(admin, string.Empty)).Items;

        var manual = Assert.Single(items, i => i.Kind == "Manual");
        Assert.Equal(("nationality", 2, false), (manual.Name, manual.ValueCount, manual.Shared));
        Assert.Null(manual.KnowledgeBase);
        Assert.Equal([friend], manual.Scope.TenantIds);
        var derived = Assert.Single(items, i => i.Kind == "Derived");
        Assert.Equal(("department", "department", "Documents", 2), (derived.Name, derived.Field, derived.Unit, derived.ValueCount));
        Assert.Equal((knowledgeBase, "policies"), (derived.KnowledgeBase!.Id, derived.KnowledgeBase.Name));
        Assert.Equal("Global", derived.Scope.Kind);
    }

    [Fact] // AC-19
    public async Task AnotherTenantsDerivedLookupsAreNotListedButItsSharedManualOnesAre()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var elsewhere = Guid.NewGuid();
        var field = await DictionaryApi.CreateOkAsync(admin, "department");
        await SeedDerivedAsync(app, elsewhere, "their-policies", field, [("hr", 1)], KnowledgeBaseScope.Global);
        await LookupApi.SeedManualOfAnotherTenantAsync(app, "their-list", LookupScope.Global);

        var items = (await GetAsync(admin, string.Empty)).Items;

        var only = Assert.Single(items);
        Assert.Equal(("their-list", "Manual", true), (only.Name, only.Kind, only.Shared));
    }

    [Fact] // AC-19
    public async Task TheKindAndScopeFiltersAndTheSearchApply()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var mine = await TenantScope.TenantIdAsync(app);
        await LookupApi.CreateManualOkAsync(admin, LookupApi.ManualBody(name: "alpha_list", scope: "Global"));
        var field = await DictionaryApi.CreateOkAsync(admin, "department");
        var assistant = Guid.NewGuid();
        await SeedDerivedAsync(app, mine, "tenant-base", field, [("hr", 1)], KnowledgeBaseScope.Tenant);
        var otherField = await DictionaryApi.CreateOkAsync(admin, "region");
        await SeedDerivedAsync(app, mine, "assistant-base", otherField, [("eu", 1)], KnowledgeBaseScope.Assistant, assistant);

        Assert.Equal(["alpha_list"], await NamesAsync(admin, "kind=manual"));
        Assert.Equal(["department", "region"], await NamesAsync(admin, "kind=derived"));
        Assert.Equal(["alpha_list"], await NamesAsync(admin, "scope=global"));
        Assert.Equal(["region"], await NamesAsync(admin, "scope=assistant"));
        Assert.Equal(["alpha_list", "department"], await NamesAsync(admin, "assistant=" + Guid.NewGuid()));
        Assert.Equal(["alpha_list", "department", "region"], await NamesAsync(admin, "assistant=" + assistant));
        Assert.Equal(["department"], await NamesAsync(admin, "q=PART"));
    }

    [Fact] // AC-19
    public async Task ThePageIsInNameOrderAndTheCursorWalksEveryLookup()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var mine = await TenantScope.TenantIdAsync(app);
        foreach (var name in new[] { "echo", "bravo" })
        {
            await LookupApi.CreateManualOkAsync(admin, LookupApi.ManualBody(name: name));
        }

        var field = await DictionaryApi.CreateOkAsync(admin, "alpha");
        await SeedDerivedAsync(app, mine, "base", field, [("x", 1)], KnowledgeBaseScope.Tenant);

        var first = await GetAsync(admin, "limit=2");
        var rest = await GetAsync(admin, $"limit=2&cursor={first.Cursor}");

        Assert.Equal(["alpha", "bravo"], first.Items.Select(i => i.Name));
        Assert.Equal(["echo"], rest.Items.Select(i => i.Name));
        Assert.Null(rest.Cursor);
    }

    [Theory] // AC-19
    [InlineData("kind=nope")]
    [InlineData("type=Text")]
    [InlineData("status=Available")]
    public async Task AnUnsupportedFilterIsRejected(string query)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        using var response = await admin.GetAsync(new Uri($"{Route}?{query}", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact] // AC-19
    public async Task OnlyAnAdministratorCanList()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        using var member = await app.CreateMemberClientAsync();

        using var response = await member.GetAsync(new Uri(Route, UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task<IReadOnlyList<string>> NamesAsync(HttpClient client, string query) =>
        [.. (await GetAsync(client, query)).Items.Select(i => i.Name).Order(StringComparer.Ordinal)];

    private static async Task<KnowledgeBaseApi.PageBody<ItemBody>> GetAsync(HttpClient client, string query) =>
        (await client.GetFromJsonAsync<KnowledgeBaseApi.PageBody<ItemBody>>(new Uri($"{Route}?{query}", UriKind.Relative), Ct))!;

    /// <summary>A knowledge base with one computed lookup of the field, whose whole-knowledge-base slice holds the values.</summary>
    private static async Task<(Guid KnowledgeBaseId, Guid LookupId)> SeedDerivedAsync(
        QuorumApp app,
        Guid tenantId,
        string knowledgeBaseName,
        Guid fieldId,
        IReadOnlyList<(string Value, int Count)> values,
        KnowledgeBaseScope scope,
        params Guid[] assistants)
    {
        await using var services = app.Services.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
        var registration = KnowledgeBaseFixtures.Create(tenantId, knowledgeBaseName, "http://qdrant.test:6334", $"c_{knowledgeBaseName}", scope, [], scopeAssistantIds: assistants);
        db.KnowledgeBases.Add(registration);
        var now = DateTimeOffset.UtcNow;
        var lookup = Lookup.Computed(tenantId, registration.Id, fieldId, LookupUnit.Documents, now);
        var slice = LookupSlice.Create(tenantId, lookup.Id, LookupSliceKey.WholeKnowledgeBase.Hash, LookupSliceKey.WholeKnowledgeBase.Filters, now);
        db.Lookups.Add(lookup);
        db.LookupSlices.Add(slice);
        await db.SaveChangesAsync(Ct);
        await LookupApi.AddGenerationAsync(db, tenantId, slice, generation: 1, values);
        return (registration.Id, lookup.Id);
    }

    private sealed record KnowledgeBaseRef(Guid Id, string Name);

    private sealed record ItemBody(
        Guid Id,
        string Name,
        string Kind,
        KnowledgeBaseRef? KnowledgeBase,
        string? Field,
        string? Unit,
        KnowledgeBaseApi.ScopeBody Scope,
        int ValueCount,
        bool Shared);
}
