using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.KnowledgeBases;
using Quorum.SampleKnowledgeBase;
using Quorum.Web.Client;
using Quorum.Web.Client.Api;

namespace Quorum.Integration.Tests.Web;

/// <summary>
/// The draft and the typed client of the admin pages for a PostgreSQL knowledge base, against the real server and a real database
/// (spec 008 AC-34, AC-35): everything the guided registration does, with no mock of the API.
/// </summary>
public sealed class PgVectorAdminClientTests(PostgresFixture postgres, PgVectorFixture pgvector)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;



    private async Task<KnowledgeBaseDraft> NewDraftAsync(string? schema = null) => new()
    {
        Kind = KnowledgeBaseKinds.PgVector,
        Name = "policies",
        Description = "Company policies.",
        Host = (await pgvector.GetEndpointAsync()).Host,
        Port = (await pgvector.GetEndpointAsync()).Port.ToString(global::System.Globalization.CultureInfo.InvariantCulture),
        Database = (await pgvector.GetEndpointAsync()).Database,
        Username = (await pgvector.GetEndpointAsync()).Username,
        Password = (await pgvector.GetEndpointAsync()).Password,
        DatabaseSchema = schema ?? string.Empty,
        EmbeddingModel = KnowledgeBaseApi.EmbeddingModel,
    };

    [Fact] // AC-34
    public async Task AnAdminListsTheRelationsChoosesOneInspectsRegistersAndSearchesWithoutTouchingFiles()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var api = new QuorumApi(admin);
        var schema = await pgvector.GetSharedSchemaAsync();
        var draft = await NewDraftAsync(schema);

        // The connection step: no relation yet, so only the list can be asked for.
        Assert.Contains(draft.ConnectionProblems(), p => p.Contains("relation", StringComparison.Ordinal));
        Assert.Empty(draft.PgVectorServerProblems());
        var relations = (await api.ListRelationsAsync(draft.ToRelationsRequest("sample_fl"), Ct)).Value;
        Assert.True(relations.Reachable);
        var flat = Assert.Single(relations.Relations, r => r.Name == SampleRelations.Flat);
        Assert.Contains("table", KnowledgeAdminView.DescribeRelation(flat), StringComparison.Ordinal);
        draft.UseRelation(flat);
        Assert.Equal((SampleRelations.Flat, "embedding"), (draft.Table, draft.VectorColumn));
        Assert.Empty(draft.ConnectionProblems());

        // The inspection proposes the mapping and the distance of the index.
        var inspection = (await api.InspectKnowledgeBaseAsync(draft.ToInspectRequest(), Ct)).Value;
        Assert.True(KnowledgeAdminView.CanReview(inspection));
        Assert.Contains("The relation exists.", KnowledgeAdminView.Findings(inspection, KnowledgeBaseKinds.PgVector));
        draft.UseInspection(inspection);
        Assert.Equal("Cosine", draft.Distance);
        Assert.Equal("text", draft.Mapping!.Text);

        // The mapping review and the save; a PostgreSQL knowledge base has no lookups to review.
        var dictionary = (await api.ListDictionaryAsync(Ct)).Value;
        draft.Mapping.ConfirmCreate = true;
        Assert.Empty(draft.LookupProblems(dictionary));
        Assert.Empty(draft.Problems(dictionary));
        Assert.DoesNotContain(draft.Summary(dictionary, []), line => line.Contains("lists", StringComparison.OrdinalIgnoreCase));
        var registered = (await api.CreateKnowledgeBaseAsync(draft.ToRequest(dictionary), Ct)).Value;
        Assert.Equal(KnowledgeBaseStatuses.Available, registered.Status);

        // The detail never carries the password, and a draft made from it can be saved again without retyping it.
        var detail = (await api.GetKnowledgeBaseAsync(registered.Id, Ct)).Value;
        Assert.True(detail.Connection!.HasPassword);
        Assert.Equal(KnowledgeBaseKinds.PgVector, detail.Kind);
        Assert.Contains(SampleRelations.Flat, KnowledgeAdminView.DescribeConnection(detail), StringComparison.Ordinal);
        Assert.DoesNotContain("Password", KnowledgeAdminView.DescribeConnection(detail), StringComparison.Ordinal);
        dictionary = (await api.ListDictionaryAsync(Ct)).Value;
        var edit = KnowledgeBaseDraft.From(detail);
        edit.MarkOpened();
        Assert.False(edit.IsDirty);
        Assert.True(edit.IsPgVector);
        Assert.False(edit.NeedsPasswordAgain);
        Assert.Empty(edit.Problems(dictionary));
        var updated = (await api.UpdateKnowledgeBaseAsync(registered.Id, edit.ToRequest(dictionary), Ct)).Value;
        Assert.Equal(KnowledgeBaseStatuses.Available, updated.Status);

        // Verify. The test search of the page runs the search of #103, which is tested there.
        Assert.Equal(KnowledgeBaseStatuses.Available, (await api.VerifyKnowledgeBaseAsync(registered.Id, Ct)).Value.Status);
    }

    [Fact] // AC-34
    public async Task AConnectionStringRegistersTheSameAndASecondEditNeedsTheSecretOnlyWhenTheModeChanges()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var api = new QuorumApi(admin);
        var endpoint = await pgvector.GetEndpointAsync();
        var draft = await NewDraftAsync(await pgvector.GetSharedSchemaAsync());
        draft.UseConnectionString = true;
        draft.ConnectionString = $"Host={endpoint.Host};Port={endpoint.Port};Database={endpoint.Database};Username={endpoint.Username};Password={endpoint.Password}";
        draft.Table = SampleRelations.Flat;
        draft.UseInspection((await api.InspectKnowledgeBaseAsync(draft.ToInspectRequest(), Ct)).Value);
        draft.Mapping!.ConfirmCreate = true;
        var dictionary = (await api.ListDictionaryAsync(Ct)).Value;

        var registered = (await api.CreateKnowledgeBaseAsync(draft.ToRequest(dictionary), Ct)).Value;

        var detail = (await api.GetKnowledgeBaseAsync(registered.Id, Ct)).Value;
        Assert.True(detail.Connection!.HasConnectionString);
        Assert.DoesNotContain("Password=", KnowledgeAdminView.DescribeConnection(detail), StringComparison.Ordinal);
        dictionary = (await api.ListDictionaryAsync(Ct)).Value;
        var edit = KnowledgeBaseDraft.From(detail);
        Assert.True(edit.UseConnectionString);
        Assert.Empty(edit.Problems(dictionary));
        Assert.Equal(KnowledgeBaseStatuses.Available, (await api.UpdateKnowledgeBaseAsync(registered.Id, edit.ToRequest(dictionary), Ct)).Value.Status);

        edit.UseConnectionString = false;
        Assert.True(edit.NeedsSecretForNewMode);
        Assert.Contains(edit.ConnectionProblems(), p => p.Contains("password", StringComparison.Ordinal));
    }

    [Fact] // AC-8
    public async Task SeveralVectorColumnsAskForAChoiceAndOfferTheDistanceOfTheIndex()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var api = new QuorumApi(admin);
        var draft = await NewDraftAsync(await pgvector.GetSharedSchemaAsync());
        var relations = (await api.ListRelationsAsync(draft.ToRelationsRequest(SampleRelations.TwoVectors), Ct)).Value;

        draft.UseRelation(Assert.Single(relations.Relations, r => r.Name == SampleRelations.TwoVectors));
        var inspection = (await api.InspectKnowledgeBaseAsync(draft.ToInspectRequest(), Ct)).Value;

        Assert.Equal(string.Empty, draft.VectorColumn);
        Assert.True(draft.NeedsVectorColumnChoice(inspection));
        draft.UseInspection(inspection);
        Assert.Equal(string.Empty, draft.VectorColumn);
        draft.VectorColumn = "embedding";
        Assert.False(draft.NeedsVectorColumnChoice(inspection));
    }

    [Fact]
    public void ATypedPortAndTheAccountAreCheckedBeforeAnythingIsSent()
    {
        var draft = new KnowledgeBaseDraft { Kind = KnowledgeBaseKinds.PgVector, Name = "docs", Description = "Docs.", EmbeddingModel = "m" };

        Assert.Equal(3, draft.PgVectorServerProblems().Count);

        draft.Host = "db";
        draft.Database = "docs";
        draft.Username = "reader";
        draft.Port = "99999";
        Assert.Contains(draft.PgVectorServerProblems(), p => p.Contains("port", StringComparison.Ordinal));

        draft.Port = "5433";
        Assert.Empty(draft.PgVectorServerProblems());
        var connection = Assert.IsType<PgVectorConnectionRequest>(draft.ToInspectRequest().Connection);
        Assert.Equal(("db", 5433, "docs", "reader"), (connection.Host, connection.Port, connection.Database, connection.Username));
        Assert.Null(connection.ConnectionString);
    }

    [Fact] // AC-5
    public void ChangingTheAccountAsksForThePasswordAgainAndTheSameAccountDoesNot()
    {
        var detail = new KnowledgeBaseDetail(
            Guid.NewGuid(),
            "docs",
            "Docs.",
            KnowledgeBaseStatuses.Available,
            false,
            KnowledgeBaseKinds.PgVector,
            new ConnectionInfo(string.Empty, string.Empty, null, false, "DB.example.test", null, "docs", "reader", null, true, false, "public", "chunks", "embedding", "Cosine"),
            "m",
            null,
            null,
            new ToolScope(ScopeKinds.Tenant, [], []),
            null,
            null);

        var edit = KnowledgeBaseDraft.From(detail);
        Assert.False(edit.NeedsPasswordAgain);

        edit.Port = "5432";
        edit.Host = "db.example.test";
        Assert.False(edit.NeedsPasswordAgain);

        edit.Database = "other";
        Assert.True(edit.NeedsPasswordAgain);

        edit.Password = "typed";
        Assert.False(edit.NeedsPasswordAgain);
        Assert.Equal("typed", Assert.IsType<PgVectorConnectionRequest>(edit.ToInspectRequest().Connection).Password);
    }

    [Fact]
    public void ThePasswordIsOmittedFromTheRequestWhenNothingWasTypedSoTheServerKeepsTheStoredOne()
    {
        var draft = new KnowledgeBaseDraft { Kind = KnowledgeBaseKinds.PgVector, Host = "db", Database = "d", Username = "u", Table = "t" };

        var connection = Assert.IsType<PgVectorConnectionRequest>(draft.ToInspectRequest().Connection);

        Assert.Null(connection.Password);
    }

    [Fact] // AC-35
    public void AQdrantDraftStillBuildsTheSameRequest()
    {
        var draft = new KnowledgeBaseDraft { Name = "docs", Description = "Docs.", Url = "http://qdrant:6334", Collection = "c", EmbeddingModel = "m" };

        var request = draft.ToInspectRequest();

        Assert.Equal(KnowledgeBaseKinds.Qdrant, request.Kind);
        Assert.Equal(new ConnectionRequest("http://qdrant:6334", null, "c", null), request.Connection);
        Assert.Empty(draft.ConnectionProblems());
    }

    [Theory]
    [InlineData("knowledge_base.pgvector_unreachable", WarningLevel.Error)]
    [InlineData("knowledge_base.pgvector_relation_not_found", WarningLevel.Error)]
    [InlineData("knowledge_base.vector_column_required", WarningLevel.Error)]
    [InlineData("knowledge_base.pgvector_vector_unindexed", WarningLevel.Info)]
    [InlineData("knowledge_base.pgvector_writable", WarningLevel.Info)]
    [InlineData("knowledge_base.pgvector_column_missing", WarningLevel.Warning)]
    public void TheWarningsOfAPostgresKnowledgeBaseAreShownAtTheirLevel(string code, WarningLevel level)
    {
        Assert.Equal(level, KnowledgeAdminView.LevelOf(new KnowledgeBaseWarning(code, "message", null)));
    }
}
