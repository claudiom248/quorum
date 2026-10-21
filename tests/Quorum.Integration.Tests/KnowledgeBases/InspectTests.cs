using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.KnowledgeBases.Storage.Qdrant;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBases;

public sealed class InspectTests(PostgresFixture postgres, QdrantFixture qdrant)
{
    private static SampleDataset Custom(string name, IEnumerable<SamplePoint> points, params SamplePayloadIndex[] indexes) =>
        new(name, "A collection shaped by one test.", [new SampleVector(null, DeterministicEmbedding.DefaultDimensions)], indexes, points.ToList());

    private static SamplePoint Point(int id, string payload) =>
        new((ulong)id, "text", JsonNode.Parse(payload)!.AsObject());

    [Fact] // AC-6
    public async Task AtMostTwentyPointsAreSampledAndTheDraftComesBack()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        InspectApi.ServeEmbeddings(app);
        var name = QdrantFixture.NewPrefix() + "thirty";
        var early = Enumerable.Range(1, 20).Select(i => Point(i, """{"text":"early","year":2024}"""));
        var late = Enumerable.Range(21, 10).Select(i => Point(i, """{"text":"late","year":2024,"only_late":"x"}"""));
        await qdrant.SeedAsync([Custom(name, early.Concat(late), new SamplePayloadIndex("year", "integer"))]);

        var report = await InspectApi.InspectOkAsync(admin, await qdrant.GetUrlAsync(), name);

