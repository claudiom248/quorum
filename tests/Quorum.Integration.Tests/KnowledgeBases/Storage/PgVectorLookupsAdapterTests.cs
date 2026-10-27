using System.Diagnostics;
using System.Text.Json;
using Npgsql;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.KnowledgeBases.Storage;
using Quorum.Modules.KnowledgeBases.Storage.PgVector;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBases.Storage;

/// <summary>Spec 008 Amendment 2: the lookups of a PgVector relation, against a real PostgreSQL with pgvector.</summary>
public sealed class PgVectorLookupsAdapterTests(PgVectorFixture pgvector)
{
    private static readonly string[] Hr = ["hr"];

    private static IStoreAdapter Adapter { get; } = FakeStoreAdapter.PgVectorStub();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(ILookupsCapability Lookups, IStoreConnection Connection)> OpenAsync(string relation, string? schema = null)
    {
        schema ??= await pgvector.GetSharedSchemaAsync();
        var fields = PgVectorApi.Fields(await pgvector.GetEndpointAsync(), schema, relation);
        var read = Adapter.ReadConnection(JsonSerializer.SerializeToElement(fields));
        Assert.True(read.IsSuccess);
        return (Adapter.Capability<ILookupsCapability>()!, read.Value);
    }

    private async Task<DistinctValues> ValuesAsync(string relation, string path, string? documentId = null, IReadOnlyList<FilterCondition>? conditions = null, int max = 100, string? schema = null)
    {
        var (lookups, connection) = await OpenAsync(relation, schema);
        return await lookups.DistinctValuesAsync(connection, new DistinctValuesQuery(path, conditions ?? [], documentId, max, 1_000_000), Ct);
    }

    private static string[] Rendered(DistinctValues values) => [.. values.Values.Select(v => $"{v.Value}={v.Count}")];

    [Fact] // A2-1
    public void ThePgVectorKindDeclaresTheLookupsAndTheSourceListing()
    {
        Assert.NotNull(Adapter.Capability<ILookupsCapability>());
        Assert.NotNull(Adapter.Capability<ISourceListingCapability>());
    }

    [Fact] // AC-44
    public async Task TheValuesOfATextColumnAreCountedByRowsMostFirstWithTiesByValue()
    {
        var values = await ValuesAsync(SampleRelations.Flat, "department");

        Assert.Equal(["hr=4", "finance=2", "it=2", "facilities=1", "legal=1"], Rendered(values));
        Assert.False(values.Truncated);
    }

    [Fact] // AC-45
    public async Task WithADocumentIdTheCountIsOfDistinctDocumentsAndExact()
    {
        var values = await ValuesAsync(SampleRelations.Flat, "department", documentId: "document_id");

        Assert.Equal(["hr=3", "facilities=1", "finance=1", "it=1", "legal=1"], Rendered(values));
    }

    [Fact] // AC-44
    public async Task AListIsCutAtTheCeilingAndSaysSo()
    {
        var cut = await ValuesAsync(SampleRelations.Flat, "department", max: 2);
        var whole = await ValuesAsync(SampleRelations.Flat, "department", max: 5);

        Assert.Equal(["hr=4", "finance=2"], Rendered(cut));
        Assert.True(cut.Truncated);
        Assert.Equal(5, whole.Values.Count);
        Assert.False(whole.Truncated);
    }

    [Fact] // AC-44
    public async Task EachElementOfATextArrayIsAValue()
    {
        var values = await ValuesAsync(SampleRelations.Tags, "tags");

        Assert.Equal(["hr=4", "finance=2", "it=2", "leave=2", "security=2", "travel=2", "confidential=1", "facilities=1", "legal=1", "onboarding=1", "remote=1"], Rendered(values));
    }

