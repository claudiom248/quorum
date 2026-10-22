using System.Net;
using System.Net.Http.Json;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.Tools;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBases;

public sealed class KnowledgeBaseTests(PostgresFixture postgres, QdrantFixture qdrant)
{
    private const string Secret = "s3cret-qdrant-key-8f2a";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(QuorumApp App, HttpClient Admin, string Collection, string Url)> StartWithFlatAsync()
    {
        var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var collection = QdrantFixture.NewPrefix() + SampleCollections.Flat;
        await qdrant.SeedAsync([SampleCollections.CreateFlat(collection)]);
        return (app, admin, collection, (await qdrant.GetUrlAsync()).ToString());
    }

    [Fact] // AC-13
    public async Task AReadyCollectionIsRegisteredAvailableWithoutWarnings()
    {
        var (app, admin, collection, url) = await StartWithFlatAsync();
        await using var _ = app;
        using var __ = admin;

        var registered = await KnowledgeBaseApi.RegisterOkAsync(admin, url, collection);

        Assert.Equal("Available", registered.Status);
        Assert.Empty(registered.Warnings);
        var detail = await KnowledgeBaseApi.GetAsync(admin, registered.Id);
        Assert.Equal("Available", detail.Status);
        Assert.Null(detail.Warning);
        Assert.NotNull(detail.LastVerifiedAt);
        Assert.Equal("policies", detail.Name);
        Assert.Equal("Qdrant", detail.Kind);
        Assert.Equal(collection, detail.Connection!.Collection);
        Assert.Equal(KnowledgeBaseApi.EmbeddingModel, detail.EmbeddingModel);
        Assert.Equal("text", detail.Mapping!.Roles.Text);
        Assert.Equal(["department", "year", "published_on", "confidential"], detail.Mapping.Fields.Select(f => f.Name));
        var listed = Assert.Single(await KnowledgeBaseApi.ListAsync(admin));
        Assert.Equal((registered.Id, "policies", "Available"), (listed.Id, listed.Name, listed.Status));
    }

    [Fact] // AC-16
    public async Task TheApiKeyIsEncryptedAtRestAndNeverReturnedOrLogged()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var collection = QdrantFixture.NewPrefix() + SampleCollections.Flat;
        await qdrant.SeedAsync([SampleCollections.CreateFlat(collection)], Secret);
        var url = (await qdrant.GetUrlAsync(Secret)).ToString();

        var registered = await KnowledgeBaseApi.RegisterOkAsync(admin, url, collection, apiKey: Secret);
        await KnowledgeBaseApi.VerifyOkAsync(admin, registered.Id);

