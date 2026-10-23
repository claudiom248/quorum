using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.KnowledgeBases.Features.Lookups;
using Quorum.Modules.KnowledgeBases.Storage;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBases.Lookups;

/// <summary>The lookups of a collection of entities: the name of each, its attributes, and the order by a number or a date (spec 007 D-4, AC-9).</summary>
public sealed class EntityLookupTests(PostgresFixture postgres, QdrantFixture qdrant)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly object[] SupplierFields =
    [
        new { name = "country", type = "Text" },
        new { name = "contract_count", type = "Number" },
        new { name = "supplier_since", type = "Date" },
    ];

    private static readonly string[] Attributes = ["country", "contract_count", "supplier_since"];

    private static object Mapping(string? entityName = "name", params string[] attributes) => new
    {
        roles = new { text = "text" },
        fields = Attributes.Select(name => new { path = name, name, entityAttribute = attributes.Contains(name) }).ToArray(),
        entityName,
    };

    private sealed record LookupBody(Guid Id, string Name, string Kind, string? Field, string? Unit, List<SliceBody> Slices);

    private sealed record SliceBody(string Hash, string Status, bool Stale, int ValueCount, bool Truncated, string? Error, bool Running);

    private async Task<(QuorumApp App, HttpClient Admin, string Url, string Collection)> StartAsync()
    {
        var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        // The real computer: the other tests of the lookups script a fake one.
        app.LookupComputer.Handler = new LookupComputer(app.Services.GetRequiredService<StoreAdapters>()).ComputeAsync;
        return (app, admin, (await qdrant.GetUrlAsync()).ToString(), await qdrant.SeedSharedAsync(SampleCollections.CreateSuppliers));
    }

    private static Task<HttpResponseMessage> RegisterAsync(HttpClient admin, string url, string collection, object mapping, object[]? createFields = null) =>
        KnowledgeBaseApi.RegisterAsync(admin, url, collection, name: "suppliers", description: "The suppliers of the company.", mapping: mapping, createFields: createFields ?? SupplierFields);

    private static async Task<KnowledgeBaseApi.RegisteredBody> RegisterOkAsync(HttpClient admin, string url, string collection, object mapping)
    {
        using var response = await RegisterAsync(admin, url, collection, mapping);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<KnowledgeBaseApi.RegisteredBody>(Ct))!;
    }

    private static async Task<List<LookupBody>> LookupsAsync(HttpClient admin, Guid knowledgeBaseId) =>
        (await admin.GetFromJsonAsync<List<LookupBody>>(LookupApi.KnowledgeBaseLookups(knowledgeBaseId), Ct))!;

    private static async Task<LookupBody> ReadyEntityLookupAsync(HttpClient admin, Guid knowledgeBaseId)
    {
        var lookups = await LookupApi.UntilAsync(
            () => LookupsAsync(admin, knowledgeBaseId),
            list => list.Any(l => l.Kind == "Entity" && l.Slices.All(s => s.Status == "Ready" && !s.Running)));
        return lookups.Single(l => l.Kind == "Entity");
    }

    private static async Task<LookupApi.ValuesBody> ValuesAsync(HttpClient admin, LookupBody lookup, string? query = null) =>
        await LookupApi.ReadValuesOkAsync(admin, lookup.Id, query);

    private static string[] Names(LookupApi.ValuesBody values) => [.. values.Values.Select(v => v.Value)];

    [Fact] // AC-9
    public async Task ANamedCollectionListsItsEntitiesWithTheirThreeAttributes()
    {
        var (app, admin, url, collection) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        var registered = await RegisterOkAsync(admin, url, collection, Mapping("name", Attributes));

        var lookup = await ReadyEntityLookupAsync(admin, registered.Id);
        Assert.Equal(("suppliers", null, 4), (lookup.Name, lookup.Unit, lookup.Slices.Single().ValueCount));
        var values = await ValuesAsync(admin, lookup);
        Assert.Null(values.Unit);
        Assert.Equal(["Acme", "Globex", "Initech", "Hooli"], Names(values));
        Assert.All(values.Values, v => Assert.Null(v.Count));
        var acme = values.Values[0].Attributes!.Value;
        Assert.Equal(("IT", 12, "2019-03-01"), (acme.GetProperty("country").GetString(), acme.GetProperty("contract_count").GetInt32(), acme.GetProperty("supplier_since").GetString()));
    }

    [Fact] // AC-9
    public async Task OnlyTheAttributesTheMappingMarksAreKept()
    {
        var (app, admin, url, collection) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        var registered = await RegisterOkAsync(admin, url, collection, Mapping("name", "contract_count"));

        var values = await ValuesAsync(admin, await ReadyEntityLookupAsync(admin, registered.Id));
        var attributes = values.Values[0].Attributes!.Value;
        Assert.Equal(["contract_count"], attributes.EnumerateObject().Select(p => p.Name));
    }

    [Theory] // AC-9
    [InlineData("-contract_count", new[] { "Acme", "Hooli", "Globex", "Initech" })]
    [InlineData("contract_count", new[] { "Initech", "Globex", "Hooli", "Acme" })]
    [InlineData("supplier_since", new[] { "Acme", "Hooli", "Globex", "Initech" })]
    [InlineData("-supplier_since", new[] { "Initech", "Globex", "Hooli", "Acme" })]
    public async Task TheListCanBeOrderedByAnAttributeOfTypeNumberOrDate(string sort, string[] expected)
    {
        var (app, admin, url, collection) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        var registered = await RegisterOkAsync(admin, url, collection, Mapping("name", Attributes));
        var lookup = await ReadyEntityLookupAsync(admin, registered.Id);

        var values = await ValuesAsync(admin, lookup, $"sort={sort}");

        Assert.Equal(expected, Names(values));
    }

    [Fact] // AC-9
    public async Task APageByAttributeContinuesWhereTheCursorStoppedAndRefusesTheCursorOfAnotherOrder()
    {
        var (app, admin, url, collection) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        var registered = await RegisterOkAsync(admin, url, collection, Mapping("name", Attributes));
        var lookup = await ReadyEntityLookupAsync(admin, registered.Id);

        var first = await ValuesAsync(admin, lookup, "sort=-contract_count&limit=2");
        var second = await ValuesAsync(admin, lookup, $"sort=-contract_count&limit=2&cursor={first.Cursor}");

        Assert.Equal(["Acme", "Hooli"], Names(first));
        Assert.Equal(["Globex", "Initech"], Names(second));
        Assert.Null(second.Cursor);
        foreach (var other in new[] { "sort=contract_count", "sort=name", "" })
        {
            using var response = await admin.GetAsync(LookupApi.Values(lookup.Id, $"{other}&cursor={first.Cursor}"), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("lookup.invalid_cursor", await KnowledgeBaseApi.ProblemCodeAsync(response));
        }
    }

    [Theory] // AC-9
    [InlineData("country")]
    [InlineData("-country")]
    [InlineData("attribute")]
    [InlineData("nothing")]
    public async Task AnAttributeOfTypeTextAndAnUnknownNameCannotBeSortedBy(string sort)
    {
        var (app, admin, url, collection) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        var registered = await RegisterOkAsync(admin, url, collection, Mapping("name", Attributes));
        var lookup = await ReadyEntityLookupAsync(admin, registered.Id);

        using var response = await admin.GetAsync(LookupApi.Values(lookup.Id, $"sort={sort}"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("lookup.invalid_sort", await KnowledgeBaseApi.ProblemCodeAsync(response));
    }

    [Fact] // AC-9
    public async Task MoreThanTenAttributesIsRefused()
    {
        var (app, admin, url, collection) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        var names = Enumerable.Range(0, 11).Select(i => $"attribute_{i}").ToArray();
        var mapping = new
        {
            roles = new { text = "text" },
            fields = names.Select(name => new { path = name, name, entityAttribute = true }).ToArray(),
            entityName = "name",
        };

        using var response = await RegisterAsync(admin, url, collection, mapping, [.. names.Select(name => (object)new { name, type = "Text" })]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("knowledge_base.too_many_entity_attributes", await KnowledgeBaseApi.ProblemCodeAsync(response));
        Assert.Empty(await KnowledgeBaseApi.ListAsync(admin));
    }

    [Fact] // AC-9
    public async Task TenAttributesAreAccepted()
    {
        var (app, admin, url, collection) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        var names = Enumerable.Range(0, 10).Select(i => $"attribute_{i}").ToArray();
        var mapping = new
        {
            roles = new { text = "text" },
            fields = names.Select(name => new { path = name, name, entityAttribute = true }).ToArray(),
            entityName = "name",
        };

        using var response = await RegisterAsync(admin, url, collection, mapping, [.. names.Select(name => (object)new { name, type = "Text" })]);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact] // AC-9
    public async Task AttributesWithoutAnEntityNameAreRefused()
    {
        var (app, admin, url, collection) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        using var response = await RegisterAsync(admin, url, collection, Mapping(entityName: null, "country"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("knowledge_base.invalid_mapping", await KnowledgeBaseApi.ProblemCodeAsync(response));
    }

    [Fact] // AC-9
    public async Task TheDetailCarriesTheEntityNameAndTheMarkedAttributes()
    {
        var (app, admin, url, collection) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        var registered = await RegisterOkAsync(admin, url, collection, Mapping("name", "country", "contract_count"));

        var mapping = (await KnowledgeBaseApi.GetAsync(admin, registered.Id)).Mapping!;
        Assert.Equal("name", mapping.EntityName);
        Assert.Equal([true, true, false], mapping.Fields.OrderBy(f => Array.IndexOf(Attributes, f.Path)).Select(f => f.EntityAttribute));
    }

    [Fact] // AC-9
    public async Task ChangingTheAttributesComputesTheListAgainAndDroppingTheEntityNameRemovesIt()
    {
        var (app, admin, url, collection) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        var registered = await RegisterOkAsync(admin, url, collection, Mapping("name", "country"));
        var lookup = await ReadyEntityLookupAsync(admin, registered.Id);

        using var changed = await admin.PutAsJsonAsync(
            KnowledgeBaseApi.One(registered.Id),
            KnowledgeBaseApi.Body(url, collection, name: "suppliers", description: "The suppliers of the company.", mapping: Mapping("name", "contract_count"), createFields: SupplierFields),
            Ct);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var values = await LookupApi.UntilAsync(
            () => ValuesAsync(admin, lookup),
            v => v.Values.Count > 0 && v.Values[0].Attributes!.Value.EnumerateObject().Any(p => p.Name == "contract_count"));
        Assert.Equal(["contract_count"], values.Values[0].Attributes!.Value.EnumerateObject().Select(p => p.Name));

        using var dropped = await admin.PutAsJsonAsync(
            KnowledgeBaseApi.One(registered.Id),
            KnowledgeBaseApi.Body(url, collection, name: "suppliers", description: "The suppliers of the company.", mapping: Mapping(entityName: null), createFields: SupplierFields),
            Ct);
        Assert.Equal(HttpStatusCode.OK, dropped.StatusCode);
        Assert.Empty(await LookupsAsync(admin, registered.Id));
    }

    [Fact] // AC-9
    public async Task ACollectionWithoutPointsOfAnEntityNameListsNothingAndIsStillReady()
    {
        var (app, admin, url, _) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        var collection = QdrantFixture.NewPrefix() + SampleCollections.Empty;
        await qdrant.SeedAsync([SampleCollections.CreateEmpty(collection)]);

        var registered = await RegisterOkAsync(admin, url, collection, Mapping("name", "country"));

        var lookup = await ReadyEntityLookupAsync(admin, registered.Id);
        Assert.Empty((await ValuesAsync(admin, lookup)).Values);
    }

    [Fact] // AC-9
    public async Task MoreEntitiesThanTheCeilingKeepsTheFirstOnesAndMarksTheSliceTruncated()
    {
        var (app, admin, url, collection) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        app.Services.GetRequiredService<IOptions<LookupOptions>>().Value.MaxValuesPerLookup = 3;

        var registered = await RegisterOkAsync(admin, url, collection, Mapping("name", Attributes));

        var lookup = await ReadyEntityLookupAsync(admin, registered.Id);
        Assert.True(lookup.Slices.Single().Truncated);
        Assert.Equal(["Acme", "Globex", "Initech"], Names(await ValuesAsync(admin, lookup)));
    }
}
