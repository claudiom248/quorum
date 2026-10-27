using System.Text.Json;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.KnowledgeBases.Storage;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBases.Storage;

/// <summary>Spec 008 D-11, D-12, A1-2: what the PgVector adapter asks the database for, against a real PostgreSQL with pgvector.</summary>
public sealed class PgVectorSearchAdapterTests(PgVectorFixture pgvector)
{
    private static readonly string[] Hr = ["hr"];
    private static readonly string[] Paths = ["text", "document_id", "department", "year"];
    private static readonly string[] JsonPaths = ["document", "cmetadata.document_id", "cmetadata.department"];

    private static string LeaveText => SampleCollections.CreateFlat().Points.Single(p => p.Id == 1).EmbeddingText;

    private static IStoreAdapter Adapter { get; } = FakeStoreAdapter.PgVectorStub();

    private async Task<IReadOnlyList<StoreHit>> SearchAsync(
        string relation,
        IReadOnlyList<FilterCondition>? conditions = null,
        IReadOnlyList<string>? paths = null,
        int limit = 5,
        double? threshold = null,
        string? distance = null,
        string? schema = null,
        string? text = null,
        string? vectorColumn = null,
        float[]? vector = null)
    {
        schema ??= await pgvector.GetSharedSchemaAsync();
        var fields = PgVectorApi.Fields(await pgvector.GetEndpointAsync(), schema, relation, vectorColumn: vectorColumn, distance: distance);
        var read = Adapter.ReadConnection(JsonSerializer.SerializeToElement(fields));
        Assert.True(read.IsSuccess);
        var query = new StoreQuery(vector ?? DeterministicEmbedding.Embed(text ?? LeaveText), conditions ?? [], limit, threshold, paths ?? Paths);
        return await Adapter.SearchAsync(read.Value, query, TestContext.Current.CancellationToken);
    }

    private static string Text(StoreHit hit, string path) => PayloadPath.Read(hit.Row, path)!.GetValue<string>();

    [Fact] // AC-21, AC-22
    public async Task TheNearestRowsComeBackBestFirstWithTheCosineSimilarity()
    {
        var hits = await SearchAsync(SampleRelations.Flat);

        Assert.Equal("leave", Text(hits[0], "document_id"));
        Assert.True(hits[0].Score > 0.999);
        Assert.Equal(hits.OrderByDescending(h => h.Score), hits);
        Assert.All(hits, hit => Assert.Equal(Paths.Length, hit.Row.Count));
    }

    [Fact] // AC-22
    public async Task AThresholdKeepsOnlyTheRowsOnTheRightSideOfIt()
    {
        var all = await SearchAsync(SampleRelations.Flat, limit: 50);
        var threshold = all[1].Score;

        var kept = await SearchAsync(SampleRelations.Flat, limit: 50, threshold: threshold);

        Assert.All(kept, hit => Assert.True(hit.Score >= threshold - 1e-9));
        Assert.Equal(all.Count(h => h.Score >= threshold - 1e-9), kept.Count);
    }

    [Fact] // AC-22
    public async Task TheL2ScoreIsTheDistanceAndTheThresholdIsAnUpperBound()
    {
        var all = await SearchAsync(SampleRelations.IvfFlatL2, distance: "L2", limit: 50, paths: ["text"]);

        Assert.True(all[0].Score < 1e-3);
        Assert.Equal(all.OrderBy(h => h.Score), all);
        var bound = all[1].Score;
        var kept = await SearchAsync(SampleRelations.IvfFlatL2, distance: "L2", limit: 50, threshold: bound, paths: ["text"]);
        Assert.All(kept, hit => Assert.True(hit.Score <= bound + 1e-9));
        Assert.Equal(all.Count(h => h.Score <= bound + 1e-9), kept.Count);
    }

    [Fact] // AC-22
    public async Task TheInnerProductScoreIsTheInnerProductAndTheThresholdIsALowerBound()
    {
        var all = await SearchAsync(SampleRelations.InnerProduct, distance: "InnerProduct", limit: 50, paths: ["text"]);

        Assert.True(all[0].Score > 0.999);
        Assert.Equal(all.OrderByDescending(h => h.Score), all);
        var bound = all[1].Score;
        var kept = await SearchAsync(SampleRelations.InnerProduct, distance: "InnerProduct", limit: 50, threshold: bound, paths: ["text"]);
        Assert.All(kept, hit => Assert.True(hit.Score >= bound - 1e-9));
    }

