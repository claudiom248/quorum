using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.KnowledgeBases.Search;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.KnowledgeBases.Contracts;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Features.Inspect;
using Quorum.Modules.KnowledgeBases.Features.Search;
using Quorum.Modules.KnowledgeBases.Features.Verification;
using Quorum.Modules.KnowledgeBases.Mapping;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.Modules.KnowledgeBases.Storage;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBases.Storage;

/// <summary>
/// The contract between the shared code and the adapter of a kind (spec 008 D-1, A1-1), shown with a kind that is not Qdrant: a
/// store in memory that names its own warnings and codes. If anything shared assumed Qdrant, these would fail.
/// </summary>
public sealed class StoreAdapterContractTests(PostgresFixture postgres)
{
    private static readonly string[] SecurityDocuments = ["security"];

    private static FakeStoreAdapter.Row Row(string text, string documentId, int year) =>
        new(text, new JsonObject { ["document_id"] = documentId, ["year"] = year });

    private static readonly FakeStoreAdapter.Row[] Rows =
    [
        Row("Passwords must be rotated every ninety days.", "security", 2024),
        Row("Annual leave is requested through the portal.", "leave", 2023),
        Row("Travel expenses are refunded within thirty days.", "travel", 2022),
    ];

    private static JsonElement Connection(string name) => JsonSerializer.SerializeToElement(new { name });

    private sealed record Setup(QuorumApp App, Guid TenantId, FakeStoreAdapter Fake) : IAsyncDisposable
    {
        public StoreAdapters Adapters { get; } = new([Fake]);

        /// <summary>
        /// A scope that runs as the tenant. The test opens it itself: the accessor keeps the request in an async-local value, which
        /// a helper that is an async method would not carry back to the test.
        /// </summary>
        public AsyncServiceScope Open() => TenantScope.Open(App, TenantId);

        public KnowledgeBaseSearchEngine Engine(IServiceProvider services) => new(
            services.GetRequiredService<KnowledgeBasesDbContext>(),
            services.GetRequiredService<IEmbeddings>(),
            Adapters,
            TimeProvider.System,
            services.GetRequiredService<ILogger<KnowledgeBaseSearchEngine>>());

        public KnowledgeBaseInspector Inspector(IServiceProvider services) => new(
            services.GetRequiredService<KnowledgeBasesDbContext>(),
            new StoreProbe(services.GetRequiredService<IEmbeddings>()),
            Adapters,
            services.GetRequiredService<ILogger<KnowledgeBaseInspector>>());

        public KnowledgeBase KnowledgeBase(string name, string connection, string? documentId = "document_id") => Modules.KnowledgeBases.Domain.KnowledgeBase.Create(
            TenantId,
            name,
            "A store in memory.",
            KnowledgeBaseKind.Qdrant,
            Fake.ToSettings(Fake.ReadConnection(Connection(connection)).Value),
            InspectApi.EmbeddingModel,
            queryPrefix: null,
            new KnowledgeBaseMapping(new MappingRoles("text", documentId, null, null, null), []),
            KnowledgeBaseScope.Tenant,
            [],
            [],
            DateTimeOffset.UtcNow);

        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private async Task<Setup> StartAsync(FakeStoreAdapter? fake = null)
    {
        var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        (await app.SetUpAsync()).Dispose();
        InspectApi.ServeEmbeddings(app);
        return new Setup(app, await TenantScope.TenantIdAsync(app), fake ?? new FakeStoreAdapter(Rows));
    }

    private static SearchInput Input(string query, int topK = 5, IReadOnlyList<string>? documentIds = null) =>
        new(query, new Dictionary<string, JsonElement>(), new Dictionary<string, JsonElement>(), topK, ScoreThreshold: null, MaxResultCharacters: null, DocumentIds: documentIds);

    [Fact] // AC-39 (mechanism)
    public async Task AKindAnswersForTheCapabilitiesItDeclaresAndNotForTheOthers()
    {
        await using var setup = await StartAsync();
        await using var scope = setup.Open();
        var declaring = new FakeStoreAdapter(Rows) { Declared = new FakeCapability() };

        Assert.Null(setup.Fake.Capability<IFakeCapability>());
        Assert.Equal("fake", declaring.Capability<IFakeCapability>()!.Describe());
        // Qdrant, as registered in the application, declares none.
        var qdrant = scope.ServiceProvider.GetRequiredService<StoreAdapters>().For(KnowledgeBaseKind.Qdrant);
        Assert.Null(qdrant.Capability<IFakeCapability>());
    }

