using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quorum.Integration.Tests.Fixtures;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBases;

/// <summary>Spec 008 D-13, D-16: registering, updating and verifying a PgVector knowledge base.</summary>
public sealed class PgVectorRegistryTests(PostgresFixture postgres, PgVectorFixture pgvector)
{
    private const string ReaderPassword = "reader-pw-7c1e";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Body(object connection, string name = "policies", object? mapping = null, object[]? createFields = null) => new
    {
        name,
        description = "Company policies.",
        kind = "PgVector",
        connection,
        embeddingModel = KnowledgeBaseApi.EmbeddingModel,
        queryPrefix = (string?)null,
        mapping = mapping ?? KnowledgeBaseApi.FlatMapping,
        scope = new { kind = "Tenant" },
        createFields = createFields ?? KnowledgeBaseApi.FlatCreateFields,
    };

    private async Task<(QuorumApp App, HttpClient Admin)> StartAsync()
    {
        var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        return (app, admin);
    }

    /// <summary>A role that can read the schema and nothing else, the way a data owner would give Quorum access.</summary>
    private async Task<string> ReaderAsync(string schema)
    {
        var role = $"reader_{Guid.NewGuid():N}"[..16];
        await pgvector.ExecuteAsync($"CREATE ROLE {role} LOGIN PASSWORD '{ReaderPassword}'");
        await pgvector.ExecuteAsync($"GRANT USAGE ON SCHEMA {schema} TO {role}");
        await pgvector.ExecuteAsync($"GRANT SELECT ON ALL TABLES IN SCHEMA {schema} TO {role}");
        return role;
    }

    private static async Task<KnowledgeBaseApi.RegisteredBody> RegisterAsync(HttpClient admin, object body, HttpStatusCode expected = HttpStatusCode.Created)
    {
        using var response = await admin.PostAsJsonAsync(KnowledgeBaseApi.All, body, Ct);
        Assert.Equal(expected, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<KnowledgeBaseApi.RegisteredBody>(Ct))!;
    }

    private static async Task<string?> RegisterRefusedAsync(HttpClient admin, object body)
    {
        using var response = await admin.PostAsJsonAsync(KnowledgeBaseApi.All, body, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return await KnowledgeBaseApi.ProblemCodeAsync(response);
    }

    private static async Task<string?> UpdateRefusedAsync(HttpClient admin, Guid id, object body)
    {
        using var response = await admin.PutAsJsonAsync(KnowledgeBaseApi.One(id), body, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return await KnowledgeBaseApi.ProblemCodeAsync(response);
    }

    [Fact] // AC-16, AC-4
    public async Task AReadyRelationOnAReadOnlyRoleIsAvailableWithoutWarningsAndTheSecretIsNeverReturned()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        var schema = await pgvector.NewSchemaAsync();
        var role = await ReaderAsync(schema);
        var connection = PgVectorApi.Fields(await pgvector.GetEndpointAsync(), schema, SampleRelations.Flat, username: role, password: ReaderPassword);

        var registered = await RegisterAsync(admin, Body(connection));

        Assert.True(registered.Status == "Available", string.Join(" | ", registered.Warnings.Select(w => w.Code + ": " + w.Message)));
        Assert.Empty(registered.Warnings);
        var raw = await KnowledgeBaseApi.GetRawAsync(admin, registered.Id);
        Assert.DoesNotContain(ReaderPassword, raw, StringComparison.Ordinal);
        using var detail = JsonDocument.Parse(raw);
        var described = detail.RootElement.GetProperty("connection");
        Assert.True(described.GetProperty("hasPassword").GetBoolean());
        Assert.False(described.GetProperty("hasConnectionString").GetBoolean());
        Assert.Equal(SampleRelations.Flat, described.GetProperty("table").GetString());
    }

    [Fact] // AC-4
    public async Task AConnectionStringIsNeverReturnedEither()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        var schema = await pgvector.GetSharedSchemaAsync();
        var connection = PgVectorApi.ConnectionString(await pgvector.GetEndpointAsync(), schema, SampleRelations.Flat);

        var registered = await RegisterAsync(admin, Body(connection));

        var raw = await KnowledgeBaseApi.GetRawAsync(admin, registered.Id);
        Assert.DoesNotContain("Password=", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(PgVectorApiPassword(await pgvector.GetEndpointAsync()), raw, StringComparison.Ordinal);
        using var detail = JsonDocument.Parse(raw);
        Assert.True(detail.RootElement.GetProperty("connection").GetProperty("hasConnectionString").GetBoolean());
    }

    private static string PgVectorApiPassword(PgVectorEndpoint endpoint) => endpoint.Password;

    [Fact] // AC-19
    public async Task AUserThatCanWriteGetsAReadOnlyRoleRecommendationAndTheStatusIsUnchanged()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        var schema = await pgvector.GetSharedSchemaAsync();

        var registered = await RegisterAsync(admin, Body(PgVectorApi.Fields(await pgvector.GetEndpointAsync(), schema, SampleRelations.Flat)));

        Assert.True(registered.Status == "Available", string.Join(" | ", registered.Warnings.Select(w => w.Code + ": " + w.Message)));
        Assert.Contains("knowledge_base.pgvector_writable", registered.Warnings.Select(w => w.Code));
    }