    [Fact] // AC-23
    public async Task FiltersOnPlainColumnsOfEveryTypeApplyInAnd()
    {
        var hr = await SearchAsync(SampleRelations.Flat, [new MatchAnyCondition("department", Hr)], limit: 50);
        Assert.NotEmpty(hr);
        Assert.All(hr, hit => Assert.Equal("hr", Text(hit, "department")));

        var recent = await SearchAsync(SampleRelations.Flat, [new NumberRangeCondition("year", 2024, null)], limit: 50);
        Assert.NotEmpty(recent);
        Assert.All(recent, hit => Assert.True(PayloadPath.Read(hit.Row, "year")!.GetValue<long>() >= 2024));

        var from = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var dated = await SearchAsync(SampleRelations.Flat, [new DateRangeCondition("published_on", from, null)], paths: ["text", "published_on"], limit: 50);
        Assert.NotEmpty(dated);
        Assert.All(dated, hit => Assert.True(string.CompareOrdinal(Text(hit, "published_on"), "2024-01-01") >= 0));

        var open = await SearchAsync(SampleRelations.Flat, [new MatchBooleanCondition("confidential", false)], paths: ["text", "confidential"], limit: 50);
        Assert.NotEmpty(open);
        Assert.All(open, hit => Assert.False(PayloadPath.Read(hit.Row, "confidential")!.GetValue<bool>()));

        var combined = await SearchAsync(
            SampleRelations.Flat,
            [new MatchAnyCondition("department", Hr), new NumberRangeCondition("year", null, 2023)],
            limit: 50);
        Assert.All(combined, hit => Assert.Equal("hr", Text(hit, "department")));
        Assert.All(combined, hit => Assert.True(PayloadPath.Read(hit.Row, "year")!.GetValue<long>() <= 2023));
    }

    [Fact] // AC-21
    public async Task AHalfPrecisionColumnAndTheChosenOfSeveralVectorColumnsAreSearched()
    {
        var half = await SearchAsync(SampleRelations.HalfVector, paths: ["text"], distance: "Cosine");
        Assert.True(half[0].Score > 0.99);

        var summary = await SearchAsync(SampleRelations.TwoVectors, paths: ["text"], vectorColumn: SampleRelations.SummaryColumn, vector: DeterministicEmbedding.Embed(LeaveText, 32));
        Assert.NotEmpty(summary);
    }

    [Fact] // AC-23
    public async Task AListColumnMatchesWhenItSharesAValue()
    {
        var hits = await SearchAsync(SampleRelations.Tags, [new MatchAnyCondition("tags", Hr)], paths: ["content", "tags"], limit: 50);

        Assert.NotEmpty(hits);
        Assert.All(hits, hit => Assert.Contains("hr", PayloadPath.Read(hit.Row, "tags")!.AsArray().Select(tag => tag!.GetValue<string>())));
    }

    [Fact] // AC-23
    public async Task FiltersOverTheKeysOfAJsonbColumnApplyAndTheRowShapeIsNested()
    {
        var hr = await SearchAsync(SampleRelations.LangChain, [new MatchAnyCondition("cmetadata.department", Hr)], JsonPaths, limit: 50);
        Assert.NotEmpty(hr);
        Assert.All(hr, hit => Assert.Equal("hr", Text(hit, "cmetadata.department")));
        Assert.All(hr, hit => Assert.False(string.IsNullOrWhiteSpace(Text(hit, "document"))));

        var recent = await SearchAsync(SampleRelations.LangChain, [new NumberRangeCondition("cmetadata.year", 2024, null)], ["document", "cmetadata.year"], limit: 50);
        Assert.NotEmpty(recent);
        Assert.All(recent, hit => Assert.True(PayloadPath.Read(hit.Row, "cmetadata.year")!.GetValue<long>() >= 2024));

        var dated = await SearchAsync(SampleRelations.LangChain, [new DateRangeCondition("cmetadata.published_on", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), null)], ["document", "cmetadata.published_on"], limit: 50);
        Assert.NotEmpty(dated);
        Assert.All(dated, hit => Assert.True(string.CompareOrdinal(Text(hit, "cmetadata.published_on"), "2024-01-01") >= 0));