    [Fact] // AC-44
    public async Task TheKeysOfAJsonbColumnContributeTheirStringsAndTheStringsOfTheirListsOnly()
    {
        var langChain = await ValuesAsync(SampleRelations.LangChain, "cmetadata.department");
        Assert.Equal(["hr=4", "finance=2", "it=2", "facilities=1", "legal=1"], Rendered(langChain));

        var schema = await pgvector.NewSchemaAsync();
        await pgvector.ExecuteAsync(
            $$$"""
            CREATE TABLE {{{schema}}}.mixed (id bigint PRIMARY KEY, text text, meta jsonb, embedding vector(3));
            INSERT INTO {{{schema}}}.mixed VALUES
                (1, 't', '{"tag": "a"}', '[1,0,0]'),
                (2, 't', '{"tag": ["a", "b", 3, {"x": 1}]}', '[1,0,0]'),
                (3, 't', '{"tag": 7}', '[1,0,0]'),
                (4, 't', '{"tag": {"x": "a"}}', '[1,0,0]'),
                (5, 't', '{"tag": ""}', '[1,0,0]'),
                (6, 't', NULL, '[1,0,0]'),
                (7, 't', '{}', '[1,0,0]');
            """);

        var mixed = await ValuesAsync("mixed", "meta.tag", schema: schema);

        Assert.Equal(["a=2", "b=1"], Rendered(mixed));
    }

    [Fact] // AC-44
    public async Task AUuidAndAnEnumColumnAreListedAsText()
    {
        var schema = await pgvector.NewSchemaAsync();
        await pgvector.ExecuteAsync(
            $$"""
            CREATE TYPE {{schema}}.mood AS ENUM ('calm', 'angry');
            CREATE TABLE {{schema}}.typed (id bigint PRIMARY KEY, text text, owner uuid, mood {{schema}}.mood, embedding vector(3));
            INSERT INTO {{schema}}.typed VALUES
                (1, 't', '00000000-0000-0000-0000-000000000001', 'calm', '[1,0,0]'),
                (2, 't', '00000000-0000-0000-0000-000000000001', 'angry', '[1,0,0]'),
                (3, 't', NULL, 'calm', '[1,0,0]');
            """);

        var owners = await ValuesAsync("typed", "owner", schema: schema);
        var moods = await ValuesAsync("typed", "mood", schema: schema);

        Assert.Equal(["00000000-0000-0000-0000-000000000001=2"], Rendered(owners));
        Assert.Equal(["calm=2", "angry=1"], Rendered(moods));
    }

    [Fact] // AC-45
    public async Task TheCountedPointAndTheFiltersNarrowTheRowsCounted()
    {
        var notConfidential = await ValuesAsync(SampleRelations.Flat, "department", conditions: [new MatchBooleanCondition("confidential", false)]);
        var recentHr = await ValuesAsync(SampleRelations.Flat, "department", conditions: [new NumberRangeCondition("year", 2024, null), new MatchAnyCondition("department", Hr)]);
        var firstPages = await ValuesAsync(SampleRelations.Flat, "department", conditions: [new NumberRangeCondition("page", 1, 1)]);

        Assert.DoesNotContain("legal", notConfidential.Values.Select(v => v.Value));
        Assert.Equal(["hr=3"], Rendered(recentHr));
        Assert.Equal(["hr=3", "facilities=1", "finance=1", "it=1", "legal=1"], Rendered(firstPages));
    }

    [Fact] // AC-47
    public async Task TheCountIsExactUnderTheConditionsOfASearch()
    {
        var (lookups, connection) = await OpenAsync(SampleRelations.Flat);

        Assert.Equal(10, await lookups.CountAsync(connection, [], Ct));
        Assert.Equal(4, await lookups.CountAsync(connection, [new MatchAnyCondition("department", Hr)], Ct));
        Assert.Equal(2, await lookups.CountAsync(connection, [new DocumentIdsCondition("document_id", ["leave"])], Ct));
        Assert.Equal(0, await lookups.CountAsync(connection, [new MatchAnyCondition("department", ["x'); DROP TABLE sample_flat; --"])], Ct));
        Assert.Equal(10, await lookups.CountAsync(connection, [], Ct));
    }

    [Theory] // AC-46
    [InlineData("pk", "id bigint PRIMARY KEY, grp text")]
    [InlineData("unique_only", "id bigint NOT NULL, grp text, UNIQUE (id)")]
    [InlineData("nothing", "id bigint, grp text")]
    public async Task APagedScanVisitsEveryRowOnceWhateverTheKeyOfTheRelation(string table, string columns)
    {
        var schema = await pgvector.NewSchemaAsync();
        await pgvector.ExecuteAsync(
            $"""
            CREATE TABLE {schema}.{table} ({columns}, text text, embedding vector(3));
            INSERT INTO {schema}.{table} SELECT i, 'group ' || (i % 3), 'row ' || i, '[1,0,0]' FROM generate_series(1, 25) i;
            """);

        var visited = await ScanAllAsync(table, schema, pageSize: 10, expectedPages: 3);

        Assert.Equal(25, visited.Count);
        Assert.Equal(25, visited.Distinct().Count());
    }