    [Fact] // AC-5
    public async Task AnOmittedPasswordIsKeptForTheSameAccountAndRequiredForAnotherOne()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        var schema = await pgvector.GetSharedSchemaAsync();
        var endpoint = await pgvector.GetEndpointAsync();
        var registered = await RegisterAsync(admin, Body(PgVectorApi.Fields(endpoint, schema, SampleRelations.Flat)));

        var withoutPassword = PgVectorApi.Fields(endpoint, schema, SampleRelations.Flat);
        withoutPassword.Remove("password");
        using var kept = await admin.PutAsJsonAsync(KnowledgeBaseApi.One(registered.Id), Body(withoutPassword), Ct);
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.Equal("Available", (await kept.Content.ReadFromJsonAsync<KnowledgeBaseApi.VerifiedBody>(Ct))!.Status);

        var otherDatabase = PgVectorApi.Fields(endpoint, schema, SampleRelations.Flat, database: "another_database");
        otherDatabase.Remove("password");
        Assert.Equal("knowledge_base.password_required", await UpdateRefusedAsync(admin, registered.Id, Body(otherDatabase)));
    }

    [Fact] // AC-5
    public async Task AnOmittedConnectionStringIsKeptAndSwitchingToFieldsNeedsTheNewSecret()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        var schema = await pgvector.GetSharedSchemaAsync();
        var endpoint = await pgvector.GetEndpointAsync();
        var registered = await RegisterAsync(admin, Body(PgVectorApi.ConnectionString(endpoint, schema, SampleRelations.Flat)));

        using var kept = await admin.PutAsJsonAsync(KnowledgeBaseApi.One(registered.Id), Body(new { schema, table = SampleRelations.Flat }), Ct);
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.Equal("Available", (await kept.Content.ReadFromJsonAsync<KnowledgeBaseApi.VerifiedBody>(Ct))!.Status);

        var fields = PgVectorApi.Fields(endpoint, schema, SampleRelations.Flat);
        fields.Remove("password");
        Assert.Equal("knowledge_base.password_required", await UpdateRefusedAsync(admin, registered.Id, Body(fields)));
    }

    [Fact] // AC-8
    public async Task SeveralVectorColumnsNeedOneToBeChosen()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        var schema = await pgvector.GetSharedSchemaAsync();
        var endpoint = await pgvector.GetEndpointAsync();

        Assert.Equal(
            "knowledge_base.vector_column_required",
            await RegisterRefusedAsync(admin, Body(PgVectorApi.Fields(endpoint, schema, SampleRelations.TwoVectors), mapping: KnowledgeBaseApi.TextOnly(), createFields: [])));

        var chosen = await RegisterAsync(admin, Body(PgVectorApi.Fields(endpoint, schema, SampleRelations.TwoVectors, vectorColumn: "embedding"), mapping: KnowledgeBaseApi.TextOnly(), createFields: []));
        Assert.True(chosen.Status == "Available", string.Join(" | ", chosen.Warnings.Select(w => w.Code + ": " + w.Message)));
    }

    [Fact] // AC-17
    public async Task ARelationThatDoesNotExistIsSavedUnavailableAndNamesTheCause()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        var schema = await pgvector.GetSharedSchemaAsync();

        var registered = await RegisterAsync(admin, Body(PgVectorApi.Fields(await pgvector.GetEndpointAsync(), schema, "not_there")));

        Assert.Equal("Unavailable", registered.Status);
        Assert.Contains("knowledge_base.pgvector_relation_not_found", registered.Warnings.Select(w => w.Code));
        var detail = await KnowledgeBaseApi.GetAsync(admin, registered.Id);
        Assert.Equal("Unavailable", detail.Status);
        Assert.NotNull(detail.Warning);
    }

    [Fact] // AC-17
    public async Task WrongCredentialsAreSavedUnavailableWithoutEchoingThePassword()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        var schema = await pgvector.GetSharedSchemaAsync();

        var registered = await RegisterAsync(admin, Body(PgVectorApi.Fields(await pgvector.GetEndpointAsync(), schema, SampleRelations.Flat, password: "not-the-password")));

        Assert.Equal("Unavailable", registered.Status);
        Assert.Contains("knowledge_base.pgvector_unauthorized", registered.Warnings.Select(w => w.Code));
        Assert.DoesNotContain(registered.Warnings, w => w.Message.Contains("not-the-password", StringComparison.Ordinal));
    }

    [Fact] // AC-18, AC-43
    public async Task MissingIndexesWarnAndTheStatusStaysAvailable()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        var schema = await pgvector.GetSharedSchemaAsync();
        var schemaIndexed = await ReaderAsync(schema);

        var registered = await RegisterAsync(admin, Body(PgVectorApi.Fields(await pgvector.GetEndpointAsync(), schema, SampleRelations.Unindexed, username: schemaIndexed, password: ReaderPassword)));

        Assert.True(registered.Status == "Available", string.Join(" | ", registered.Warnings.Select(w => w.Code + ": " + w.Message)));
        var warnings = registered.Warnings.Select(w => (w.Path, w.Code)).ToList();
        Assert.Contains(("embedding", "knowledge_base.pgvector_vector_unindexed"), warnings);
        Assert.Contains(("department", "knowledge_base.pgvector_field_unindexed"), warnings);
        Assert.Contains(("document_id", "knowledge_base.pgvector_field_unindexed"), warnings);
    }

    [Fact] // AC-20
    public async Task ADroppedMetadataColumnIsReportedByNameAndTheTextColumnMakesTheKnowledgeBaseUnavailable()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        var schema = await pgvector.NewSchemaAsync();
        var role = await ReaderAsync(schema);
        var registered = await RegisterAsync(admin, Body(PgVectorApi.Fields(await pgvector.GetEndpointAsync(), schema, SampleRelations.Flat, username: role, password: ReaderPassword)));
        Assert.Empty(registered.Warnings);

        await pgvector.ExecuteAsync($"ALTER TABLE {schema}.{SampleRelations.Flat} DROP COLUMN department CASCADE");
        var afterMetadata = await KnowledgeBaseApi.VerifyOkAsync(admin, registered.Id);
        Assert.Equal("Available", afterMetadata.Status);
        Assert.Contains(afterMetadata.Warnings, w => w.Code == "knowledge_base.pgvector_column_missing" && w.Path == "department");

        await pgvector.ExecuteAsync($"ALTER TABLE {schema}.{SampleRelations.Flat} RENAME COLUMN text TO body");
        var afterText = await KnowledgeBaseApi.VerifyOkAsync(admin, registered.Id);
        Assert.Equal("Unavailable", afterText.Status);
        Assert.Contains(afterText.Warnings, w => w.Code == "knowledge_base.pgvector_column_missing" && w.Path == "text");
    }

    [Fact] // A2-1, AC-42 as read for a kind with the capability
    public async Task TheMappingMembersOfTheLookupsAreAcceptedBecauseTheKindHasLookups()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        var schema = await pgvector.GetSharedSchemaAsync();
        var connection = PgVectorApi.Fields(await pgvector.GetEndpointAsync(), schema, SampleRelations.Flat);
        var listable = new
        {
            roles = new { text = "text" },
            fields = new[] { new { path = "department", name = "department", listable = true } },
        };

        var registered = await RegisterAsync(admin, Body(connection, mapping: listable, createFields: [new { name = "department", type = "Text" }]));

        var detail = await KnowledgeBaseApi.GetAsync(admin, registered.Id);
        Assert.True(Assert.Single(detail.Mapping!.Fields).Listable);
    }
}