    [Fact]
    public void AKindWithoutAnAdapterStopsTheApplicationWithAClearMessage()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new StoreAdapters([]));

        Assert.Contains("Qdrant", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheInspectorDraftsAMappingFromTheRowsOfAnInMemoryStoreAndWordsItsWarningsThroughTheAdapter()
    {
        await using var setup = await StartAsync();
        await using var scope = setup.Open();

        var result = await setup.Inspector(scope.ServiceProvider).InspectAsync(
            new InspectRequest("Qdrant", Connection("memory"), InspectApi.EmbeddingModel, QueryPrefix: null, Schema: null),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.IsSuccess ? string.Empty : $"{result.Error.Code}: {result.Error.Description}");
        var report = result.Value;
        Assert.True(report.Reachable);
        Assert.True(report.CollectionExists);
        Assert.Equal([new VectorDto(null, DeterministicEmbedding.DefaultDimensions, "Dot")], report.Vectors);
        Assert.True(report.DimensionMatch);
        Assert.Equal("text", report.DraftMapping.Roles.Text);
        Assert.Equal("document_id", report.DraftMapping.Roles.DocumentId);
        Assert.Equal(["year"], report.DraftMapping.Fields.Select(f => f.Path));
        // "year" is the one path the store says it indexes; nothing here names Qdrant.
        Assert.DoesNotContain(report.Warnings, w => w.Code.Contains("qdrant", StringComparison.Ordinal));
        Assert.Empty(report.UnindexedFields);
    }

    [Fact]
    public async Task TheInspectorSaysWhatTheAdapterSaysWhenASourceIsEmptyOrAPathHasNoIndex()
    {
        await using var setup = await StartAsync(new FakeStoreAdapter([]));
        await using var scope = setup.Open();

        var empty = await setup.Inspector(scope.ServiceProvider).InspectAsync(
            new InspectRequest("Qdrant", Connection("memory"), InspectApi.EmbeddingModel, QueryPrefix: null, Schema: null),
            TestContext.Current.CancellationToken);
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { text = new { type = "string" }, department = new { type = "string" } } });
        var withSchema = await setup.Inspector(scope.ServiceProvider).InspectAsync(
            new InspectRequest("Qdrant", Connection("memory"), InspectApi.EmbeddingModel, QueryPrefix: null, Schema: schema),
            TestContext.Current.CancellationToken);