        var open = await SearchAsync(SampleRelations.LangChain, [new MatchBooleanCondition("cmetadata.confidential", false)], ["document", "cmetadata.confidential"], limit: 50);
        Assert.NotEmpty(open);
        Assert.All(open, hit => Assert.False(PayloadPath.Read(hit.Row, "cmetadata.confidential")!.GetValue<bool>()));
    }

    [Fact] // AC-24
    public async Task AJsonbKeyOfAnotherTypeInSomeRowsIsNotAMatchAndNeverAnError()
    {
        var schema = await pgvector.NewSchemaAsync();
        await pgvector.ExecuteAsync(
            $$"""
            CREATE TABLE {{schema}}.mixed (id bigint PRIMARY KEY, text text, meta jsonb, embedding vector(3));
            INSERT INTO {{schema}}.mixed VALUES
                (1, 'number', '{"year": 2024, "day": "2024-05-01", "ok": true, "tag": "a"}', '[1,0,0]'),
                (2, 'string', '{"year": "twenty", "day": 5, "ok": "yes", "tag": 7}', '[1,0.1,0]'),
                (3, 'object', '{"year": {"x": 1}, "day": "not a date", "ok": null, "tag": ["a", 3]}', '[1,0.2,0]'),
                (4, 'missing', '{}', '[1,0.3,0]'),
                (5, 'null column', NULL, '[1,0.4,0]');
            """);
        var vector = new[] { 1f, 0f, 0f };

        var numbers = await SearchAsync("mixed", [new NumberRangeCondition("meta.year", 2000, null)], ["text"], schema: schema, vector: vector);
        var dates = await SearchAsync("mixed", [new DateRangeCondition("meta.day", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), null)], ["text"], schema: schema, vector: vector);
        var flags = await SearchAsync("mixed", [new MatchBooleanCondition("meta.ok", true)], ["text"], schema: schema, vector: vector);
        var tags = await SearchAsync("mixed", [new MatchAnyCondition("meta.tag", ["a"])], ["text"], schema: schema, vector: vector);

        Assert.Equal(["number"], numbers.Select(h => Text(h, "text")));
        Assert.Equal(["number"], dates.Select(h => Text(h, "text")));
        Assert.Equal(["number"], flags.Select(h => Text(h, "text")));
        Assert.Equal(["number", "object"], tags.Select(h => Text(h, "text")));
    }

    [Fact] // AC-25
    public async Task ValuesAreDataAndIdentifiersWithCapitalsSpacesAndReservedWordsWork()
    {
        var injected = await SearchAsync(SampleRelations.Flat, [new MatchAnyCondition("department", ["x'); DROP TABLE sample_flat; --", "\" OR 1=1 --"])]);
        Assert.Empty(injected);
        Assert.NotEmpty(await SearchAsync(SampleRelations.Flat));

        var hits = await SearchAsync(
            SampleRelations.MixedCase,
            [new NumberRangeCondition("Page", 1, null)],
            ["Chunk Text", "Select", "Page"],
            vectorColumn: null);
        Assert.NotEmpty(hits);
        Assert.All(hits, hit => Assert.False(string.IsNullOrWhiteSpace(Text(hit, "Chunk Text"))));
        Assert.All(hits, hit => Assert.NotNull(PayloadPath.Read(hit.Row, "Select")));
    }

    [Fact] // AC-26
    public async Task ASelectiveFilterOnAnApproximateIndexStillReturnsTheRowsAsked()
    {
        var schema = await pgvector.NewSchemaAsync();
        await pgvector.ExecuteAsync(
            $"""
            CREATE TABLE {schema}.big (id bigint PRIMARY KEY, text text, category integer, embedding vector(8));
            INSERT INTO {schema}.big
                SELECT i, 'row ' || i, i % 100, (SELECT array_agg(random()::real) FROM generate_series(1, 8))::vector FROM generate_series(1, 4000) i;
            CREATE INDEX ON {schema}.big USING hnsw (embedding vector_cosine_ops);
            ANALYZE {schema}.big;
            """);

        var hits = await SearchAsync("big", [new NumberRangeCondition("category", 7, 7)], ["text", "category"], limit: 10, schema: schema, vector: [1, 0, 0, 0, 0, 0, 0, 0]);

        Assert.Equal(10, hits.Count);
        Assert.All(hits, hit => Assert.Equal(7, PayloadPath.Read(hit.Row, "category")!.GetValue<long>()));
        Assert.Equal(hits.OrderByDescending(h => h.Score), hits);
    }

    [Theory] // AC-40, AC-41
    [InlineData("text", "'doc-1', 'doc-2'", "doc-1", "doc-2", "abc")]
    [InlineData("integer", "1, 2", "1", "2", "abc")]
    [InlineData("bigint", "1, 2", "1", "2", "abc")]
    [InlineData("uuid", "'00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000002'", "00000000-0000-0000-0000-000000000001", "00000000-0000-0000-0000-000000000002", "abc")]
    public async Task DocumentIdsAreComparedInTheTypeOfTheColumn(string type, string values, string first, string second, string unconvertible)
    {
        var schema = await pgvector.NewSchemaAsync();
        await pgvector.ExecuteAsync(
            $"""
            CREATE TABLE {schema}.docs (id bigint PRIMARY KEY, text text, doc {type}, embedding vector(3));
            INSERT INTO {schema}.docs SELECT n, 'chunk ' || n, (ARRAY[{values}])[n]::{type}, '[1,0,0]' FROM generate_series(1, 2) n;
            CREATE INDEX ON {schema}.docs (doc);
            """);

        var one = await SearchAsync("docs", [new DocumentIdsCondition("doc", [first])], ["text"], schema: schema, vector: [1, 0, 0]);
        var both = await SearchAsync("docs", [new DocumentIdsCondition("doc", [first, second, unconvertible])], ["text"], schema: schema, vector: [1, 0, 0]);
        var none = await SearchAsync("docs", [new DocumentIdsCondition("doc", [unconvertible])], ["text"], schema: schema, vector: [1, 0, 0]);

        Assert.Equal(["chunk 1"], one.Select(h => Text(h, "text")));
        Assert.Equal(["chunk 1", "chunk 2"], both.Select(h => Text(h, "text")).Order(StringComparer.Ordinal));
        Assert.Empty(none);
    }

    [Fact] // AC-40
    public async Task DocumentIdsOverAJsonbKeyAreComparedAsText()
    {
        var hits = await SearchAsync(SampleRelations.LangChain, [new DocumentIdsCondition("cmetadata.document_id", ["leave"])], JsonPaths, limit: 50);

        Assert.NotEmpty(hits);
        Assert.All(hits, hit => Assert.Equal("leave", Text(hit, "cmetadata.document_id")));
    }

    [Fact] // AC-23
    public async Task AColumnThatIsGoneFailsAsNotFoundWithoutTheSql()
    {
        var failure = await Assert.ThrowsAsync<StoreException>(() => SearchAsync(SampleRelations.Flat, [new MatchAnyCondition("no_such_column", Hr)]));

        Assert.Equal(StoreFailure.NotFound, failure.Failure);
        Assert.DoesNotContain("no_such_column", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT", failure.Message, StringComparison.Ordinal);
    }

    [Fact] // AC-28
    public async Task AVectorOfAnotherSizeIsRejectedWithoutTheServersWords()
    {
        var failure = await Assert.ThrowsAsync<StoreException>(() => SearchAsync(SampleRelations.Flat, vector: new float[10]));

        Assert.Equal(StoreFailure.Rejected, failure.Failure);
        Assert.DoesNotContain("dimensions 10", failure.Message, StringComparison.Ordinal);
    }

    [Fact] // a reference column that is gone only leaves the source less precise
    public async Task AMissingReferenceColumnIsSkippedButAMissingTextColumnFails()
    {
        var hits = await SearchAsync(SampleRelations.Flat, paths: ["text", "no_such_page", "document_id"]);

        Assert.NotEmpty(hits);
        Assert.All(hits, hit => Assert.Equal(["text", "document_id"], hit.Row.Select(p => p.Key)));
        var failure = await Assert.ThrowsAsync<StoreException>(() => SearchAsync(SampleRelations.Flat, paths: ["no_such_text", "document_id"]));
        Assert.Equal(StoreFailure.NotFound, failure.Failure);
    }

    [Fact] // a data error is not a dimension error
    public async Task AnImpossibleDateInAJsonKeyFailsTheSearchWithoutTheDimensionMessage()
    {
        var schema = await pgvector.NewSchemaAsync();
        await pgvector.ExecuteAsync(
            $$"""
            CREATE TABLE {{schema}}.dates (id bigint PRIMARY KEY, text text, meta jsonb, embedding vector(3));
            INSERT INTO {{schema}}.dates VALUES (1, 't', '{"day": "2024-02-30"}', '[1,0,0]');
            """);

        var failure = await Assert.ThrowsAsync<StoreException>(() => SearchAsync("dates", [new DateRangeCondition("meta.day", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), null)], ["text"], schema: schema, vector: [1, 0, 0]));

        Assert.NotEqual(StoreFailure.Rejected, failure.Failure);
        Assert.DoesNotContain("dimension", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // the relation is described once; a change is noticed after the call that failed
    public async Task AColumnDroppedAfterASearchIsSkippedWhenItIsOnlyAReference()
    {
        var schema = await pgvector.NewSchemaAsync();
        await pgvector.ExecuteAsync($"CREATE TABLE {schema}.changing (id bigint PRIMARY KEY, text text, extra text, embedding vector(3)); INSERT INTO {schema}.changing VALUES (1, 'a', 'x', '[1,0,0]');");
        Assert.NotEmpty(await SearchAsync("changing", paths: ["text", "extra"], schema: schema, vector: [1, 0, 0]));

        await pgvector.ExecuteAsync($"ALTER TABLE {schema}.changing DROP COLUMN extra");
        var hits = await SearchAsync("changing", paths: ["text", "extra"], schema: schema, vector: [1, 0, 0]);

        Assert.All(hits, hit => Assert.DoesNotContain("extra", hit.Row.Select(p => p.Key)));
    }

    [Fact] // AC-28
    public async Task ARelationThatWasDroppedFailsAsNotFound()
    {
        var failure = await Assert.ThrowsAsync<StoreException>(() => SearchAsync("not_there"));

        Assert.Equal(StoreFailure.NotFound, failure.Failure);
    }
}