    [Fact] // AC-46
    public async Task AViewWithNoUniqueColumnIsScannedByOffset()
    {
        var schema = await pgvector.NewSchemaAsync();
        await pgvector.ExecuteAsync(
            $"""
            CREATE TABLE {schema}.base (id bigint PRIMARY KEY, grp text, text text, embedding vector(3));
            INSERT INTO {schema}.base SELECT i, 'group ' || (i % 3), 'row ' || i, '[1,0,0]' FROM generate_series(1, 25) i;
            CREATE VIEW {schema}.v AS SELECT id, grp, text, embedding FROM {schema}.base;
            """);

        var visited = await ScanAllAsync("v", schema, pageSize: 10, expectedPages: 3);

        Assert.Equal(25, visited.Count);
        Assert.Equal(25, visited.Distinct().Count());
    }

    [Fact] // AC-46
    public async Task AScanReturnsOnlyTheRowsOfTheConditionsAndThePathsAsked()
    {
        var (lookups, connection) = await OpenAsync(SampleRelations.Flat);

        var page = await lookups.ScanAsync(connection, new RecordQuery([new MatchAnyCondition("department", Hr)], ["document_id", "year"], 10, null), Ct);

        Assert.Equal(4, page.Rows.Count);
        Assert.Null(page.Cursor);
        Assert.All(page.Rows, row => Assert.Equal(2, row.Count));
    }

    [Fact] // AC-46
    public async Task ACursorThatIsNotOursIsRefusedWithoutEchoingIt()
    {
        var (lookups, connection) = await OpenAsync(SampleRelations.Flat);

        var failure = await Assert.ThrowsAsync<StoreException>(() => lookups.ScanAsync(connection, new RecordQuery([], ["document_id"], 5, "not a cursor!"), Ct));

        Assert.DoesNotContain("not a cursor", failure.Message, StringComparison.Ordinal);
    }

    [Fact] // AC-48
    public async Task TheCheckNeedsNoIndexButNamesAColumnThatIsGone()
    {
        var (lookups, connection) = await OpenAsync(SampleRelations.Unindexed);

        Assert.Null(await lookups.CheckAsync(connection, ["department", "document_id"], Ct));
        var message = await lookups.CheckAsync(connection, ["department", "no_such_column", "meta.year"], Ct);
        Assert.Contains("'meta.year', 'no_such_column'", message, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // AC-48
    public async Task ARelationThatIsGoneFailsAsNotFoundAndTheWarningAsksForAnIndex()
    {
        var (lookups, connection) = await OpenAsync("not_there");

        Assert.Contains("does not exist", await lookups.CheckAsync(connection, ["x"], Ct), StringComparison.Ordinal);
        var failure = await Assert.ThrowsAsync<StoreException>(() => lookups.CountAsync(connection, [], Ct));
        Assert.Equal(StoreFailure.NotFound, failure.Failure);
        var warning = lookups.UnindexedWarning("department", "listable field");
        Assert.Equal("knowledge_base.lookup_field_unindexed", warning.Code);
        Assert.Equal("department", warning.Path);
    }

    [Fact] // AC-47
    public async Task ACallThatIsCancelledStopsTheStatementOnTheServer()
    {
        var schema = await pgvector.NewSchemaAsync();
        await pgvector.ExecuteAsync(
            $"""
            CREATE VIEW {schema}.slow AS SELECT pg_sleep(20)::text AS s, 'x' AS text, '[1,0,0]'::vector(3) AS embedding;
            """);
        var (lookups, connection) = await OpenAsync("slow", schema);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(700));
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookups.CountAsync(connection, [], cancel.Token));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"The call took {clock.Elapsed}.");
        await using var dataSource = NpgsqlDataSource.Create(await pgvector.GetConnectionStringAsync());
        await using var probe = dataSource.CreateCommand("SELECT count(*) FROM pg_stat_activity WHERE state = 'active' AND query LIKE '%slow%' AND pid <> pg_backend_pid()");
        var running = 1L;
        for (var attempt = 0; attempt < 20 && running > 0; attempt++)
        {
            running = (long)(await probe.ExecuteScalarAsync(Ct))!;
            if (running > 0)
            {
                await Task.Delay(250, Ct);
            }
        }

