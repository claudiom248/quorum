using System.Net;
using Quorum.Integration.Tests.Fixtures;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBases;

/// <summary>Spec 008 D-5: the relations of a database that the administrator picks from.</summary>
public sealed class PgVectorRelationsTests(PostgresFixture postgres, PgVectorFixture pgvector)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-7
    public async Task EveryRelationWithAVectorColumnTheUserCanReadIsListed()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var schema = await pgvector.NewSchemaAsync();
        await pgvector.ExecuteAsync($"CREATE MATERIALIZED VIEW {schema}.sample_materialized AS SELECT id, embedding FROM {schema}.{SampleRelations.Flat}");
        await pgvector.ExecuteAsync($"CREATE TABLE {schema}.no_vector (id int PRIMARY KEY, text text)");
        await pgvector.ExecuteAsync(
            $"""
            CREATE TABLE {schema}.partitioned (id bigint, embedding vector(3)) PARTITION BY RANGE (id);
            CREATE TABLE {schema}.partitioned_part PARTITION OF {schema}.partitioned FOR VALUES FROM (0) TO (100);
            """);

        var report = await PgVectorApi.RelationsOkAsync(admin, PgVectorApi.Fields(await pgvector.GetEndpointAsync()), filter: schema);

        Assert.True(report.Reachable);
        Assert.False(report.Truncated);
        Assert.Empty(report.Warnings);
        var relations = report.Relations.ToDictionary(r => r.Name);
        Assert.DoesNotContain("no_vector", relations.Keys);
        Assert.DoesNotContain("partitioned_part", relations.Keys);
        Assert.Equal("table", relations[SampleRelations.Flat].Type);
        Assert.Equal("view", relations[SampleRelations.View].Type);
        Assert.Equal("materialized view", relations["sample_materialized"].Type);
        Assert.Equal("partitioned table", relations["partitioned"].Type);
        Assert.Equal(schema, relations[SampleRelations.Flat].Schema);
        Assert.Equal(10, relations[SampleRelations.Flat].ApproximateRows);
        Assert.Null(relations[SampleRelations.View].ApproximateRows);
        Assert.Equal([new PgVectorApi.VectorColumnBody("embedding", "vector(64)", 64)], relations[SampleRelations.Flat].VectorColumns);
        Assert.Equal(
            [new PgVectorApi.VectorColumnBody("embedding", "vector(64)", 64), new PgVectorApi.VectorColumnBody(SampleRelations.SummaryColumn, "vector(32)", 32)],
            relations[SampleRelations.TwoVectors].VectorColumns);
        Assert.Equal([new PgVectorApi.VectorColumnBody("embedding", "halfvec(64)", 64)], relations[SampleRelations.HalfVector].VectorColumns);
        Assert.Equal([new PgVectorApi.VectorColumnBody("embedding", "vector", null)], relations[SampleRelations.UnspecifiedDimensions].VectorColumns);
    }

    [Fact] // AC-7
    public async Task ARelationTheUserCannotReadIsNotListed()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var schema = await pgvector.NewSchemaAsync();
        var role = $"reader_{Guid.NewGuid():N}"[..16];
        await pgvector.ExecuteAsync($"CREATE ROLE {role} LOGIN PASSWORD 'reader-pw'");
        await pgvector.ExecuteAsync($"GRANT USAGE ON SCHEMA {schema} TO {role}");
        await pgvector.ExecuteAsync($"GRANT SELECT ON {schema}.{SampleRelations.Flat} TO {role}");

        var connection = PgVectorApi.Fields(await pgvector.GetEndpointAsync(), username: role, password: "reader-pw");
        var report = await PgVectorApi.RelationsOkAsync(admin, connection, filter: schema);

        Assert.Equal([SampleRelations.Flat], report.Relations.Select(r => r.Name));
    }

    [Fact] // AC-7
    public async Task TheListIsFilteredByName()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var schema = await pgvector.NewSchemaAsync();

        var report = await PgVectorApi.RelationsOkAsync(admin, PgVectorApi.Fields(await pgvector.GetEndpointAsync()), filter: $"{schema}.sample_FLAT");

        Assert.Equal([SampleRelations.Flat], report.Relations.Select(r => r.Name));
    }

    [Fact] // AC-7
    public async Task AFilterWithWildcardsIsTakenAsText()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var schema = await pgvector.NewSchemaAsync();

        var percent = await PgVectorApi.RelationsOkAsync(admin, PgVectorApi.Fields(await pgvector.GetEndpointAsync()), filter: $"{schema}.%");
        var underscore = await PgVectorApi.RelationsOkAsync(admin, PgVectorApi.Fields(await pgvector.GetEndpointAsync()), filter: $"{schema}.sample_f_at");

        Assert.Empty(percent.Relations);
        Assert.Empty(underscore.Relations);
    }

    [Fact] // AC-7
    public async Task AtMostTwoHundredRelationsAreReturnedAndTheRestIsSaidToBeCut()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var schema = $"many_{Guid.NewGuid():N}"[..12];
        await pgvector.ExecuteAsync(
            $"""
            CREATE EXTENSION IF NOT EXISTS vector;
            CREATE SCHEMA {schema};
            DO $$ BEGIN FOR i IN 1..205 LOOP EXECUTE format('CREATE TABLE {schema}.t%s (embedding vector(3))', i); END LOOP; END $$;
            """);

        var report = await PgVectorApi.RelationsOkAsync(admin, PgVectorApi.Fields(await pgvector.GetEndpointAsync()), filter: schema);

        Assert.Equal(200, report.Relations.Count);
        Assert.True(report.Truncated);
    }

    [Fact] // AC-3
    public async Task AConnectionStringReachesTheDatabaseToo()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var schema = await pgvector.NewSchemaAsync();

        var report = await PgVectorApi.RelationsOkAsync(admin, PgVectorApi.ConnectionString(await pgvector.GetEndpointAsync()), filter: schema);

        Assert.Contains(SampleRelations.Flat, report.Relations.Select(r => r.Name));
    }

    [Theory] // AC-3
    [InlineData("both")]
    [InlineData("neither")]
    [InlineData("unparsable")]
    [InlineData("port")]
    [InlineData("sslmode")]
    public async Task ABadConnectionIsRefusedBeforeAnythingIsSent(string problem)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var endpoint = await pgvector.GetEndpointAsync();
        object connection = problem switch
        {
            "both" => PgVectorApi.Fields(endpoint).Concat(PgVectorApi.ConnectionString(endpoint)).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value),
            "neither" => new Dictionary<string, object?> { ["schema"] = "public" },
            "unparsable" => new Dictionary<string, object?> { ["connectionString"] = "this is not; a connection string=" },
            "port" => PgVectorApi.Fields(endpoint).Concat(new Dictionary<string, object?> { ["port"] = 0 }).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value),
            _ => PgVectorApi.Fields(endpoint).Concat(new Dictionary<string, object?> { ["sslMode"] = "1" }).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value),
        };

        using var response = await PgVectorApi.PostRelationsAsync(admin, connection);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("knowledge_base.invalid_connection", await PgVectorApi.ProblemCodeAsync(response));
        Assert.DoesNotContain(endpoint.Password, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact] // AC-7
    public async Task AKindThatCannotListItsSourcesIsToldSo()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        using var response = await PgVectorApi.PostRelationsAsync(admin, new { url = "http://qdrant:6334", collection = "x" }, kind: "Qdrant");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("knowledge_base.unsupported_operation", await PgVectorApi.ProblemCodeAsync(response));
    }

    [Fact] // AC-2
    public async Task AnUnknownKindIsRefusedNamingTheSupportedOnes()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        using var response = await PgVectorApi.PostRelationsAsync(admin, new { }, kind: "Pinecone");
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("knowledge_base.unsupported_kind", await PgVectorApi.ProblemCodeAsync(response));
        Assert.Contains("PgVector", body, StringComparison.Ordinal);
        Assert.Contains("Qdrant", body, StringComparison.Ordinal);
    }

    [Fact] // AC-17
    public async Task WrongCredentialsGiveAWarningWithoutTheirDetails()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var endpoint = await pgvector.GetEndpointAsync();

        var report = await PgVectorApi.RelationsOkAsync(admin, PgVectorApi.Fields(endpoint, password: "not-the-password"));

        Assert.False(report.Reachable);
        Assert.Empty(report.Relations);
        var warning = Assert.Single(report.Warnings);
        Assert.Equal("knowledge_base.pgvector_unauthorized", warning.Code);
        Assert.DoesNotContain(endpoint.Host, warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not-the-password", warning.Message, StringComparison.Ordinal);
    }

    [Fact] // AC-17
    public async Task ADatabaseThatIsNotThereIsReportedWithoutItsName()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        var report = await PgVectorApi.RelationsOkAsync(admin, PgVectorApi.Fields(await pgvector.GetEndpointAsync(), database: "no_such_database"));

        Assert.False(report.Reachable);
        Assert.Equal(["knowledge_base.pgvector_relation_not_found"], report.WarningCodes);
    }

    [Fact] // AC-17
    public async Task AServerThatDoesNotAnswerIsUnreachable()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var endpoint = await pgvector.GetEndpointAsync() with { Port = 1 };

        var report = await PgVectorApi.RelationsOkAsync(admin, PgVectorApi.Fields(endpoint));

        Assert.False(report.Reachable);
        Assert.Equal(["knowledge_base.pgvector_unreachable"], report.WarningCodes);
    }

    [Fact] // AC-17
    public async Task ADatabaseWithoutTheExtensionSaysSo()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var database = $"noext_{Guid.NewGuid():N}"[..14];
        await pgvector.ExecuteAsync($"CREATE DATABASE {database}");

        var report = await PgVectorApi.RelationsOkAsync(admin, PgVectorApi.Fields(await pgvector.GetEndpointAsync(), database: database));

        Assert.True(report.Reachable);
        Assert.Empty(report.Relations);
        Assert.Equal(["knowledge_base.pgvector_extension_missing"], report.WarningCodes);
    }

    [Fact] // AC-31
    public async Task ListingRelationsNeedsThePermissionAndAnonymousCallersAreRejected()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        using var member = await app.CreateMemberClientAsync();
        using var anonymous = app.CreateUiClient();
        var connection = PgVectorApi.Fields(await pgvector.GetEndpointAsync());

        using var asMember = await PgVectorApi.PostRelationsAsync(member, connection);
        using var asAnonymous = await PgVectorApi.PostRelationsAsync(anonymous, connection);
        using var asAdmin = await PgVectorApi.PostRelationsAsync(admin, connection);

        Assert.Equal(HttpStatusCode.Forbidden, asMember.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, asAnonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, asAdmin.StatusCode);
    }
}
