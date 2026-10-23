using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.KnowledgeBases.Features.Search;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBases.Search;

/// <summary>
/// The search can be restricted to some documents (spec 008 A1-2, the engine's part): the condition is combined with the filters
/// and applies to the path the knowledge base mapped to the <c>documentId</c> role. The rag tool reaches it
/// with <c>document_ids</c> (spec 007 D-14); the request's own rules (at most 50, no blank id) are checked here too.
/// </summary>
public sealed class DocumentIdsSearchTests(PostgresFixture postgres, QdrantFixture qdrant)
{
    private static readonly string[] LeaveAndNda = ["leave", "nda"];
    private static readonly string[] Unknown = ["no-such-document"];
    private static readonly string[] Hr = ["hr"];

    private static string Text(ulong chunk) => SampleCollections.CreateFlat().Points.Single(p => p.Id == chunk).EmbeddingText;

    private static SearchInput Input(IReadOnlyList<string>? documentIds, object? filters = null) => new(
        Text(1),
        filters is null
            ? new Dictionary<string, JsonElement>()
            : JsonSerializer.SerializeToElement(filters).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal),
        new Dictionary<string, JsonElement>(),
        TopK: 20,
        ScoreThreshold: null,
        MaxResultCharacters: null,
        DocumentIds: documentIds);

    private async Task<(QuorumApp App, HttpClient Admin, Guid TenantId)> StartAsync()
    {
        var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var collection = await qdrant.SeedSharedAsync(SampleCollections.CreateFlat);
        var url = (await qdrant.GetUrlAsync()).ToString();
        await KnowledgeBaseApi.RegisterOkAsync(admin, url, collection, "mapped");
        await KnowledgeBaseApi.RegisterOkAsync(admin, url, collection, "unmapped", mapping: KnowledgeBaseApi.TextOnly(), createFields: []);
        return (app, admin, await TenantScope.TenantIdAsync(app));
    }

    private static async Task<(Modules.KnowledgeBases.Contracts.KnowledgeBaseSearchResponse Response, bool Success)> SearchAsync(QuorumApp app, Guid tenantId, SearchInput input, params string[] names)
    {
        await using var scope = TenantScope.Open(app, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
        var bases = await db.KnowledgeBases.Where(k => names.Contains(k.Name)).ToListAsync(TestContext.Current.CancellationToken);
        var result = await scope.ServiceProvider.GetRequiredService<KnowledgeBaseSearchEngine>().SearchAsync(bases, input, TestContext.Current.CancellationToken);
        return (result.IsSuccess ? result.Value : null!, result.IsSuccess);
    }

    [Fact] // AC-41
    public async Task OnlyChunksOfTheListedDocumentsComeBack()
    {
        var (app, admin, tenantId) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        var (response, success) = await SearchAsync(app, tenantId, Input(LeaveAndNda), "mapped");

        Assert.True(success);
        Assert.Equal(LeaveAndNda, response.Chunks.Select(c => c.DocumentId).Distinct().Order(StringComparer.Ordinal));
        Assert.NotEmpty(response.Chunks);
    }

    [Fact] // AC-41
    public async Task TheListIsCombinedWithTheFiltersInAnd()
    {
        var (app, admin, tenantId) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        var (response, success) = await SearchAsync(app, tenantId, Input(LeaveAndNda, new { department = Hr }), "mapped");

        Assert.True(success);
        Assert.Equal(["leave"], response.Chunks.Select(c => c.DocumentId).Distinct());
    }

    [Fact] // AC-41
    public async Task AnIdNoDocumentHasMatchesNothingAndTheSearchSucceeds()
    {
        var (app, admin, tenantId) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        var (response, success) = await SearchAsync(app, tenantId, Input(Unknown), "mapped");

        Assert.True(success);
        Assert.Empty(response.Chunks);
        Assert.Empty(response.FailedKnowledgeBases);
    }

    [Fact] // AC-41
    public async Task AKnowledgeBaseWithoutADocumentIdIsSkippedAndNamedWhileTheOthersAreSearched()
    {
        var (app, admin, tenantId) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        var (response, success) = await SearchAsync(app, tenantId, Input(LeaveAndNda), "mapped", "unmapped");

        Assert.True(success);
        Assert.Equal(["unmapped"], response.SkippedKnowledgeBases);
        Assert.All(response.Chunks, chunk => Assert.Equal("mapped", chunk.KnowledgeBase));
        Assert.NotEmpty(response.Chunks);
    }

    [Fact] // AC-37
    public void AtMostFiftyDistinctIdsAreAccepted()
    {
        var fifty = Enumerable.Range(0, 50).Select(n => $"doc-{n}").ToList();

        Assert.True(SearchRules.DocumentIds(fifty).IsSuccess);
        Assert.True(SearchRules.DocumentIds([.. fifty, .. fifty]).IsSuccess);

        var tooMany = SearchRules.DocumentIds([.. fifty, "one-more"]);
        Assert.False(tooMany.IsSuccess);
        Assert.Equal(Modules.KnowledgeBases.Contracts.KnowledgeBaseSearchErrors.InvalidRequest, tooMany.Error.Code);
    }

    [Fact]
    public void ABlankIdIsRefusedAndNoListIsNoRestriction()
    {
        Assert.False(SearchRules.DocumentIds(["leave", " "]).IsSuccess);
        Assert.Null(SearchRules.DocumentIds(null).Value);
        Assert.Null(SearchRules.DocumentIds([]).Value);
        Assert.Equal(["leave"], SearchRules.DocumentIds([" leave ", "leave"]).Value);
    }

    [Fact]
    public async Task WithoutTheListEveryKnowledgeBaseIsSearchedAsBefore()
    {
        var (app, admin, tenantId) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        var (response, success) = await SearchAsync(app, tenantId, Input(documentIds: null), "mapped", "unmapped");

        Assert.True(success);
        Assert.Empty(response.SkippedKnowledgeBases);
        Assert.Contains(response.Chunks, c => c.KnowledgeBase == "unmapped");
        Assert.Contains(response.Chunks, c => c.KnowledgeBase == "mapped");
    }
}
