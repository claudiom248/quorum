using System.Net;
using System.Net.Http.Json;
using Quorum.Integration.Tests.Fixtures;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBases;

public sealed class KnowledgeBaseLifecycleTests(PostgresFixture postgres, QdrantFixture qdrant)
{
    private const string Secret = "s3cret-qdrant-key-1c9d";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object UpdateBody(string url, string collection, string name = "policies", string description = "Company policies.", string? apiKey = null, object? mapping = null) =>
        new
        {
            name,
            description,
            kind = "Qdrant",
            connection = new { url, apiKey, collection, vectorName = (string?)null },
            embeddingModel = KnowledgeBaseApi.EmbeddingModel,
            queryPrefix = (string?)null,
            mapping = mapping ?? KnowledgeBaseApi.TextOnly(),
            scope = new { kind = "Tenant" },
            createFields = Array.Empty<object>(),
        };

    [Fact] // AC-17
    public async Task AnUnavailableKnowledgeBaseThatBecameReadyIsAvailableAfterVerifying()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var collection = QdrantFixture.NewPrefix() + SampleCollections.Flat;
        var url = (await qdrant.GetUrlAsync()).ToString();
        var registered = await KnowledgeBaseApi.RegisterOkAsync(admin, url, collection, mapping: KnowledgeBaseApi.TextOnly(), createFields: []);
        Assert.Equal("Unavailable", registered.Status);
        await qdrant.SeedAsync([SampleCollections.CreateFlat(collection)]);

        var verified = await KnowledgeBaseApi.VerifyOkAsync(admin, registered.Id);