        Assert.True(report.Reachable);
        Assert.True(report.CollectionExists);
        Assert.Equal("text", report.DraftMapping.Roles.Text);
        Assert.Equal(["year"], report.DraftMapping.Fields.Select(f => f.Name));
        Assert.DoesNotContain(report.DraftMapping.Fields, f => f.Path == "only_late");
        Assert.Equal([("year", "Number")], report.ProposedFields.Select(f => (f.Name, f.Type)));
        Assert.Empty(report.UnindexedFields);
    }

    [Fact] // AC-6
    public async Task FieldsWithoutAPayloadIndexAreListedAndWarnedAbout()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        InspectApi.ServeEmbeddings(app);
        var prefix = QdrantFixture.NewPrefix();
        await qdrant.SeedAsync([SampleCollections.CreateUnindexed(prefix + SampleCollections.Unindexed), SampleCollections.CreateFlat(prefix + SampleCollections.Flat)]);
        var url = await qdrant.GetUrlAsync();

        var unindexed = await InspectApi.InspectOkAsync(admin, url, prefix + SampleCollections.Unindexed);
        var flat = await InspectApi.InspectOkAsync(admin, url, prefix + SampleCollections.Flat);

        Assert.Equal(["confidential", "department", "published_on", "year"], unindexed.UnindexedFields.Order(StringComparer.Ordinal));
        Assert.Equal(4, unindexed.Warnings.Count(w => w.Code == "knowledge_base.field_unindexed"));
        Assert.Empty(flat.UnindexedFields);
        Assert.DoesNotContain("knowledge_base.field_unindexed", flat.WarningCodes);
    }

    [Fact] // AC-7
    public async Task TheRolesAndTheNestedYearAreFoundInASampledCollection()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        InspectApi.ServeEmbeddings(app);
        var name = QdrantFixture.NewPrefix() + SampleCollections.Nested;
        await qdrant.SeedAsync([SampleCollections.CreateNested(name)]);

        var report = await InspectApi.InspectOkAsync(admin, await qdrant.GetUrlAsync(), name);

        Assert.Equal(new InspectApi.RolesBody("chunk_text", "doc_id", "file_name", "page", "source_url"), report.DraftMapping.Roles);
        var year = report.DraftMapping.Fields.Single(f => f.Path == "meta.year");
        Assert.Equal("year", year.Name);
        Assert.Equal("Number", year.Type);
        Assert.Contains(("published_on", "Date"), report.DraftMapping.Fields.Select(f => (f.Name, f.Type)));
        Assert.Equal(["meta.department", "meta.published_on", "meta.year"], report.DraftMapping.Fields.Select(f => f.Path).Order(StringComparer.Ordinal));
        Assert.Equal(["meta.published_on"], report.UnindexedFields);
    }

    [Fact] // AC-8
    public async Task APastedSchemaGivesTheSameDraftAsSamplingEvenOnAnEmptyCollection()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        InspectApi.ServeEmbeddings(app);
        var prefix = QdrantFixture.NewPrefix();
        await qdrant.SeedAsync([SampleCollections.CreateNested(prefix + "full"), SampleCollections.CreateEmpty(prefix + "empty")]);
        var url = await qdrant.GetUrlAsync();

        var sampled = await InspectApi.InspectOkAsync(admin, url, prefix + "full");
        var described = await InspectApi.InspectOkAsync(admin, url, prefix + "empty", schema: SampleSchemas.Nested);

        Assert.Equal(sampled.DraftMapping.Roles, described.DraftMapping.Roles);
        Assert.Equal(sampled.DraftMapping.Fields.Select(f => (f.Path, f.Name, f.Type)).Order(), described.DraftMapping.Fields.Select(f => (f.Path, f.Name, f.Type)).Order());
        Assert.Contains("knowledge_base.empty_collection", described.WarningCodes);
    }

    [Fact] // AC-8
    public async Task ASchemaSentAsAnObjectIsAcceptedToo()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        InspectApi.ServeEmbeddings(app);
        var name = QdrantFixture.NewPrefix() + "empty";
        await qdrant.SeedAsync([SampleCollections.CreateEmpty(name)]);

        var report = await InspectApi.InspectOkAsync(admin, await qdrant.GetUrlAsync(), name, schema: JsonNode.Parse(SampleSchemas.Flat));

        Assert.Equal("text", report.DraftMapping.Roles.Text);
        Assert.Contains(report.DraftMapping.Fields, f => f.Name == "published_on");
    }

    [Theory] // AC-8
    [InlineData("this is not json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{\"type\":\"object\"}")]
    public async Task ASchemaThatIsNotAnObjectSchemaIsRefused(string schema)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        InspectApi.ServeEmbeddings(app);
        var name = QdrantFixture.NewPrefix() + "any";
        await qdrant.SeedAsync([SampleCollections.CreateEmpty(name)]);

        using var response = await InspectApi.PostAsync(admin, (await qdrant.GetUrlAsync()).ToString(), name, schema: schema);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("knowledge_base.invalid_schema", await InspectApi.ProblemCodeAsync(response));
    }

    [Fact] // AC-8
    public async Task ASchemaOverOneHundredKilobytesIsRefused()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var schema = "{\"type\":\"object\",\"description\":\"" + new string('x', 101 * 1024) + "\",\"properties\":{\"a\":{\"type\":\"string\"}}}";

        using var response = await InspectApi.PostAsync(admin, "http://[::1]:1", "any", schema: schema);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("knowledge_base.invalid_schema", await InspectApi.ProblemCodeAsync(response));
    }

    [Fact] // AC-9
    public async Task CollidingInvalidAndReservedNamesAreFlaggedAndNotProposed()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        InspectApi.ServeEmbeddings(app);
        var name = QdrantFixture.NewPrefix() + "names";
        await qdrant.SeedAsync([Custom(name, [Point(1, """{"text":"a","meta":{"year":1},"info":{"year":2},"Release-Date":"x","size_min":3,"team":"hr"}""")])]);

        var report = await InspectApi.InspectOkAsync(admin, await qdrant.GetUrlAsync(), name);

        var issues = report.DraftMapping.Fields.ToDictionary(f => f.Path, f => f.Issues);
        Assert.Equal(["knowledge_base.field_name_collision"], issues["meta.year"]);
        Assert.Equal(["knowledge_base.field_name_collision"], issues["info.year"]);
        Assert.Equal(["knowledge_base.invalid_field_name"], issues["Release-Date"]);
        Assert.Equal(["knowledge_base.reserved_field_name"], issues["size_min"]);
        Assert.Empty(issues["team"]);
        Assert.Equal(["team"], report.ProposedFields.Select(f => f.Name));
    }

    [Fact]
    public async Task AVisibleDictionaryFieldIsLinkedAndOneOfAnotherTypeIsAConflict()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        InspectApi.ServeEmbeddings(app);
        var name = QdrantFixture.NewPrefix() + "known";
        await qdrant.SeedAsync([Custom(name, [Point(1, """{"text":"a","year":2024,"department":"hr","team":"x"}""")])]);
        var yearId = await DictionaryApi.CreateOkAsync(admin, "year", "Number");
        await DictionaryApi.CreateOkAsync(admin, "department", "Number");

        var report = await InspectApi.InspectOkAsync(admin, await qdrant.GetUrlAsync(), name);

        var fields = report.DraftMapping.Fields.ToDictionary(f => f.Name);
        Assert.Equal(yearId, fields["year"].DictionaryFieldId);
        Assert.Empty(fields["year"].Issues);
        Assert.Equal(["knowledge_base.field_type_conflict"], fields["department"].Issues);
        Assert.Null(fields["team"].DictionaryFieldId);
        Assert.Equal(["team"], report.ProposedFields.Select(f => f.Name));
    }

    [Fact] // AC-11 (the inspect half: the admin is told to choose)
    public async Task SeveralVectorsAndNoChosenNameAskTheAdminToChoose()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        InspectApi.ServeEmbeddings(app);
        var name = QdrantFixture.NewPrefix() + SampleCollections.NamedVectors;
        await qdrant.SeedAsync([SampleCollections.CreateNamedVectors(name)]);
        var url = await qdrant.GetUrlAsync();

        var unchosen = await InspectApi.InspectOkAsync(admin, url, name);
        var chosen = await InspectApi.InspectOkAsync(admin, url, name, vectorName: SampleCollections.DenseVectorName);
        var wrong = await InspectApi.InspectOkAsync(admin, url, name, vectorName: "nope");

        Assert.Equal([("dense", 64), ("summary", 32)], unchosen.Vectors.Select(v => (v.Name!, v.Size)).Order());
        Assert.Contains("knowledge_base.vector_name_required", unchosen.WarningCodes);
        Assert.Null(unchosen.DimensionMatch);
        Assert.True(chosen.DimensionMatch);
        Assert.DoesNotContain("knowledge_base.vector_name_required", chosen.WarningCodes);
        Assert.Contains("knowledge_base.vector_not_found", wrong.WarningCodes);
        Assert.Null(wrong.DimensionMatch);
    }

    [Fact]
    public async Task VectorsOfAnotherSizeThanTheModelsAreAMismatch()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        InspectApi.ServeEmbeddings(app);
        var name = QdrantFixture.NewPrefix() + SampleCollections.OtherVectorSize;
        await qdrant.SeedAsync([SampleCollections.CreateOtherVectorSize(name)]);

        var report = await InspectApi.InspectOkAsync(admin, await qdrant.GetUrlAsync(), name);

        Assert.Equal(64, report.EmbeddingSize);
        Assert.Equal(128, Assert.Single(report.Vectors).Size);
        Assert.False(report.DimensionMatch);
        var warning = Assert.Single(report.Warnings, w => w.Code == "knowledge_base.dimension_mismatch");
        Assert.Contains("128", warning.Message, StringComparison.Ordinal);
        Assert.Contains("64", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MatchingDimensionsAreConfirmed()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        InspectApi.ServeEmbeddings(app);
        var name = QdrantFixture.NewPrefix() + SampleCollections.Flat;
        await qdrant.SeedAsync([SampleCollections.CreateFlat(name)]);

        var report = await InspectApi.InspectOkAsync(admin, await qdrant.GetUrlAsync(), name);

        Assert.Equal(64, report.EmbeddingSize);
        Assert.True(report.DimensionMatch);
        var vector = Assert.Single(report.Vectors);
        Assert.Null(vector.Name);
        Assert.Equal("Cosine", vector.Distance);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public async Task AnEmptyCollectionHasNothingToSampleAndSaysSo()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        InspectApi.ServeEmbeddings(app);
        var name = QdrantFixture.NewPrefix() + SampleCollections.Empty;
        await qdrant.SeedAsync([SampleCollections.CreateEmpty(name)]);

        var report = await InspectApi.InspectOkAsync(admin, await qdrant.GetUrlAsync(), name);

        Assert.True(report.CollectionExists);
        Assert.Null(report.DraftMapping.Roles.Text);
        Assert.Empty(report.DraftMapping.Fields);
        Assert.Contains("knowledge_base.empty_collection", report.WarningCodes);
    }

    [Fact]
    public async Task AMissingCollectionIsReportedOnAReachableServer()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        InspectApi.ServeEmbeddings(app);

        var report = await InspectApi.InspectOkAsync(admin, await qdrant.GetUrlAsync(), QdrantFixture.NewPrefix() + "missing");

        Assert.True(report.Reachable);
        Assert.False(report.CollectionExists);
        Assert.Empty(report.Vectors);
        Assert.Contains("knowledge_base.collection_not_found", report.WarningCodes);
        Assert.Equal(64, report.EmbeddingSize);
        Assert.Null(report.DimensionMatch);
    }

    [Fact]
    public async Task AServerThatDoesNotAnswerIsReportedAsUnreachableWithTheEmbeddingStillChecked()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        InspectApi.ServeEmbeddings(app);

        var report = await InspectApi.InspectOkAsync(admin, new Uri("http://[::1]:1"), "anything");

        Assert.False(report.Reachable);
        Assert.False(report.CollectionExists);
        Assert.Contains("knowledge_base.qdrant_unreachable", report.WarningCodes);
        Assert.Equal(64, report.EmbeddingSize);
    }

    [Fact]
    public async Task TheApiKeyIsSentWhenThereIsOneAndNeverReturnedOrLogged()
    {
        const string key = "s3cr3t-key-for-the-test";
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        InspectApi.ServeEmbeddings(app);
        var name = QdrantFixture.NewPrefix() + SampleCollections.Flat;
        await qdrant.SeedAsync([SampleCollections.CreateFlat(name)], apiKey: key);
        var url = await qdrant.GetUrlAsync(key);

        var without = await InspectApi.InspectOkAsync(admin, url, name);
        var wrong = await InspectApi.InspectOkAsync(admin, url, name, apiKey: "not-the-key");
        using var withKey = await InspectApi.PostAsync(admin, url.ToString(), name, apiKey: key);
        var withKeyBody = await withKey.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.False(without.Reachable);
        Assert.Contains("knowledge_base.qdrant_unauthorized", without.WarningCodes);
        Assert.False(wrong.Reachable);
        Assert.Equal(HttpStatusCode.OK, withKey.StatusCode);
        Assert.Contains("\"reachable\":true", withKeyBody, StringComparison.Ordinal);
        Assert.DoesNotContain(key, withKeyBody, StringComparison.Ordinal);
        Assert.DoesNotContain(app.Logs.Messages, message => message.Contains(key, StringComparison.Ordinal) || message.Contains("not-the-key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnApiKeyWithCharactersAHeaderCannotCarryIsRefusedByName()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        using var response = await InspectApi.PostAsync(admin, "http://[::1]:1", "docs", apiKey: "key-with-é-in-it");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("knowledge_base.invalid_connection", await InspectApi.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ACallTheCallerCancelledIsCancelledNotReportedAsAFailureOfTheServer()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var connector = app.Services.GetRequiredService<QdrantConnector>();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var connection = new QdrantConnection(await qdrant.GetUrlAsync(), "docs", ApiKey: null, VectorName: null);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connector.GetCollectionAsync(connection, cancelled.Token));
    }

    [Fact]
    public async Task AModelTheEndpointDoesNotServeIsAWarningNotAFailure()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var name = QdrantFixture.NewPrefix() + SampleCollections.Flat;
        await qdrant.SeedAsync([SampleCollections.CreateFlat(name)]);

        var report = await InspectApi.InspectOkAsync(admin, await qdrant.GetUrlAsync(), name, model: "not-served");

        Assert.Null(report.EmbeddingSize);
        Assert.Null(report.DimensionMatch);
        var warning = Assert.Single(report.Warnings, w => w.Code == "knowledge_base.embedding_failed");
        Assert.Contains("not-served", warning.Message, StringComparison.Ordinal);
        Assert.Equal("text", report.DraftMapping.Roles.Text);
    }

    [Fact]
    public async Task InspectingLeavesNoQueryChunkTextOrDocumentNameInTheLogs()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        InspectApi.ServeEmbeddings(app);
        var name = QdrantFixture.NewPrefix() + SampleCollections.Flat;
        await qdrant.SeedAsync([SampleCollections.CreateFlat(name)]);

        await InspectApi.InspectOkAsync(admin, await qdrant.GetUrlAsync(), name);

        Assert.DoesNotContain(app.Logs.Messages, m => m.Contains("annual leave", StringComparison.OrdinalIgnoreCase) || m.Contains("Password and access", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(null, "http://127.0.0.1:6333", "docs", "nomic", "knowledge_base.unsupported_kind")]
    [InlineData("Pgvector", "http://127.0.0.1:6333", "docs", "nomic", "knowledge_base.unsupported_kind")]
    [InlineData("Qdrant", null, "docs", "nomic", "knowledge_base.invalid_connection")]
    [InlineData("Qdrant", "not a url", "docs", "nomic", "knowledge_base.invalid_connection")]
    [InlineData("Qdrant", "ftp://qdrant:6333", "docs", "nomic", "knowledge_base.invalid_connection")]
    [InlineData("Qdrant", "http://user:pass@qdrant:6333", "docs", "nomic", "knowledge_base.invalid_connection")]
    [InlineData("Qdrant", "http://127.0.0.1:6333", "", "nomic", "knowledge_base.invalid_connection")]
    [InlineData("Qdrant", "http://127.0.0.1:6333", "docs", "", "knowledge_base.invalid_embedding_model")]
    public async Task AnIncompleteRequestIsRefusedBeforeAnythingIsContacted(string? kind, string? url, string collection, string model, string code)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        using var response = await admin.PostAsJsonAsync(
            InspectApi.Inspect,
            new { kind, connection = new { url, collection }, embeddingModel = model },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, await InspectApi.ProblemCodeAsync(response));
    }
}
