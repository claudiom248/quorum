using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.Tools;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBases.Search;

/// <summary>The administrator's test search of a knowledge base (D-22), which is also the plainest way to watch the search work against a real Qdrant.</summary>
public sealed class SearchEndpointTests(PostgresFixture postgres, QdrantFixture qdrant)
{
    private const string Secret = "s3cret-qdrant-key-5b1d";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The text of a sample chunk. Searched for as it is, its embedding is the chunk's own: the chunk comes first, with a score of 1.</summary>
    private static string Text(ulong chunk) => SampleCollections.CreateFlat().Points.Single(p => p.Id == chunk).EmbeddingText;

    private async Task<(QuorumApp App, HttpClient Admin, Guid Id)> StartWithFlatAsync(string? queryPrefix = null)
    {
        var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var collection = await qdrant.SeedSharedAsync(SampleCollections.CreateFlat);
        var url = (await qdrant.GetUrlAsync()).ToString();

        using var response = await KnowledgeBaseApi.RegisterAsync(admin, url, collection, queryPrefix: queryPrefix);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var registered = await response.Content.ReadFromJsonAsync<KnowledgeBaseApi.RegisteredBody>(Ct);
        return (app, admin, registered!.Id);
    }

    [Fact] // AC-47
    public async Task ATestSearchReturnsScoredResultsWithTheirSourcesAndWritesNoAuditEntry()
    {
        var (app, admin, id) = await StartWithFlatAsync();
        await using var _ = app;
        using var __ = admin;

        var found = await SearchApi.SearchOkAsync(admin, id, Text(4));

        Assert.Equal(5, found.Results.Count);
        var best = found.Results[0];
        Assert.Equal(Text(4), best.Text);
        Assert.InRange(best.Score, 0.999, 1.001);
        Assert.Equal(
            new SearchApi.SourceBody("policies", "security", "Password and access policy", "1", "https://intranet.example.test/policies/security"),
            best.Source);
        Assert.Equal(found.Results.Select(r => r.Score).OrderDescending(), found.Results.Select(r => r.Score));
        Assert.Empty(found.Notes);
        Assert.Equal(0, await ToolFixtures.ScalarAsync<long>(app, "SELECT COUNT(*) FROM agents.tool_audit_entries"));
    }

    [Fact] // AC-24
    public async Task TheQueryIsEmbeddedWithTheModelAndThePrefixOfTheKnowledgeBase()
    {
        var (app, admin, id) = await StartWithFlatAsync(queryPrefix: "search_query: ");
        await using var _ = app;
        using var __ = admin;
        var before = app.ModelServer.EmbeddingRequests.Count;

        await SearchApi.SearchOkAsync(admin, id, "annual leave");

        var request = Assert.Single(app.ModelServer.EmbeddingRequests.Skip(before));
        Assert.Equal((KnowledgeBaseApi.EmbeddingModel, "search_query: annual leave"), (request.Model, request.Input));
    }

