using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.KnowledgeBases.Features.Lookups;
using Quorum.Modules.KnowledgeBases.Storage;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBases.Lookups;

/// <summary>
/// The lookups of a PgVector knowledge base, computed in the background and read through the API (spec 008 Amendment 2: AC-44 to
/// AC-48), on the handbook and the employees of the company (<see cref="PeopleSampleData"/>).
/// </summary>
public sealed class PgVectorLookupTests(PostgresFixture postgres, PgVectorFixture pgvector)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly object[] HandbookFields =
    [
        new { name = "department", type = "Text" },
        new { name = "audience", type = "TextList" },
        new { name = "country", type = "Text" },
        new { name = "status", type = "Text" },
    ];

    private static readonly object[] JsonFields =
    [
        new { name = "department", type = "Text" },
        new { name = "audience", type = "TextList" },
        new { name = "country", type = "Text" },
        new { name = "status", type = "Text" },
    ];

    private static readonly string[] FieldNames = ["department", "audience", "country", "status"];

    private static readonly string[] EntityFieldNames = ["department", "country", "hired_on"];

    private static readonly object FirstChunk = new { path = "is_first_chunk", value = true };

    private sealed record LookupBody(Guid Id, string Name, string Kind, string? Field, string? Unit, List<SliceBody> Slices);

    private sealed record SliceBody(string Hash, string Status, bool Stale, int ValueCount, bool Truncated, string? Error, bool Running);

    private static object Mapping(string prefix, object? countedPoint, bool documentId, string text, params string[] listable) => new
    {
        roles = new { text, documentId = documentId ? prefix + "document_id" : null, title = prefix + "title" },
        fields = FieldNames
            .Select(name => new { path = prefix + name, name, listable = listable.Contains(name) })
            .ToArray(),
        countedPoint,
    };

    private async Task<(QuorumApp App, HttpClient Admin)> StartAsync()
    {
        var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        // The real computer: the other tests of the lookups script a fake one.
        app.LookupComputer.Handler = new LookupComputer(app.Services.GetRequiredService<StoreAdapters>()).ComputeAsync;
        return (app, admin);
    }

    private async Task<Guid> RegisterAsync(HttpClient admin, string relation, object mapping, object[] createFields, string name = "handbook", string? schema = null)
    {
        var body = new
        {
            name,
            description = "The policies of the company.",
            kind = "PgVector",
            connection = PgVectorApi.Fields(await pgvector.GetEndpointAsync(), schema ?? await pgvector.GetSharedSchemaAsync(), relation),
            embeddingModel = KnowledgeBaseApi.EmbeddingModel,
            queryPrefix = (string?)null,
            mapping,
            scope = new { kind = "Tenant" },
            createFields,
        };
        using var response = await admin.PostAsJsonAsync(KnowledgeBaseApi.All, body, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<KnowledgeBaseApi.RegisteredBody>(Ct))!.Id;
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

    private static async Task<string[]> ValuesAsync(HttpClient admin, LookupBody lookup)
    {
        var values = await LookupApi.ReadValuesOkAsync(admin, lookup.Id, "limit=200");
        return [.. values.Values.Select(v => $"{v.Value}={v.Count}")];
    }

    // The documents, not the chunks, that carry each value: what the handbook's counted point says.
    private static string[] Expected(Func<PeopleSampleData.Policy, IEnumerable<string?>> values) =>
    [
        .. PeopleSampleData.Policies
            .SelectMany(policy => values(policy).OfType<string>())
            .GroupBy(value => value)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key}={group.Count()}"),
    ];

    [Fact] // AC-44, AC-45
    public async Task EveryListableColumnOfTheHandbookIsListedWithTheDocumentsOfEachValue()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        var kb = await RegisterAsync(admin, SampleRelations.Handbook, Mapping(string.Empty, FirstChunk, documentId: true, "text", "department", "audience", "country", "status"), HandbookFields);

        Assert.Equal(Expected(p => [p.Department]), await ValuesAsync(admin, await LookupOfAsync(admin, kb, "department")));
        Assert.Equal(Expected(p => p.Audience), await ValuesAsync(admin, await LookupOfAsync(admin, kb, "audience")));
        Assert.Equal(Expected(p => [p.Country]), await ValuesAsync(admin, await LookupOfAsync(admin, kb, "country")));
        var status = await LookupOfAsync(admin, kb, "status");
        Assert.Equal(Expected(p => [p.Status]), await ValuesAsync(admin, status));
        Assert.Equal("documents", status.Unit);
    }

    [Fact] // AC-45
    public async Task WithoutACountedPointTheDocumentIdStillCountsDocumentsAndWithNeitherTheLookupCountsChunks()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        var documents = await RegisterAsync(admin, SampleRelations.Handbook, Mapping(string.Empty, countedPoint: null, documentId: true, "text", "department"), HandbookFields, "by-document");
        var chunks = await RegisterAsync(admin, SampleRelations.Handbook, Mapping(string.Empty, countedPoint: null, documentId: false, "text", "department"), HandbookFields, "by-chunk");

        var byDocument = await LookupOfAsync(admin, documents, "department");
        var byChunk = await LookupOfAsync(admin, chunks, "department");

        Assert.Equal(Expected(p => [p.Department]), await ValuesAsync(admin, byDocument));
        Assert.Equal("documents", byDocument.Unit);
        Assert.Equal("chunks", byChunk.Unit);
        Assert.Equal(PeopleSampleData.Policies.Sum(p => p.Sections.Length), (await LookupApi.ReadValuesOkAsync(admin, byChunk.Id, "limit=200")).Values.Sum(v => v.Count));
    }

    [Fact] // AC-44, AC-45
    public async Task TheKeysOfAJsonbColumnAreListedTheSameWayIncludingTheListInsideIt()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        var counted = new { path = "cmetadata.is_first_chunk", value = true };

        var kb = await RegisterAsync(
            admin,
            SampleRelations.HandbookJson,
            Mapping("cmetadata.", counted, documentId: true, "document", "department", "audience", "country", "status"),
            JsonFields,
            "handbook-json");

        Assert.Equal(Expected(p => [p.Department]), await ValuesAsync(admin, await LookupOfAsync(admin, kb, "department")));
        Assert.Equal(Expected(p => p.Audience), await ValuesAsync(admin, await LookupOfAsync(admin, kb, "audience")));
        Assert.Equal(Expected(p => [p.Country]), await ValuesAsync(admin, await LookupOfAsync(admin, kb, "country")));
    }

    [Fact] // AC-46
    public async Task TheEmployeesAreListedAsEntitiesWithTheirAttributesAndCanBeOrderedByTheDateOfHiring()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        var mapping = new
        {
            roles = new { text = "text" },
            fields = EntityFieldNames.Select(name => new { path = name, name, entityAttribute = true }).ToArray(),
            entityName = "name",
        };
        object[] fields = [new { name = "department", type = "Text" }, new { name = "country", type = "Text" }, new { name = "hired_on", type = "Date" }];

        var kb = await RegisterAsync(admin, SampleRelations.Employees, mapping, fields, "people");

        var lookups = await LookupApi.UntilAsync(
            () => LookupsAsync(admin, kb),
            list => list.Any(l => l.Kind == "Entity" && l.Slices.All(s => s.Status == "Ready" && !s.Running)));
        var lookup = lookups.Single(l => l.Kind == "Entity");
        Assert.Equal(PeopleSampleData.Employees.Count, lookup.Slices.Single().ValueCount);
        var byDate = await LookupApi.ReadValuesOkAsync(admin, lookup.Id, "sort=hired_on&limit=200");
        Assert.Equal(PeopleSampleData.Employees.OrderBy(e => e.HiredOn, StringComparer.Ordinal).First().Name, byDate.Values[0].Value);
        var first = byDate.Values[0].Attributes!.Value;
        Assert.Equal(["country", "department", "hired_on"], first.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        var newest = await LookupApi.ReadValuesOkAsync(admin, lookup.Id, "sort=-hired_on&limit=200");
        Assert.Equal(PeopleSampleData.Employees.OrderByDescending(e => e.HiredOn, StringComparer.Ordinal).First().Name, newest.Values[0].Value);
    }

    [Fact] // AC-48
    public async Task AColumnThatIsGoneIsNamedWithoutTheDatabasesWordsAndTheLastListIsKept()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        var schema = await pgvector.NewSchemaAsync();
        var kb = await RegisterAsync(admin, SampleRelations.Handbook, Mapping(string.Empty, FirstChunk, documentId: true, "text", "department"), HandbookFields, schema: schema);
        await LookupOfAsync(admin, kb, "department");
        await pgvector.ExecuteAsync($"ALTER TABLE {schema}.{SampleRelations.Handbook} DROP COLUMN department CASCADE");

        using var refresh = await LookupApi.PostAsync(admin, LookupApi.RefreshKnowledgeBase(kb));
        var kept = await LookupApi.UntilAsync(
            () => LookupsAsync(admin, kb),
            list => list.Any(l => l.Field == "department" && l.Slices.Any(s => s.Error is not null)));

        var slice = kept.Single(l => l.Field == "department").Slices.Single();
        Assert.Contains("'department'", slice.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT", slice.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(slice.Stale);
        Assert.True(slice.ValueCount > 0);
    }

    [Fact] // AC-48
    public async Task AListableColumnThatTheRelationDoesNotHaveFailsItsFirstComputation()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        var mapping = new
        {
            roles = new { text = "text" },
            fields = new[] { new { path = "ghost", name = "ghost", listable = true } },
        };

        var kb = await RegisterAsync(admin, SampleRelations.Handbook, mapping, [new { name = "ghost", type = "Text" }]);

        var failed = await LookupOfAsync(admin, kb, "ghost", "Failed");
        var error = failed.Slices.Single().Error!;
        Assert.Contains("'ghost'", error, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT", error, StringComparison.OrdinalIgnoreCase);
    }
}