        Assert.Equal("Available", registered.Status);
        var detail = await KnowledgeBaseApi.GetAsync(admin, registered.Id);
        Assert.True(detail.Connection!.HasApiKey);
        Assert.DoesNotContain(Secret, await KnowledgeBaseApi.GetRawAsync(admin, registered.Id), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, await admin.GetStringAsync(KnowledgeBaseApi.All, Ct), StringComparison.Ordinal);
        var stored = await ToolFixtures.ScalarAsync<string>(app, "SELECT \"Connection\"::text FROM knowledge_base.knowledge_bases LIMIT 1");
        Assert.DoesNotContain(Secret, stored, StringComparison.Ordinal);
        Assert.DoesNotContain(app.Logs.Messages, message => message.Contains(Secret, StringComparison.Ordinal));
    }

    [Fact] // AC-16
    public async Task ARegistrationWithoutAKeyReportsThatNoneIsSet()
    {
        var (app, admin, collection, url) = await StartWithFlatAsync();
        await using var _ = app;
        using var __ = admin;

        var registered = await KnowledgeBaseApi.RegisterOkAsync(admin, url, collection);

        Assert.False((await KnowledgeBaseApi.GetAsync(admin, registered.Id)).Connection!.HasApiKey);
    }

    [Theory] // AC-14
    [InlineData("unreachable", "knowledge_base.qdrant_unreachable")]
    [InlineData("missing-collection", "knowledge_base.collection_not_found")]
    [InlineData("other-vector-size", "knowledge_base.dimension_mismatch")]
    [InlineData("wrong-key", "knowledge_base.qdrant_unauthorized")]
    public async Task ACollectionThatIsNotReadyIsSavedUnavailableWithAWarningThatNamesTheCause(string situation, string code)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var prefix = QdrantFixture.NewPrefix();
        await qdrant.SeedAsync([SampleCollections.CreateOtherVectorSize(prefix + SampleCollections.OtherVectorSize)]);
        await qdrant.SeedAsync([SampleCollections.CreateFlat(prefix + SampleCollections.Flat)], Secret);

        var (url, collection, apiKey) = situation switch
        {
            "unreachable" => ("http://[::1]:1", "docs", (string?)null),
            "missing-collection" => ((await qdrant.GetUrlAsync()).ToString(), prefix + "not_there", null),
            "other-vector-size" => ((await qdrant.GetUrlAsync()).ToString(), prefix + SampleCollections.OtherVectorSize, null),
            _ => ((await qdrant.GetUrlAsync(Secret)).ToString(), prefix + SampleCollections.Flat, "not-the-key"),
        };

        var registered = await KnowledgeBaseApi.RegisterOkAsync(admin, url, collection, apiKey: apiKey, mapping: KnowledgeBaseApi.TextOnly(), createFields: []);

        Assert.Equal("Unavailable", registered.Status);
        var warning = Assert.Single(registered.Warnings, w => w.Code == code);
        Assert.Contains("RAG", warning.Message, StringComparison.Ordinal);
        var detail = await KnowledgeBaseApi.GetAsync(admin, registered.Id);
        Assert.Equal("Unavailable", detail.Status);
        Assert.False(string.IsNullOrWhiteSpace(detail.Warning));
    }

    [Fact] // AC-18
    public async Task ANameAlreadyUsedInTheTenantIsRefused()
    {
        var (app, admin, collection, url) = await StartWithFlatAsync();
        await using var _ = app;
        using var __ = admin;
        await KnowledgeBaseApi.RegisterOkAsync(admin, url, collection);

        using var again = await KnowledgeBaseApi.RegisterAsync(admin, url, collection, mapping: KnowledgeBaseApi.TextOnly(), createFields: []);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("knowledge_base.name_taken", await KnowledgeBaseApi.ProblemCodeAsync(again));
        Assert.Single(await KnowledgeBaseApi.ListAsync(admin));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Policies")]
    [InlineData("has space")]
    [InlineData("-leading")]
    [InlineData("a-name-that-is-longer-than-forty-characters-long")]
    public async Task AnInvalidNameIsRefused(string name)
    {
        var (app, admin, collection, url) = await StartWithFlatAsync();
        await using var _ = app;
        using var __ = admin;

        using var response = await KnowledgeBaseApi.RegisterAsync(admin, url, collection, name, mapping: KnowledgeBaseApi.TextOnly(), createFields: []);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("knowledge_base.invalid_name", await KnowledgeBaseApi.ProblemCodeAsync(response));
    }

    [Theory]
    [InlineData("Everyone", false, false)]
    [InlineData("Global", true, false)]
    [InlineData("Tenant", false, true)]
    [InlineData("Assistant", false, false)]
    [InlineData("Assistant", true, false)]
    public async Task AnIncoherentScopeIsRefused(string kind, bool withTenants, bool withAssistants)
    {
        var (app, admin, collection, url) = await StartWithFlatAsync();
        await using var _ = app;
        using var __ = admin;

        using var response = await KnowledgeBaseApi.RegisterAsync(
            admin,
            url,
            collection,
            mapping: KnowledgeBaseApi.TextOnly(),
            createFields: [],
            scope: new { kind, tenantIds = withTenants ? new[] { Guid.NewGuid() } : null, assistantIds = withAssistants ? new[] { Guid.NewGuid() } : null });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("knowledge_base.invalid_scope", await KnowledgeBaseApi.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task TheAgentScopeIsNotSupportedYet()
    {
        var (app, admin, collection, url) = await StartWithFlatAsync();
        await using var _ = app;
        using var __ = admin;

        using var response = await KnowledgeBaseApi.RegisterAsync(admin, url, collection, mapping: KnowledgeBaseApi.TextOnly(), createFields: [], scope: new { kind = "Agent" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("knowledge_base.scope_not_supported", await KnowledgeBaseApi.ProblemCodeAsync(response));
    }

    [Fact] // AC-11
    public async Task ACollectionWithSeveralVectorsNeedsOneToBeChosen()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var collection = QdrantFixture.NewPrefix() + SampleCollections.NamedVectors;
        await qdrant.SeedAsync([SampleCollections.CreateNamedVectors(collection)]);
        var url = (await qdrant.GetUrlAsync()).ToString();

        using var unchosen = await KnowledgeBaseApi.RegisterAsync(admin, url, collection, mapping: KnowledgeBaseApi.TextOnly(), createFields: []);
        using var unknown = await KnowledgeBaseApi.RegisterAsync(admin, url, collection, vectorName: "nope", mapping: KnowledgeBaseApi.TextOnly(), createFields: []);
        var chosen = await KnowledgeBaseApi.RegisterOkAsync(admin, url, collection, vectorName: SampleCollections.DenseVectorName, mapping: KnowledgeBaseApi.TextOnly(), createFields: []);

        Assert.Equal(HttpStatusCode.BadRequest, unchosen.StatusCode);
        Assert.Equal("knowledge_base.vector_name_required", await KnowledgeBaseApi.ProblemCodeAsync(unchosen));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("knowledge_base.vector_not_found", await KnowledgeBaseApi.ProblemCodeAsync(unknown));
        Assert.Equal("Available", chosen.Status);
        Assert.Equal(SampleCollections.DenseVectorName, (await KnowledgeBaseApi.GetAsync(admin, chosen.Id)).Connection!.VectorName);
    }

    [Fact] // D-21
    public async Task TheLogsCarryNeitherTheAddressNorTheCollectionNorTheDescription()
    {
        var (app, admin, collection, url) = await StartWithFlatAsync();
        await using var _ = app;
        using var __ = admin;

        await KnowledgeBaseApi.RegisterOkAsync(admin, url, collection);

        Assert.DoesNotContain(app.Logs.Messages, m => m.Contains(collection, StringComparison.Ordinal) || m.Contains(url, StringComparison.Ordinal) || m.Contains("Company policies", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheKindMustBeQdrant()
    {
        var (app, admin, collection, url) = await StartWithFlatAsync();
        await using var _ = app;
        using var __ = admin;

        using var response = await admin.PostAsJsonAsync(
            KnowledgeBaseApi.All,
            new
            {
                name = "policies",
                description = "Company policies.",
                kind = "PgVector",
                connection = new { url, collection },
                embeddingModel = KnowledgeBaseApi.EmbeddingModel,
                mapping = KnowledgeBaseApi.TextOnly(),
                scope = new { kind = "Tenant" },
            },
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("knowledge_base.unsupported_kind", await KnowledgeBaseApi.ProblemCodeAsync(response));
    }
}