    [Theory] // AC-29
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(10)]
    public async Task TheLimitIsHonoured(int limit)
    {
        var (app, admin, id) = await StartWithFlatAsync();
        await using var _ = app;
        using var __ = admin;

        var found = await SearchApi.SearchOkAsync(admin, id, Text(1), limit: limit);

        Assert.Equal(limit, found.Results.Count);
    }

    [Theory]
    [InlineData(null, 5)]
    [InlineData(0, 400)]
    [InlineData(21, 400)]
    [InlineData(-1, 400)]
    public async Task TheLimitIsFiveByDefaultAndTwentyAtMost(int? limit, int expected)
    {
        var (app, admin, id) = await StartWithFlatAsync();
        await using var _ = app;
        using var __ = admin;

        using var response = await SearchApi.PostAsync(admin, id, Text(1), limit: limit);

        if (expected == 400)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("knowledge_base.invalid_search", await SearchApi.ProblemCodeAsync(response));
        }
        else
        {
            var body = await response.Content.ReadFromJsonAsync<SearchApi.SearchBody>(Ct);
            Assert.Equal(expected, body!.Results.Count);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AQueryIsRequired(string? query)
    {
        var (app, admin, id) = await StartWithFlatAsync();
        await using var _ = app;
        using var __ = admin;

        using var response = await SearchApi.PostAsync(admin, id, query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("knowledge_base.invalid_search", await SearchApi.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ASearchOfAnUnknownKnowledgeBaseIsNotFound()
    {
        var (app, admin, _) = await StartWithFlatAsync();
        await using var _1 = app;
        using var _2 = admin;

        using var response = await SearchApi.PostAsync(admin, Guid.NewGuid(), "leave");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public static TheoryData<string, string, string[]> Filters => new()
    {
        { "text", """{"department": ["finance"]}""", ["travel"] },
        { "text, several values", """{"department": ["hr", "legal"]}""", ["leave", "nda", "onboarding", "remote"] },
        { "number range", """{"year_min": 2024, "year_max": 2024}""", ["leave", "security"] },
        { "number, only a minimum", """{"year_min": 2025}""", ["onboarding"] },
        { "date range", """{"published_on_from": "2023-01-01", "published_on_to": "2023-12-31"}""", ["office", "remote"] },
        { "date range ending on a day that has chunks", """{"published_on_from": "2024-03-10", "published_on_to": "2024-03-10"}""", ["security"] },
        { "boolean", """{"confidential": true}""", ["nda"] },
        { "boolean false and a text", """{"confidential": false, "department": ["it"]}""", ["security"] },
        { "text and a number", """{"department": ["it"], "year_min": 2024}""", ["security"] },
        { "a null and an empty list are no filter", """{"department": [], "year_min": null, "confidential": true}""", ["nda"] },
    };

    [Theory] // AC-25
    [MemberData(nameof(Filters))]
    public async Task OnlyChunksThatMatchEveryFilterComeBack(string description, string filters, string[] documents)
    {
        Assert.NotEmpty(description);
        var (app, admin, id) = await StartWithFlatAsync();
        await using var _ = app;
        using var __ = admin;

        var found = await SearchApi.SearchOkAsync(admin, id, Text(1), JsonDocument.Parse(filters).RootElement, limit: 20);

        Assert.Equal(documents, found.Results.Select(r => r.Source.DocumentId).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact] // AC-25
    public async Task FiltersWorkOnACollectionWithoutPayloadIndexes()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var collection = await qdrant.SeedSharedAsync(SampleCollections.CreateUnindexed);
        var registered = await KnowledgeBaseApi.RegisterOkAsync(admin, (await qdrant.GetUrlAsync()).ToString(), collection);

        var found = await SearchApi.SearchOkAsync(
            admin,
            registered.Id,
            Text(1),
            JsonDocument.Parse("""{"department": ["hr"], "year_min": 2023, "published_on_from": "2024-01-01"}""").RootElement,
            limit: 20);

        Assert.Equal(["leave", "onboarding"], found.Results.Select(r => r.Source.DocumentId).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(3, found.Results.Count);
    }

    [Fact] // AC-25
    public async Task AListFieldMatchesChunksThatContainAnyOfTheValues()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var collection = await qdrant.SeedSharedAsync(SampleCollections.CreateTags);
        var registered = await KnowledgeBaseApi.RegisterOkAsync(
            admin,
            (await qdrant.GetUrlAsync()).ToString(),
            collection,
            mapping: SearchApi.TagsMapping,
            createFields: SearchApi.TagsCreateFields);

        var found = await SearchApi.SearchOkAsync(admin, registered.Id, Text(1), JsonDocument.Parse("""{"tags": ["travel", "onboarding"]}""").RootElement, limit: 20);

        Assert.Equal(["Onboarding checklist", "Travel expense rules"], found.Results.Select(r => r.Source.Title).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(3, found.Results.Count);
    }

    [Fact] // AC-25, AC-7
    public async Task FiltersAndSourcesFollowTheMappedPathsOfANestedPayload()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var collection = await qdrant.SeedSharedAsync(SampleCollections.CreateNested);
        var registered = await KnowledgeBaseApi.RegisterOkAsync(admin, (await qdrant.GetUrlAsync()).ToString(), collection, mapping: SearchApi.NestedMapping);

        var found = await SearchApi.SearchOkAsync(admin, registered.Id, Text(6), JsonDocument.Parse("""{"department": ["finance"], "year_max": 2022}""").RootElement, limit: 20);

        Assert.Equal(2, found.Results.Count);
        var best = found.Results[0];
        Assert.Equal(Text(6), best.Text);
        Assert.Equal(new SearchApi.SourceBody("policies", "travel", "Travel expense rules", "1", "https://intranet.example.test/policies/travel"), best.Source);
    }

    [Fact] // D-18: a chosen named vector is the one searched
    public async Task ANamedVectorIsSearchedWhenTheKnowledgeBaseChoseIt()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var collection = await qdrant.SeedSharedAsync(SampleCollections.CreateNamedVectors);
        var registered = await KnowledgeBaseApi.RegisterOkAsync(admin, (await qdrant.GetUrlAsync()).ToString(), collection, vectorName: SampleCollections.DenseVectorName);

        var found = await SearchApi.SearchOkAsync(admin, registered.Id, Text(9), limit: 1);

        Assert.Equal(Text(9), Assert.Single(found.Results).Text);
    }

    [Fact] // AC-30
    public async Task AnEmptyCollectionAnswersWithoutResults()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var collection = await qdrant.SeedSharedAsync(SampleCollections.CreateEmpty);
        var registered = await KnowledgeBaseApi.RegisterOkAsync(admin, (await qdrant.GetUrlAsync()).ToString(), collection, mapping: KnowledgeBaseApi.TextOnly(), createFields: []);

        var found = await SearchApi.SearchOkAsync(admin, registered.Id, "anything");

        Assert.Empty(found.Results);
        Assert.Equal("Available", (await KnowledgeBaseApi.GetAsync(admin, registered.Id)).Status);
    }

    [Fact] // AC-31
    public async Task AFilterThatDoesNotFitIsRefusedBeforeAnythingIsEmbeddedOrSent()
    {
        var (app, admin, id) = await StartWithFlatAsync();
        await using var _ = app;
        using var __ = admin;
        var before = app.ModelServer.EmbeddingRequests.Count;

        using var response = await SearchApi.PostAsync(admin, id, Text(1), JsonDocument.Parse("""{"year_min": "recent"}""").RootElement);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("knowledge_base.invalid_filter", await SearchApi.ProblemCodeAsync(response));
        Assert.Equal(before, app.ModelServer.EmbeddingRequests.Count);
    }

    [Fact] // AC-28
    public async Task AFilterOnAFieldTheKnowledgeBaseDidNotMapIsAnErrorWhenItIsTheOnlyKnowledgeBase()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var collection = await qdrant.SeedSharedAsync(SampleCollections.CreateFlat);
        var url = (await qdrant.GetUrlAsync()).ToString();
        await KnowledgeBaseApi.RegisterOkAsync(admin, url, collection, name: "complete");
        var bare = await KnowledgeBaseApi.RegisterOkAsync(admin, url, collection, name: "bare", mapping: KnowledgeBaseApi.TextOnly(), createFields: []);

        using var response = await SearchApi.PostAsync(admin, bare.Id, Text(1), JsonDocument.Parse("""{"year_min": 2023}""").RootElement);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("knowledge_base.filter_unsupported", await SearchApi.ProblemCodeAsync(response));
    }

    [Fact] // AC-33, AC-35
    public async Task ADownEmbeddingEndpointFailsTheSearchMarksTheKnowledgeBaseUnavailableAndALaterSuccessBringsItBack()
    {
        var (app, admin, id) = await StartWithFlatAsync();
        await using var _ = app;
        using var __ = admin;

        app.ModelServer.FailEmbeddings(QuorumApp.ModelBaseUrl, 503);
        using var failed = await SearchApi.PostAsync(admin, id, Text(1));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, failed.StatusCode);
        Assert.Equal("knowledge_base.unavailable", await SearchApi.ProblemCodeAsync(failed));
        var down = await KnowledgeBaseApi.GetAsync(admin, id);
        Assert.Equal("Unavailable", down.Status);
        Assert.False(string.IsNullOrWhiteSpace(down.Warning));

        app.ModelServer.RecoverEmbeddings(QuorumApp.ModelBaseUrl);
        var found = await SearchApi.SearchOkAsync(admin, id, Text(1));

        Assert.NotEmpty(found.Results);
        var back = await KnowledgeBaseApi.GetAsync(admin, id);
        Assert.Equal("Available", back.Status);
        Assert.Null(back.Warning);
    }

    [Fact] // AC-33
    public async Task AnUnreachableStoreFailsTheSearchWithoutRevealingItsAddress()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var registered = await KnowledgeBaseApi.RegisterOkAsync(admin, "http://[::1]:1", "docs", mapping: KnowledgeBaseApi.TextOnly(), createFields: []);
        Assert.Equal("Unavailable", registered.Status);

        using var response = await SearchApi.PostAsync(admin, registered.Id, "leave");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("knowledge_base.unavailable", body, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docs", body, StringComparison.Ordinal);
    }

    [Fact] // AC-33, AC-35
    public async Task AKnowledgeBaseRegisteredBeforeItsCollectionExistedBecomesAvailableOnceASearchWorks()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var collection = QdrantFixture.NewPrefix() + SampleCollections.Flat;
        var url = (await qdrant.GetUrlAsync()).ToString();
        var registered = await KnowledgeBaseApi.RegisterOkAsync(admin, url, collection);
        Assert.Equal("Unavailable", registered.Status);

        using var early = await SearchApi.PostAsync(admin, registered.Id, Text(1));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, early.StatusCode);

        await qdrant.SeedAsync([SampleCollections.CreateFlat(collection)]);
        var found = await SearchApi.SearchOkAsync(admin, registered.Id, Text(1));

        Assert.NotEmpty(found.Results);
        Assert.Equal("Available", (await KnowledgeBaseApi.GetAsync(admin, registered.Id)).Status);
    }

    [Fact] // AC-33
    public async Task VectorsOfAnotherSizeThanTheModelsFailTheSearchWithoutInternalDetail()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var collection = await qdrant.SeedSharedAsync(SampleCollections.CreateOtherVectorSize);
        var registered = await KnowledgeBaseApi.RegisterOkAsync(admin, (await qdrant.GetUrlAsync()).ToString(), collection, mapping: KnowledgeBaseApi.TextOnly(), createFields: []);
        Assert.Equal("Unavailable", registered.Status);

        using var response = await SearchApi.PostAsync(admin, registered.Id, "leave");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("knowledge_base.unavailable", await SearchApi.ProblemCodeAsync(response));
        Assert.Contains("same model", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact] // AC-46
    public async Task NeitherTheQueryNorTheChunksNorTheDocumentNamesNorTheKeyReachTheLogs()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var collection = await qdrant.SeedSharedAsync(SampleCollections.CreateFlat, Secret);
        var url = (await qdrant.GetUrlAsync(Secret)).ToString();
        var registered = await KnowledgeBaseApi.RegisterOkAsync(admin, url, collection, apiKey: Secret);
        var query = Text(5);

        await SearchApi.SearchOkAsync(admin, registered.Id, query, JsonDocument.Parse("""{"department": ["it"]}""").RootElement);
        app.ModelServer.FailEmbeddings(QuorumApp.ModelBaseUrl, 503);
        using var failed = await SearchApi.PostAsync(admin, registered.Id, query);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, failed.StatusCode);

        foreach (var secret in new[] { query, Text(4), "Password and access policy", "intranet.example.test", Secret, "wiped remotely" })
        {
            Assert.DoesNotContain(app.Logs.Messages, message => message.Contains(secret, StringComparison.Ordinal));
        }
    }
}
