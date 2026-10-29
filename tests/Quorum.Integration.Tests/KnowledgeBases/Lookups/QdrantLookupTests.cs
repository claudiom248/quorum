using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Features.Lookups;
using Quorum.Modules.KnowledgeBases.Features.Search;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.Modules.KnowledgeBases.Storage;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBases.Lookups;

/// <summary>Computing the lookups of a Qdrant knowledge base: units, order, ceiling, slices and the index they need (spec 007 D-2, D-3, D-6, D-8).</summary>
public sealed class QdrantLookupTests(PostgresFixture postgres, QdrantFixture qdrant)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly object[] ContractFields =
    [
        new { name = "supplier", type = "Text" },
        new { name = "department", type = "Text" },
        new { name = "nationality", type = "Text" },
        new { name = "audience", type = "TextList" },
    ];

    private static readonly string[] MappedPaths = ["supplier", "department", "nationality", "audience"];

    private static object Mapping(object? countedPoint, bool documentId, params string[] listable) => new
    {
        roles = new { text = "text", documentId = documentId ? "document_id" : null, title = "title" },
        fields = MappedPaths
            .Select(name => new { path = name, name, listable = listable.Contains(name) })
            .ToArray(),
        countedPoint,
    };

    private static readonly object FirstChunk = new { path = "is_first_chunk", value = true };

    private sealed record LookupBody(Guid Id, string Name, string Kind, string? Field, string? Unit, List<SliceBody> Slices);

    private sealed record SliceBody(string Hash, string Status, bool Stale, int ValueCount, bool Truncated, string? Error, bool Running);

    private async Task<(QuorumApp App, HttpClient Admin, string Url)> StartAsync()
    {
        var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        // The real computer: the other tests of the lookups script a fake one.
        var computer = new LookupComputer(app.Services.GetRequiredService<StoreAdapters>());
        app.LookupComputer.Handler = computer.ComputeAsync;
        return (app, admin, (await qdrant.GetUrlAsync()).ToString());
    }

    private async Task<string> CollectionAsync() => await qdrant.SeedSharedAsync(SampleCollections.CreateLookups);

    private static async Task<KnowledgeBaseApi.RegisteredBody> RegisterAsync(HttpClient admin, string url, string collection, object mapping)
    {
        using var response = await KnowledgeBaseApi.RegisterAsync(admin, url, collection, mapping: mapping, createFields: ContractFields);
        if (response.StatusCode != HttpStatusCode.Created) throw new Xunit.Sdk.XunitException(await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<KnowledgeBaseApi.RegisteredBody>(Ct))!;
    }

    private static async Task<List<LookupBody>> LookupsAsync(HttpClient admin, Guid knowledgeBaseId) =>
        (await admin.GetFromJsonAsync<List<LookupBody>>(LookupApi.KnowledgeBaseLookups(knowledgeBaseId), Ct))!;

    private static async Task<LookupBody> LookupOfAsync(HttpClient admin, Guid knowledgeBaseId, string field, string status = "Ready")
    {
        var lookups = await LookupApi.UntilAsync(
            () => LookupsAsync(admin, knowledgeBaseId),
            list => list.Any(l => l.Field == field && l.Slices.All(s => s.Status == status && !s.Running)));
        return lookups.Single(l => l.Field == field);
    }

    private static async Task<List<(string Value, int? Count)>> ValuesAsync(HttpClient admin, LookupBody lookup, string? slice = null)
    {
        var values = await LookupApi.ReadValuesOkAsync(admin, lookup.Id, slice is null ? "limit=200" : $"slice={slice}&limit=200");
        return [.. values.Values.Select(v => (v.Value, v.Count))];
    }

    [Fact] // AC-1
    public async Task MarkingAFieldListableCreatesItsLookupAndComputesItInTheBackground()
    {
        var (app, admin, url) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        var registered = await RegisterAsync(admin, url, await CollectionAsync(), Mapping(FirstChunk, documentId: true, "supplier"));

        var lookups = await LookupsAsync(admin, registered.Id);
        var created = Assert.Single(lookups);
        Assert.Equal(("Computed", "supplier"), (created.Kind, created.Field));
        Assert.Single(created.Slices);
        var ready = await LookupOfAsync(admin, registered.Id, "supplier");
        Assert.Equal(3, ready.Slices.Single().ValueCount);
    }

    [Theory] // AC-4
    [InlineData("is_first_chunk", true)]
    [InlineData("chunk_index", 0)]
    public async Task WithACountedPointOnlyThoseRecordsAreCountedAndTheUnitIsDocuments(string path, object value)
    {
        var (app, admin, url) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        var registered = await RegisterAsync(admin, url, await CollectionAsync(), Mapping(new { path, value }, documentId: false, "supplier"));

        var lookup = await LookupOfAsync(admin, registered.Id, "supplier");
        Assert.Equal("documents", lookup.Unit);
        Assert.Equal([("Acme", 3), ("Globex", 2), ("Initech", 1)], (await ValuesAsync(admin, lookup)).Select(v => (v.Value, v.Count!.Value)));
    }

    [Fact] // AC-4
    public async Task WithoutACountedPointButWithADocumentIdTheDistinctDocumentsAreCounted()
    {
        var (app, admin, url) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        var registered = await RegisterAsync(admin, url, await CollectionAsync(), Mapping(countedPoint: null, documentId: true, "supplier"));

        var lookup = await LookupOfAsync(admin, registered.Id, "supplier");
        Assert.Equal("documents", lookup.Unit);
        Assert.Equal([("Acme", 3), ("Globex", 2), ("Initech", 1)], (await ValuesAsync(admin, lookup)).Select(v => (v.Value, v.Count!.Value)));
    }

    [Fact] // AC-4
    public async Task WithNeitherTheChunksAreCountedAndTheUnitSaysSo()
    {
        var (app, admin, url) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        var registered = await RegisterAsync(admin, url, await CollectionAsync(), Mapping(countedPoint: null, documentId: false, "supplier"));

        var lookup = await LookupOfAsync(admin, registered.Id, "supplier");
        Assert.Equal("chunks", lookup.Unit);
        Assert.Equal([("Acme", 7), ("Globex", 3), ("Initech", 1)], (await ValuesAsync(admin, lookup)).Select(v => (v.Value, v.Count!.Value)));
    }

    [Fact] // AC-5
    public async Task ValuesAreThoseOfThePayloadOrderedByCountThenValueAndAListFieldContributesEachOfItsValues()
    {
        var (app, admin, url) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        var registered = await RegisterAsync(admin, url, await CollectionAsync(), Mapping(FirstChunk, documentId: true, "department", "audience", "nationality"));

        var department = await LookupOfAsync(admin, registered.Id, "department");
        var audience = await LookupOfAsync(admin, registered.Id, "audience");
        var nationality = await LookupOfAsync(admin, registered.Id, "nationality");
        Assert.Equal([("facilities", 2), ("it", 2), ("finance", 1), ("legal", 1)], (await ValuesAsync(admin, department)).Select(v => (v.Value, v.Count!.Value)));
        Assert.Equal([("managers", 3), ("all", 2), ("it", 2)], (await ValuesAsync(admin, audience)).Select(v => (v.Value, v.Count!.Value)));
        Assert.Equal([("IT", 3), ("US", 2), ("DE", 1)], (await ValuesAsync(admin, nationality)).Select(v => (v.Value, v.Count!.Value)));
    }

    [Fact] // AC-6
    public async Task MoreValuesThanTheCeilingKeepsTheOnesWithTheMostDocumentsAndMarksTheSliceTruncated()
    {
        var (app, admin, url) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        app.Services.GetRequiredService<IOptions<LookupOptions>>().Value.MaxValuesPerLookup = 2;

        var registered = await RegisterAsync(admin, url, await CollectionAsync(), Mapping(FirstChunk, documentId: true, "supplier"));

        var lookup = await LookupOfAsync(admin, registered.Id, "supplier");
        Assert.True(lookup.Slices.Single().Truncated);
        Assert.Equal([("Acme", 3), ("Globex", 2)], (await ValuesAsync(admin, lookup)).Select(v => (v.Value, v.Count!.Value)));
    }

    [Fact] // AC-7
    public async Task AFieldWithoutAnIndexFailsTheLookupNamingItAndAnIndexMakesItReady()
    {
        var (app, admin, url) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        var collection = QdrantFixture.NewPrefix() + SampleCollections.LookupsUnindexed;
        await qdrant.SeedAsync([SampleCollections.CreateLookupsUnindexed(collection)]);

        var registered = await RegisterAsync(admin, url, collection, Mapping(FirstChunk, documentId: true, "supplier"));

        var failed = await LookupOfAsync(admin, registered.Id, "supplier", "Failed");
        var error = failed.Slices.Single().Error!;
        Assert.Contains("supplier", error, StringComparison.Ordinal);
        Assert.Contains("is_first_chunk", error, StringComparison.Ordinal);

        using var rest = new HttpClient { BaseAddress = await qdrant.GetRestUrlAsync() };
        foreach (var (field, schema) in new[] { ("supplier", "keyword"), ("is_first_chunk", "bool") })
        {
            using var created = await rest.PutAsJsonAsync($"collections/{collection}/index?wait=true", new { field_name = field, field_schema = schema }, Ct);
            created.EnsureSuccessStatusCode();
        }

        using var refresh = await LookupApi.PostAsync(admin, LookupApi.RefreshKnowledgeBase(registered.Id));
        Assert.Equal(HttpStatusCode.Accepted, refresh.StatusCode);
        var ready = await LookupOfAsync(admin, registered.Id, "supplier");
        Assert.Equal(3, ready.Slices.Single().ValueCount);
    }

    [Fact] // AC-22
    public async Task ASliceListsOnlyTheValuesOfTheDocumentsItsFiltersLeaveInAndTheWholeListIsUnchanged()
    {
        var (app, admin, url) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        var registered = await RegisterAsync(admin, url, await CollectionAsync(), Mapping(FirstChunk, documentId: true, "supplier"));
        var whole = await LookupOfAsync(admin, registered.Id, "supplier");

        var hash = await AddSliceAsync(app, whole.Id, registered.Id, "department", "facilities");
        await app.Services.GetRequiredService<LookupRefresher>().RunDueAsync(Ct);
        await LookupApi.UntilAsync(() => LookupsAsync(admin, registered.Id), l => l.Single().Slices.All(s => s.Status == "Ready" && !s.Running));

        Assert.Equal([("Acme", 2)], await ValuesAsync(admin, whole, hash));
        Assert.Equal([("Acme", 3), ("Globex", 2), ("Initech", 1)], (await ValuesAsync(admin, whole)).Select(v => (v.Value, v.Count!.Value)));
    }

    [Fact] // AC-22
    public async Task ASliceWhoseFilterUsesAFieldTheKnowledgeBaseDoesNotMapFails()
    {
        var (app, admin, url) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        var registered = await RegisterAsync(admin, url, await CollectionAsync(), Mapping(FirstChunk, documentId: true, "supplier"));
        var whole = await LookupOfAsync(admin, registered.Id, "supplier");

        await AddSliceAsync(app, whole.Id, registered.Id, field: null, "x");
        await app.Services.GetRequiredService<LookupRefresher>().RunDueAsync(Ct);

        var slices = (await LookupApi.UntilAsync(() => LookupsAsync(admin, registered.Id), l => l.Single().Slices.Any(s => s.Status == "Failed"))).Single().Slices;
        Assert.Contains("does not map a field", slices.Single(s => s.Status == "Failed").Error, StringComparison.Ordinal);
    }

    /// <summary>A slice the way a lookup tool would declare it: the fixed filter is canonical, so its hash is the one a tool finds.</summary>
    private static async Task<string> AddSliceAsync(QuorumApp app, Guid lookupId, Guid knowledgeBaseId, string? field, string value)
    {
        var tenantId = await TenantScope.TenantIdAsync(app);
        await using var scope = TenantScope.Open(app, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
        var fieldId = field is null
            ? Guid.NewGuid()
            : (await db.KnowledgeBases.SingleAsync(k => k.Id == knowledgeBaseId, Ct)).Mapping.Fields.Single(f => f.Path == field).DictionaryFieldId;
        var key = LookupSliceKey.From([new AnyOf(new FilterField(fieldId, field ?? "unmapped", DictionaryFieldType.Text), [value])]);
        db.LookupSlices.Add(LookupSlice.Create(tenantId, lookupId, key.Hash, key.Filters, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(Ct);
        return key.Hash;
    }

    [Fact] // AC-1
    public async Task AFieldThatStopsBeingListableTakesItsLookupAndValuesWithIt()
    {
        var (app, admin, url) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        var collection = await CollectionAsync();
        var registered = await RegisterAsync(admin, url, collection, Mapping(FirstChunk, documentId: true, "supplier", "department"));
        await LookupOfAsync(admin, registered.Id, "supplier");
        await LookupOfAsync(admin, registered.Id, "department");

        using var update = await admin.PutAsJsonAsync(
            KnowledgeBaseApi.One(registered.Id),
            KnowledgeBaseApi.Body(url, collection, mapping: Mapping(FirstChunk, documentId: true, "department"), createFields: ContractFields),
            Ct);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var remaining = Assert.Single(await LookupsAsync(admin, registered.Id));
        Assert.Equal("department", remaining.Field);
    }

    [Fact] // AC-4
    public async Task ChangingTheCountedPointRecomputesTheLookupUnderTheNewUnit()
    {
        var (app, admin, url) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        var collection = await CollectionAsync();
        var registered = await RegisterAsync(admin, url, collection, Mapping(countedPoint: null, documentId: false, "supplier"));
        var chunks = await LookupOfAsync(admin, registered.Id, "supplier");
        Assert.Equal("chunks", chunks.Unit);

        using var update = await admin.PutAsJsonAsync(
            KnowledgeBaseApi.One(registered.Id),
            KnowledgeBaseApi.Body(url, collection, mapping: Mapping(FirstChunk, documentId: false, "supplier"), createFields: ContractFields),
            Ct);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var values = await LookupApi.UntilAsync(() => ValuesAsync(admin, chunks), v => v.Count > 0 && v[0].Count == 3);
        Assert.Equal([("Acme", 3), ("Globex", 2), ("Initech", 1)], values.Select(v => (v.Value, v.Count!.Value)));
        Assert.Equal("documents", Assert.Single(await LookupsAsync(admin, registered.Id)).Unit);
    }
}
