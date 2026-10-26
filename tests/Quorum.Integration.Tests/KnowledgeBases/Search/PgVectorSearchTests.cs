using System.Net.Http.Json;
using Quorum.Integration.Tests.Fixtures;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBases.Search;

/// <summary>Spec 008 D-14: a rag search over a PgVector knowledge base, through the engine the Agents module calls.</summary>
public sealed class PgVectorSearchTests(PostgresFixture postgres, PgVectorFixture pgvector, QdrantFixture qdrant)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string LeaveText => SampleCollections.CreateFlat().Points.Single(p => p.Id == 1).EmbeddingText;

    private sealed record Setup(QuorumApp App, HttpClient Admin, Guid TenantId, Guid AssistantId, PgVectorEndpoint Endpoint) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Admin.Dispose();
            await App.DisposeAsync();
        }
    }

    private async Task<Setup> StartAsync()
    {
        var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        return new Setup(app, admin, await TenantScope.TenantIdAsync(app), await SearchApi.AssistantIdAsync(app), await pgvector.GetEndpointAsync());
    }

    private static async Task<Guid> RegisterAsync(Setup setup, string schema, string relation, string name = "policies", object? mapping = null, object[]? createFields = null)
    {
        var body = new
        {
            name,
            description = "Company policies.",
            kind = "PgVector",
            connection = PgVectorApi.Fields(setup.Endpoint, schema, relation),
            embeddingModel = KnowledgeBaseApi.EmbeddingModel,
            queryPrefix = (string?)null,
            mapping = mapping ?? KnowledgeBaseApi.FlatMapping,
            scope = new { kind = "Tenant" },
            createFields = createFields ?? KnowledgeBaseApi.FlatCreateFields,
        };
        using var response = await setup.Admin.PostAsJsonAsync(KnowledgeBaseApi.All, body, Ct);
        Assert.Equal(global::System.Net.HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<KnowledgeBaseApi.RegisteredBody>(Ct))!.Id;
    }

    [Fact] // AC-21, AC-23
    public async Task TheQueryIsEmbeddedAndTheRelationSearchedWithTheFiltersOfTheModel()
    {
        await using var setup = await StartAsync();
        var kb = await RegisterAsync(setup, await pgvector.GetSharedSchemaAsync(), SampleRelations.Flat);

        var plain = await SearchApi.RunAsync(setup.App, setup.TenantId, SearchApi.Request(setup.TenantId, setup.AssistantId, LeaveText, [kb]));
        var filtered = await SearchApi.RunAsync(setup.App, setup.TenantId, SearchApi.Request(setup.TenantId, setup.AssistantId, LeaveText, [kb], new { year_min = 2025 }, topK: 20));

        Assert.True(plain.IsSuccess);
        var best = plain.Value.Chunks[0];
        Assert.Equal(("policies", "leave", "Annual leave policy", "1"), (best.KnowledgeBase, best.DocumentId, best.Title, best.Position));
        Assert.StartsWith("https://", best.Url, StringComparison.Ordinal);
        Assert.Equal(5, plain.Value.Chunks.Count);
        Assert.Equal(plain.Value.Chunks.OrderByDescending(c => c.Score), plain.Value.Chunks);
        Assert.True(filtered.IsSuccess);
        Assert.NotEmpty(filtered.Value.Chunks);
        Assert.DoesNotContain(filtered.Value.Chunks, c => c.DocumentId == "leave");
    }

    [Fact] // AC-28
    public async Task ADroppedRelationFailsTheKnowledgeBaseWithoutTheDatabasesWordsAndItComesBack()
    {
        await using var setup = await StartAsync();
        var schema = await pgvector.NewSchemaAsync();
        var kb = await RegisterAsync(setup, schema, SampleRelations.Flat);
        Assert.True((await SearchApi.RunAsync(setup.App, setup.TenantId, SearchApi.Request(setup.TenantId, setup.AssistantId, LeaveText, [kb]))).IsSuccess);

        await pgvector.ExecuteAsync($"ALTER TABLE {schema}.{SampleRelations.Flat} RENAME TO moved");
        var failed = await SearchApi.RunAsync(setup.App, setup.TenantId, SearchApi.Request(setup.TenantId, setup.AssistantId, LeaveText, [kb]));

        Assert.False(failed.IsSuccess);
        Assert.Equal("knowledge_base.unavailable", failed.Error.Code);
        Assert.Equal("Unavailable", (await KnowledgeBaseApi.GetAsync(setup.Admin, kb)).Status);

        await pgvector.ExecuteAsync($"ALTER TABLE {schema}.moved RENAME TO {SampleRelations.Flat}");
        var back = await SearchApi.RunAsync(setup.App, setup.TenantId, SearchApi.Request(setup.TenantId, setup.AssistantId, LeaveText, [kb]));
        Assert.True(back.IsSuccess);
        Assert.Equal("Available", (await KnowledgeBaseApi.GetAsync(setup.Admin, kb)).Status);
    }

    [Fact] // AC-28
    public async Task TheErrorTheModelReadsNamesNeitherTheHostNorTheSql()
    {
        await using var setup = await StartAsync();
        var schema = await pgvector.NewSchemaAsync();
        var bad = await RegisterAsync(setup, schema, "not_there", mapping: KnowledgeBaseApi.TextOnly(), createFields: []);
        var good = await RegisterAsync(setup, schema, SampleRelations.Flat, "good");

        var result = await SearchApi.RunAsync(setup.App, setup.TenantId, SearchApi.Request(setup.TenantId, setup.AssistantId, LeaveText, [bad, good]));

        Assert.True(result.IsSuccess);
        Assert.All(result.Value.Chunks, chunk => Assert.Equal("good", chunk.KnowledgeBase));
        var failure = Assert.Single(result.Value.FailedKnowledgeBases);
        Assert.Equal("knowledge_base.pgvector_relation_not_found", failure.Code);
        Assert.DoesNotContain(setup.Endpoint.Host, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not_there", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // AC-29
    public async Task AQdrantAndAPgVectorKnowledgeBaseAreInterleavedAndEachSourceNamesItsOwn()
    {
        await using var setup = await StartAsync();
        var collection = await qdrant.SeedSharedAsync(SampleCollections.CreateFlat);
        var url = (await qdrant.GetUrlAsync()).ToString();
        var fromQdrant = (await KnowledgeBaseApi.RegisterOkAsync(setup.Admin, url, collection, "from-qdrant")).Id;
        var fromPgVector = await RegisterAsync(setup, await pgvector.GetSharedSchemaAsync(), SampleRelations.Flat, "from-pgvector");

        var result = await SearchApi.RunAsync(setup.App, setup.TenantId, SearchApi.Request(setup.TenantId, setup.AssistantId, LeaveText, [fromQdrant, fromPgVector], topK: 4));

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.FailedKnowledgeBases);
        Assert.Equal(["from-pgvector", "from-qdrant"], result.Value.Chunks.Select(c => c.KnowledgeBase).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact] // AC-30
    public async Task RowsWithoutUsableTextAreSkippedAndCountedAndWhenAllAreTheResultSaysSo()
    {
        await using var setup = await StartAsync();
        var schema = await pgvector.NewSchemaAsync();
        await pgvector.ExecuteAsync(
            $"""
            CREATE TABLE {schema}.ragged (id bigint PRIMARY KEY, text text, embedding vector(64));
            INSERT INTO {schema}.ragged VALUES
                (1, 'Contractors submit timesheets every Friday.', array_fill(0.1::real, ARRAY[64])::vector),
                (2, NULL, array_fill(0.1::real, ARRAY[64])::vector),
                (3, '   ', array_fill(0.1::real, ARRAY[64])::vector);
            CREATE TABLE {schema}.empty_text (id bigint PRIMARY KEY, text text, embedding vector(64));
            INSERT INTO {schema}.empty_text VALUES (1, NULL, array_fill(0.1::real, ARRAY[64])::vector);
            """);
        var some = await RegisterAsync(setup, schema, "ragged", "some", KnowledgeBaseApi.TextOnly(), []);
        var none = await RegisterAsync(setup, schema, "empty_text", "none", KnowledgeBaseApi.TextOnly(), []);

        var partly = await SearchApi.RunAsync(setup.App, setup.TenantId, SearchApi.Request(setup.TenantId, setup.AssistantId, "Contractors", [some]));
        var all = await SearchApi.RunAsync(setup.App, setup.TenantId, SearchApi.Request(setup.TenantId, setup.AssistantId, "Contractors", [none]));

        Assert.True(partly.IsSuccess);
        Assert.Equal(2, partly.Value.UnusableChunks);
        Assert.Equal(["Contractors submit timesheets every Friday."], partly.Value.Chunks.Select(c => c.Text));
        Assert.True(all.IsSuccess);
        Assert.Empty(all.Value.Chunks);
        Assert.Equal(1, all.Value.UnusableChunks);
    }

    [Fact] // AC-27
    public async Task ASearchWritesNothing()
    {
        await using var setup = await StartAsync();
        var schema = await pgvector.NewSchemaAsync();
        var kb = await RegisterAsync(setup, schema, SampleRelations.Flat);
        var before = await ChangesAsync(schema);

        Assert.True((await SearchApi.RunAsync(setup.App, setup.TenantId, SearchApi.Request(setup.TenantId, setup.AssistantId, LeaveText, [kb]))).IsSuccess);

        Assert.Equal(before, await ChangesAsync(schema));
    }

    // Inserts, updates and deletes the database counted for the relation: a search leaves them where they were.
    private async Task<long> ChangesAsync(string schema)
    {
        await using var dataSource = Npgsql.NpgsqlDataSource.Create(await pgvector.GetConnectionStringAsync());
        await using var command = dataSource.CreateCommand(
            "SELECT COALESCE(n_tup_ins + n_tup_upd + n_tup_del, 0) FROM pg_stat_user_tables WHERE schemaname = @schema AND relname = @name");
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("name", SampleRelations.Flat);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct) ?? 0L, global::System.Globalization.CultureInfo.InvariantCulture);
    }
}
