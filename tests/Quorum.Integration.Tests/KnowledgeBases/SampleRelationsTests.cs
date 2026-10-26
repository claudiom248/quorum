using System.Globalization;
using Quorum.Integration.Tests.Fixtures;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBases;

/// <summary>AC-36 (the generator part): the sample relations of spec 008 D-18 exist in a pgvector database and can be queried with the deterministic embedding.</summary>
public sealed class SampleRelationsTests(PgVectorFixture pgvector)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var dataSource = PgVectorSeeder.CreateDataSource(await pgvector.GetConnectionStringAsync());
        await using var command = dataSource.CreateCommand(sql);
        var value = await command.ExecuteScalarAsync(Ct);
        return value is null or DBNull ? default : (T)value;
    }

    [Fact]
    public async Task EveryCaseOfTheSpecIsSeeded()
    {
        var schema = await pgvector.GetSharedSchemaAsync();

        Assert.Equal(10L, await ScalarAsync<long>($"SELECT count(*) FROM {schema}.{SampleRelations.Flat}"));
        Assert.Equal(10L, await ScalarAsync<long>($"SELECT count(*) FROM {schema}.{SampleRelations.LangChain} WHERE cmetadata ->> 'department' IS NOT NULL"));
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM {schema}.{SampleRelations.Empty}"));
        Assert.Equal(9L, await ScalarAsync<long>($"SELECT count(*) FROM {schema}.{SampleRelations.View}"));
        Assert.Equal(10L, await ScalarAsync<long>($"SELECT count(*) FROM {schema}.\"{SampleRelations.MixedCase}\""));
        Assert.Equal(10L, await ScalarAsync<long>($"SELECT count(*) FROM {schema}.{SampleRelations.DottedColumn} WHERE \"meta.year\" > 0"));
    }

    [Theory]
    [InlineData(SampleRelations.Flat, "embedding", "vector(64)")]
    [InlineData(SampleRelations.TwoVectors, SampleRelations.SummaryColumn, "vector(32)")]
    [InlineData(SampleRelations.HalfVector, "embedding", "halfvec(64)")]
    [InlineData(SampleRelations.UnspecifiedDimensions, "embedding", "vector")]
    [InlineData(SampleRelations.OtherVectorSize, "embedding", "vector(128)")]
    public async Task TheVectorColumnsHaveTheTypeOfTheirCase(string relation, string column, string type)
    {
        var schema = await pgvector.GetSharedSchemaAsync();

        var actual = await ScalarAsync<string>(
            $"""
            SELECT format_type(a.atttypid, a.atttypmod) FROM pg_attribute a
            WHERE a.attrelid = '{schema}.{relation}'::regclass AND a.attname = '{column}'
            """);

        Assert.Equal(type, actual);
    }

    [Theory]
    [InlineData(SampleRelations.Flat, "hnsw", "vector_cosine_ops")]
    [InlineData(SampleRelations.IvfFlatL2, "ivfflat", "vector_l2_ops")]
    [InlineData(SampleRelations.InnerProduct, "hnsw", "vector_ip_ops")]
    [InlineData(SampleRelations.HalfVector, "hnsw", "halfvec_cosine_ops")]
    public async Task TheVectorIndexesUseTheOperatorClassOfTheirCase(string relation, string method, string operatorClass)
    {
        var schema = await pgvector.GetSharedSchemaAsync();

        var actual = await ScalarAsync<string>(
            $"""
            SELECT am.amname || '/' || oc.opcname FROM pg_index i
            JOIN pg_class ic ON ic.oid = i.indexrelid JOIN pg_am am ON am.oid = ic.relam
            JOIN pg_opclass oc ON oc.oid = i.indclass[0]
            WHERE i.indrelid = '{schema}.{relation}'::regclass AND am.amname IN ('hnsw', 'ivfflat')
            """);

        Assert.Equal($"{method}/{operatorClass}", actual);
    }

    [Fact]
    public async Task TheUnindexedRelationHasNoIndexBesidesItsKey()
    {
        var schema = await pgvector.GetSharedSchemaAsync();

        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM pg_index WHERE indrelid = '{schema}.{SampleRelations.Unindexed}'::regclass"));
    }

    [Fact]
    public async Task TheDeterministicEmbeddingRanksTheMatchingPolicyFirst()
    {
        var schema = await pgvector.GetSharedSchemaAsync();
        var query = string.Join(",", DeterministicEmbedding.Embed("how many days of annual leave do employees get").Select(x => x.ToString("R", CultureInfo.InvariantCulture)));

        var title = await ScalarAsync<string>($"SELECT title FROM {schema}.{SampleRelations.Flat} ORDER BY embedding <=> '[{query}]'::vector LIMIT 1");

        Assert.Equal("Annual leave policy", title);
    }

    [Fact]
    public async Task SeedingAgainRecreatesTheRelations()
    {
        var schema = await pgvector.NewSchemaAsync();
        await pgvector.SeedAsync(schema);

        Assert.Equal(10L, await ScalarAsync<long>($"SELECT count(*) FROM {schema}.{SampleRelations.Flat}"));
    }
}