        Assert.Equal("Available", verified.Status);
        Assert.Empty(verified.Warnings);
        var detail = await KnowledgeBaseApi.GetAsync(admin, registered.Id);
        Assert.Equal("Available", detail.Status);
        Assert.Null(detail.Warning);
        Assert.Equal("Available", Assert.Single(await KnowledgeBaseApi.ListAsync(admin)).Status);
    }

    [Fact] // D-8
    public async Task VerifyingAgainMarksAKnowledgeBaseUnavailableWhenTheCollectionIsGone()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var collection = QdrantFixture.NewPrefix() + SampleCollections.Flat;
        await qdrant.SeedAsync([SampleCollections.CreateFlat(collection)]);
        var url = await qdrant.GetUrlAsync();
        var registered = await KnowledgeBaseApi.RegisterOkAsync(admin, url.ToString(), collection, mapping: KnowledgeBaseApi.TextOnly(), createFields: []);
        Assert.Equal("Available", registered.Status);
        using var rest = new HttpClient { BaseAddress = await qdrant.GetRestUrlAsync() };
        using var deleted = await rest.DeleteAsync(new Uri($"/collections/{collection}", UriKind.Relative), Ct);
        deleted.EnsureSuccessStatusCode();

        var verified = await KnowledgeBaseApi.VerifyOkAsync(admin, registered.Id);

        Assert.Equal("Unavailable", verified.Status);
        Assert.Contains(verified.Warnings, w => w.Code == "knowledge_base.collection_not_found");
        Assert.NotNull((await KnowledgeBaseApi.GetAsync(admin, registered.Id)).Warning);
    }

    [Fact] // D-8
    public async Task VerifyingWarnsWhenAMappedPathIsNoLongerInThePayload()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var collection = QdrantFixture.NewPrefix() + SampleCollections.Flat;
        await qdrant.SeedAsync([SampleCollections.CreateFlat(collection)]);
        var url = (await qdrant.GetUrlAsync()).ToString();
        var registered = await KnowledgeBaseApi.RegisterOkAsync(
            admin,
            url,
            collection,
            mapping: new { roles = new { text = "text", title = "headline" }, fields = Array.Empty<object>() },
            createFields: []);

        var verified = await KnowledgeBaseApi.VerifyOkAsync(admin, registered.Id);

        var warning = Assert.Single(verified.Warnings);
        Assert.Equal("knowledge_base.mapped_path_missing", warning.Code);
        Assert.Equal("headline", warning.Path);
        Assert.Equal("Available", verified.Status);
    }

    [Fact]
    public async Task VerifyingAnUnknownKnowledgeBaseIsNotFound()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        using var response = await admin.PostAsync(KnowledgeBaseApi.Verify(Guid.NewGuid()), content: null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnUpdateChangesWhatWasRegisteredAndVerifiesAgain()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var prefix = QdrantFixture.NewPrefix();
        await qdrant.SeedAsync([SampleCollections.CreateFlat(prefix + SampleCollections.Flat)]);
        var url = (await qdrant.GetUrlAsync()).ToString();
        var registered = await KnowledgeBaseApi.RegisterOkAsync(admin, url, prefix + SampleCollections.Flat, mapping: KnowledgeBaseApi.TextOnly(), createFields: []);

        using var toMissing = await admin.PutAsJsonAsync(KnowledgeBaseApi.One(registered.Id), UpdateBody(url, prefix + "missing", "renamed", "Other description."), Ct);
        Assert.Equal(HttpStatusCode.OK, toMissing.StatusCode);
        var outcome = (await toMissing.Content.ReadFromJsonAsync<KnowledgeBaseApi.VerifiedBody>(Ct))!;
        var afterMove = await KnowledgeBaseApi.GetAsync(admin, registered.Id);

        Assert.Equal("Unavailable", outcome.Status);
        Assert.Contains(outcome.Warnings, w => w.Code == "knowledge_base.collection_not_found");
        Assert.Equal(("renamed", "Other description.", "Unavailable"), (afterMove.Name, afterMove.Description, afterMove.Status));
        Assert.Equal(prefix + "missing", afterMove.Connection!.Collection);

        using var back = await admin.PutAsJsonAsync(KnowledgeBaseApi.One(registered.Id), UpdateBody(url, prefix + SampleCollections.Flat, "renamed"), Ct);
        Assert.Equal("Available", (await back.Content.ReadFromJsonAsync<KnowledgeBaseApi.VerifiedBody>(Ct))!.Status);
    }

    [Fact] // AC-16
    public async Task AnUpdateWithoutAnApiKeyKeepsTheStoredOneAndANewKeyReplacesIt()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var collection = QdrantFixture.NewPrefix() + SampleCollections.Flat;
        await qdrant.SeedAsync([SampleCollections.CreateFlat(collection)], Secret);
        var url = (await qdrant.GetUrlAsync(Secret)).ToString();
        var registered = await KnowledgeBaseApi.RegisterOkAsync(admin, url, collection, apiKey: Secret, mapping: KnowledgeBaseApi.TextOnly(), createFields: []);

        using var kept = await admin.PutAsJsonAsync(KnowledgeBaseApi.One(registered.Id), UpdateBody(url, collection, description: "Changed."), Ct);
        var keptOutcome = (await kept.Content.ReadFromJsonAsync<KnowledgeBaseApi.VerifiedBody>(Ct))!;
        using var replaced = await admin.PutAsJsonAsync(KnowledgeBaseApi.One(registered.Id), UpdateBody(url, collection, apiKey: "wrong-key"), Ct);
        var replacedOutcome = (await replaced.Content.ReadFromJsonAsync<KnowledgeBaseApi.VerifiedBody>(Ct))!;

        Assert.Equal("Available", keptOutcome.Status);
        Assert.Equal("Unavailable", replacedOutcome.Status);
        Assert.True((await KnowledgeBaseApi.GetAsync(admin, registered.Id)).Connection!.HasApiKey);
    }

    [Fact] // AC-16
    public async Task TheStoredKeyIsNotReusedForAnotherServer()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var collection = QdrantFixture.NewPrefix() + SampleCollections.Flat;
        await qdrant.SeedAsync([SampleCollections.CreateFlat(collection)], Secret);
        var url = (await qdrant.GetUrlAsync(Secret)).ToString();
        var registered = await KnowledgeBaseApi.RegisterOkAsync(admin, url, collection, apiKey: Secret, mapping: KnowledgeBaseApi.TextOnly(), createFields: []);

        using var elsewhere = await admin.PutAsJsonAsync(KnowledgeBaseApi.One(registered.Id), UpdateBody("http://attacker.example:6334", collection), Ct);
        using var withKey = await admin.PutAsJsonAsync(KnowledgeBaseApi.One(registered.Id), UpdateBody("http://[::1]:1", collection, apiKey: "typed-again"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, elsewhere.StatusCode);
        Assert.Equal("knowledge_base.api_key_required", await KnowledgeBaseApi.ProblemCodeAsync(elsewhere));
        Assert.Equal(HttpStatusCode.OK, withKey.StatusCode);
        Assert.True((await KnowledgeBaseApi.GetAsync(admin, registered.Id)).Connection!.HasApiKey);
    }

    [Fact] // AC-18
    public async Task AnUpdateCannotTakeAnotherKnowledgeBasesName()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var collection = QdrantFixture.NewPrefix() + SampleCollections.Flat;
        await qdrant.SeedAsync([SampleCollections.CreateFlat(collection)]);
        var url = (await qdrant.GetUrlAsync()).ToString();
        await KnowledgeBaseApi.RegisterOkAsync(admin, url, collection, "first", mapping: KnowledgeBaseApi.TextOnly(), createFields: []);
        var second = await KnowledgeBaseApi.RegisterOkAsync(admin, url, collection, "second", mapping: KnowledgeBaseApi.TextOnly(), createFields: []);

        using var taken = await admin.PutAsJsonAsync(KnowledgeBaseApi.One(second.Id), UpdateBody(url, collection, "first"), Ct);
        using var same = await admin.PutAsJsonAsync(KnowledgeBaseApi.One(second.Id), UpdateBody(url, collection, "second"), Ct);

        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal("knowledge_base.name_taken", await KnowledgeBaseApi.ProblemCodeAsync(taken));
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
    }

    [Fact]
    public async Task AnUpdateOfAnUnknownKnowledgeBaseIsNotFound()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        using var response = await admin.PutAsJsonAsync(KnowledgeBaseApi.One(Guid.NewGuid()), UpdateBody("http://localhost:6334", "docs"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact] // D-17
    public async Task DeletingRemovesTheRegistrationAndLeavesTheDictionaryAndTheStoreAlone()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var collection = QdrantFixture.NewPrefix() + SampleCollections.Flat;
        await qdrant.SeedAsync([SampleCollections.CreateFlat(collection)]);
        var url = (await qdrant.GetUrlAsync()).ToString();
        var registered = await KnowledgeBaseApi.RegisterOkAsync(admin, url, collection);

        using var deleted = await admin.DeleteAsync(KnowledgeBaseApi.One(registered.Id), Ct);
        using var again = await admin.DeleteAsync(KnowledgeBaseApi.One(registered.Id), Ct);
        using var read = await admin.GetAsync(KnowledgeBaseApi.One(registered.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Empty(await KnowledgeBaseApi.ListAsync(admin));
        Assert.Equal(4, (await DictionaryApi.ListAsync(admin)).Count);
        using var rest = new HttpClient { BaseAddress = await qdrant.GetRestUrlAsync() };
        using var stillThere = await rest.GetAsync(new Uri($"/collections/{collection}", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, stillThere.StatusCode);
    }
}