        Assert.Equal(0, running);
    }

    [Fact] // spec 008 A2-2: ten minutes, not the 30 seconds Npgsql allows a command by default
    public async Task ALookupThatTakesLongerThanThirtySecondsIsNotCutByTheDriver()
    {
        var schema = await pgvector.NewSchemaAsync();
        await pgvector.ExecuteAsync(
            $"""
            CREATE VIEW {schema}.slow AS SELECT pg_sleep(32)::text AS s, 'x' AS text, '[1,0,0]'::vector(3) AS embedding;
            """);
        var (lookups, connection) = await OpenAsync("slow", schema);

        var count = await lookups.CountAsync(connection, [], Ct);

        Assert.Equal(1, count);
    }

    [Fact] // a data error is not a dimension error
    public async Task ABadValueInAJsonKeyIsNotReportedAsADimensionMismatch()
    {
        var schema = await pgvector.NewSchemaAsync();
        await pgvector.ExecuteAsync(
            $$"""
            CREATE TABLE {{schema}}.dates (id bigint PRIMARY KEY, text text, meta jsonb, embedding vector(3));
            INSERT INTO {{schema}}.dates VALUES (1, 't', '{"day": "2024-02-30"}', '[1,0,0]');
            """);
        var (lookups, connection) = await OpenAsync("dates", schema);

        var failure = await Assert.ThrowsAsync<StoreException>(() => lookups.CountAsync(connection, [new DateRangeCondition("meta.day", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), null)], Ct));

        Assert.NotEqual(StoreFailure.Rejected, failure.Failure);
    }

    [Fact] // a cursor that decodes to nothing is refused
    public async Task ACursorThatDecodesToNullIsRefusedAndDoesNotRestartTheScan()
    {
        var (lookups, connection) = await OpenAsync(SampleRelations.Flat);

        var failure = await Assert.ThrowsAsync<StoreException>(() => lookups.ScanAsync(connection, new RecordQuery([], ["document_id"], 5, "bnVsbA"), Ct));

        Assert.Equal(StoreFailure.Unexpected, failure.Failure);
    }

    [Fact] // a json column listed as a whole is refused by the check
    public async Task AJsonColumnWithoutAKeyIsRefusedByTheCheckWithAMessageThatSaysWhatToMap()
    {
        var (lookups, connection) = await OpenAsync(SampleRelations.LangChain);

        var message = await lookups.CheckAsync(connection, ["cmetadata"], Ct);

        Assert.Contains("cmetadata.", message, StringComparison.Ordinal);
        Assert.Null(await lookups.CheckAsync(connection, ["cmetadata.department"], Ct));
    }

    [Fact] // rows that look alike in the paths asked for still page in the same order
    public void AnOffsetScanOrdersByEveryOrderableColumnAfterThePathsAsked()
    {
        PgVectorColumn[] columns =
        [
            new("id", "int8", "b", "bigint", -1),
            new("grp", "text", "b", "text", -1),
            new("raw", "json", "b", "json", -1),
            new("embedding", "vector", "b", "vector(3)", 3),
        ];
        var relation = new PgVectorRelation("s", "v", "v", -1, columns, []);

        var statement = PgVectorScanStatement.Build(relation, new PgVectorOrder(PgVectorOrderKind.Offset, []), new RecordQuery([], ["grp"], 10, null));

        Assert.Contains("ORDER BY c0, \"id\", \"grp\"", statement.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"raw\"", statement.Sql.Split("ORDER BY")[1], StringComparison.Ordinal);
    }

    private async Task<List<string>> ScanAllAsync(string relation, string schema, int pageSize, int expectedPages)
    {
        var (lookups, connection) = await OpenAsync(relation, schema);
        List<string> visited = [];
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await lookups.ScanAsync(connection, new RecordQuery([], ["text", "grp"], pageSize, cursor), Ct);
            visited.AddRange(page.Rows.Select(row => row["text"]!.GetValue<string>()));
            cursor = page.Cursor;
            pages++;
        }
        while (cursor is not null && pages < 20);

        Assert.Equal(expectedPages, pages);
        return visited;
    }
}