        Assert.Contains(empty.Value.Warnings, w => w.Code == "fake.empty");
        Assert.Equal(["department"], withSchema.Value.UnindexedFields);
        Assert.Contains(withSchema.Value.Warnings, w => w is { Code: "fake.unindexed", Path: "department" });
    }

    [Fact]
    public async Task TheInspectorRefusesAConnectionTheAdapterDoesNotAccept()
    {
        await using var setup = await StartAsync();
        await using var scope = setup.Open();

        var result = await setup.Inspector(scope.ServiceProvider).InspectAsync(
            new InspectRequest("Qdrant", JsonSerializer.SerializeToElement(new { url = "http://qdrant:6334" }), InspectApi.EmbeddingModel, QueryPrefix: null, Schema: null),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("knowledge_base.invalid_connection", result.Error.Code);
        Assert.Equal("Fake: the connection needs a name.", result.Error.Description);
    }

    [Fact]
    public async Task TheSearchEngineSearchesAnInMemoryStoreAndAsksItOnlyForTheMappedPaths()
    {
        await using var setup = await StartAsync();
        await using var scope = setup.Open();
        var knowledgeBase = setup.KnowledgeBase("memory", "memory");

        var result = await setup.Engine(scope.ServiceProvider).SearchAsync([knowledgeBase], Input(Rows[1].Text), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.IsSuccess ? string.Empty : $"{result.Error.Code}: {result.Error.Description}");
        var best = result.Value.Chunks[0];
        Assert.Equal((Rows[1].Text, "leave", "memory"), (best.Text, best.DocumentId, best.KnowledgeBase));
        Assert.InRange(best.Score, 0.999, 1.001);
        Assert.Equal(3, result.Value.Chunks.Count);
        var query = Assert.Single(setup.Fake.Queries);
        Assert.Equal(["text", "document_id"], query.Paths);
        Assert.Equal(5, query.Limit);
        Assert.Empty(query.Conditions);
    }

    [Fact]
    public async Task AFailureOfTheStoreIsGivenTheCodeTheAdapterChoosesAndTheOtherKnowledgeBasesStillAnswer()
    {
        await using var setup = await StartAsync();
        await using var scope = setup.Open();
        var working = setup.KnowledgeBase("working", "memory");
        var broken = setup.KnowledgeBase("broken", "broken");
        var locked = setup.KnowledgeBase("locked", "locked");

        var result = await setup.Engine(scope.ServiceProvider).SearchAsync([working, broken, locked], Input(Rows[0].Text), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.IsSuccess ? string.Empty : $"{result.Error.Code}: {result.Error.Description}");
        Assert.All(result.Value.Chunks, chunk => Assert.Equal("working", chunk.KnowledgeBase));
        Assert.Equal(
            [("broken", "fake.Unreachable", "Fake: the store is down."), ("locked", "fake.secret_unreadable", "Fake: the secret cannot be read.")],
            result.Value.FailedKnowledgeBases.OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => (f.Name, f.Code, f.Message)));
    }

    [Fact] // 008 R7 (the engine, with the Qdrant adapter's translation tested apart)
    public async Task ADocumentIdsConditionReachesTheAdapterOnThePathOfTheDocumentIdRole()
    {
        await using var setup = await StartAsync();
        await using var scope = setup.Open();
        var knowledgeBase = setup.KnowledgeBase("memory", "memory");

        var result = await setup.Engine(scope.ServiceProvider).SearchAsync([knowledgeBase], Input(Rows[0].Text, documentIds: SecurityDocuments), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.IsSuccess ? string.Empty : $"{result.Error.Code}: {result.Error.Description}");
        Assert.Equal("security", Assert.Single(result.Value.Chunks).DocumentId);
        var condition = Assert.IsType<DocumentIdsCondition>(Assert.Single(Assert.Single(setup.Fake.Queries).Conditions));
        Assert.Equal(("document_id", SecurityDocuments), (condition.Path, condition.Ids));
    }

    [Fact] // 008 R7, AC-41 (skipping part)
    public async Task AKnowledgeBaseWithoutADocumentIdRoleIsSkippedAndNamedAndNeverSearchedWithoutTheCondition()
    {
        await using var setup = await StartAsync();
        await using var scope = setup.Open();
        var mapped = setup.KnowledgeBase("mapped", "memory");
        var unmapped = setup.KnowledgeBase("unmapped", "memory", documentId: null);

        var result = await setup.Engine(scope.ServiceProvider).SearchAsync([unmapped, mapped], Input(Rows[0].Text, documentIds: SecurityDocuments), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.IsSuccess ? string.Empty : $"{result.Error.Code}: {result.Error.Description}");
        Assert.Equal(["unmapped"], result.Value.SkippedKnowledgeBases);
        Assert.All(result.Value.Chunks, chunk => Assert.Equal("mapped", chunk.KnowledgeBase));
        Assert.Single(setup.Fake.Queries);
    }

    [Fact] // 008 R7
    public async Task WhenNoKnowledgeBaseCanApplyTheDocumentIdsTheSearchSaysSoWithoutAskingAnyStore()
    {
        await using var setup = await StartAsync();
        await using var scope = setup.Open();
        var unmapped = setup.KnowledgeBase("unmapped", "memory", documentId: null);

        var result = await setup.Engine(scope.ServiceProvider).SearchAsync([unmapped], Input(Rows[0].Text, documentIds: SecurityDocuments), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(KnowledgeBaseSearchErrors.FilterUnsupported, result.Error.Code);
        Assert.Empty(setup.Fake.Queries);
    }

    [Theory] // 008 R7: no list, or an empty one, is no condition
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoDocumentIdsIsNoCondition(bool emptyList)
    {
        await using var setup = await StartAsync();
        await using var scope = setup.Open();
        var unmapped = setup.KnowledgeBase("unmapped", "memory", documentId: null);

        var result = await setup.Engine(scope.ServiceProvider).SearchAsync([unmapped], Input(Rows[0].Text, documentIds: emptyList ? [] : null), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.IsSuccess ? string.Empty : $"{result.Error.Code}: {result.Error.Description}");
        Assert.Empty(Assert.Single(setup.Fake.Queries).Conditions);
    }
}
