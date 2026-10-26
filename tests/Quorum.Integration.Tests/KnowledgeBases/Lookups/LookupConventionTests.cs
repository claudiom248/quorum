using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.KnowledgeBases.Storage;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Features.Inspect;
using Quorum.Modules.KnowledgeBases.Features.Lookups;
using Quorum.Modules.KnowledgeBases.Features.Registry;
using Quorum.Modules.KnowledgeBases.Features.Verification;
using Quorum.Modules.KnowledgeBases.Mapping;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.Modules.KnowledgeBases.Storage;
using Quorum.Shared.Tenancy;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBases.Lookups;

/// <summary>What the draft mapping proposes for the counted point, what the inspection warns about, and the kinds that cannot list (spec 007 D-19, AC-3).</summary>
public sealed class LookupConventionTests(PostgresFixture postgres, QdrantFixture qdrant)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DiscoveredProperty Prop(string path, DictionaryFieldType type) => new(path, type);

    [Theory]
    [InlineData("is_first_chunk")]
    [InlineData("first_chunk")]
    [InlineData("is_primary")]
    [InlineData("IsFirstChunk")]
    public void ABooleanFlagThatMarksTheFirstChunkIsProposedAsTrue(string name)
    {
        var draft = MappingConventions.Build([Prop("text", DictionaryFieldType.Text), Prop(name, DictionaryFieldType.Boolean)]);

        Assert.Equal(new CountedPoint(name, "true"), draft.CountedPoint);
        Assert.Empty(draft.Fields);
    }

    [Fact]
    public void AChunkIndexIsProposedAsEqualToZero()
    {
        var draft = MappingConventions.Build([Prop("text", DictionaryFieldType.Text), Prop("chunk_index", DictionaryFieldType.Number)]);

        Assert.Equal(new CountedPoint("chunk_index", "0"), draft.CountedPoint);
        Assert.Empty(draft.Fields);
    }

    [Fact]
    public void TheFlagWinsOverTheChunkIndexAndTheIndexStaysAMetadataField()
    {
        var draft = MappingConventions.Build([Prop("is_first_chunk", DictionaryFieldType.Boolean), Prop("chunk_index", DictionaryFieldType.Number)]);

        Assert.Equal(new CountedPoint("is_first_chunk", "true"), draft.CountedPoint);
        Assert.Equal(["chunk_index"], draft.Fields.Select(f => f.Path));
    }

    [Theory]
    [InlineData("is_first_chunk", "Text")]
    [InlineData("chunk_index", "Text")]
    [InlineData("page_index", "Number")]
    public void APropertyOfAnotherTypeOrNameIsNeverProposed(string name, string type)
    {
        var draft = MappingConventions.Build([Prop(name, Enum.Parse<DictionaryFieldType>(type))]);

        Assert.Null(draft.CountedPoint);
        Assert.Equal([name], draft.Fields.Select(f => f.Path));
    }

    [Fact]
    public async Task TheInspectionProposesTheCountedPointOfTheLookupsCollection()
    {
        var collection = await qdrant.SeedSharedAsync(SampleCollections.CreateLookups);
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        InspectApi.ServeEmbeddings(app);

        var report = await InspectApi.InspectOkAsync(admin, await qdrant.GetUrlAsync(), collection);

        var point = report.DraftMapping.CountedPoint!;
        Assert.Equal(("is_first_chunk", JsonValueKind.True), (point.Path, point.Value.ValueKind));
        Assert.DoesNotContain("knowledge_base.lookup_field_unindexed", report.WarningCodes);
    }

    [Fact]
    public async Task TheInspectionWarnsAboutAnUnindexedCountedPointAndDocumentId()
    {
        var collection = QdrantFixture.NewPrefix() + SampleCollections.LookupsUnindexed;
        await qdrant.SeedAsync([SampleCollections.CreateLookupsUnindexed(collection)]);
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        InspectApi.ServeEmbeddings(app);

        var report = await InspectApi.InspectOkAsync(admin, await qdrant.GetUrlAsync(), collection);

        var warned = report.Warnings.Where(w => w.Code == "knowledge_base.lookup_field_unindexed").Select(w => w.Path).Order(StringComparer.Ordinal);
        Assert.Equal(["document_id", "is_first_chunk"], warned);
    }

    [Fact] // AC-3
    public async Task AKindWithoutLookupsRefusesAListableField()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        (await app.SetUpAsync()).Dispose();
        InspectApi.ServeEmbeddings(app);
        var tenantId = await TenantScope.TenantIdAsync(app);
        await using var scope = TenantScope.Open(app, tenantId);
        var services = scope.ServiceProvider;
        var fake = new FakeStoreAdapter([]);
        var registrar = new KnowledgeBaseRegistrar(
            services.GetRequiredService<KnowledgeBasesDbContext>(),
            services.GetRequiredService<ITenantContext>(),
            new StoreProbe(services.GetRequiredService<IEmbeddings>()),
            new StoreAdapters([fake, FakeStoreAdapter.PgVectorStub()]),
            services.GetRequiredService<IAssistantDirectory>(),
            services.GetRequiredService<LookupRefreshSignal>(),
            services.GetRequiredService<LookupCache>(),
            TimeProvider.System,
            services.GetRequiredService<ILogger<KnowledgeBaseRegistrar>>());

        var result = await registrar.CreateAsync(
            new KnowledgeBaseRequest(
                "in-memory",
                "A store in memory.",
                "Qdrant",
                JsonSerializer.SerializeToElement(new { name = "memory" }),
                InspectApi.EmbeddingModel,
                QueryPrefix: null,
                new MappingRequest(new RolesDto("text", null, null, null, null), [new MappedFieldRequest("year", null, "year", Listable: true)]),
                new ScopeRequest("Tenant", null, null),
                [new CreateFieldRequest("year", "Text", null)]),
            Ct);

        Assert.False(result.IsSuccess);
        Assert.Equal("knowledge_base.lookups_not_supported", result.Error.Code);
    }
}
