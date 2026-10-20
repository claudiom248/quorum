using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.Tools;
using Quorum.Modules.KnowledgeBases.Contracts;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Mapping;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBases.Search;

/// <summary>The search the rag tool of the Agents module calls (<see cref="IKnowledgeBaseSearch"/>), over several knowledge bases and against a real Qdrant.</summary>
public sealed class KnowledgeBaseSearchTests(PostgresFixture postgres, QdrantFixture qdrant)
{
    private static readonly string[] Hr = ["hr"];
    private static readonly string[] Finance = ["finance"];
    private static readonly string[] HrAndLegal = ["hr", "legal"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Text(ulong chunk) => SampleCollections.CreateFlat().Points.Single(p => p.Id == chunk).EmbeddingText;

    private sealed record Setup(QuorumApp App, HttpClient Admin, Guid TenantId, Guid AssistantId, string Url) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Admin.Dispose();
            await App.DisposeAsync();
        }
    }

    private async Task<Setup> StartAsync(FakeTimeProvider? clock = null)
    {
        var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, clock);
        var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        return new Setup(app, admin, await TenantScope.TenantIdAsync(app), await SearchApi.AssistantIdAsync(app), (await qdrant.GetUrlAsync()).ToString());
    }

    private async Task<string> SeedAsync(Func<string, SampleDataset> dataset, string standardName)
    {
        var name = QdrantFixture.NewPrefix() + standardName;
        await qdrant.SeedAsync([dataset(name)]);
        return name;
    }

    private async Task<Guid> RegisterFlatAsync(Setup setup, string name, string? collection = null)
    {
        collection ??= await SeedAsync(SampleCollections.CreateFlat, SampleCollections.Flat);
        return (await KnowledgeBaseApi.RegisterOkAsync(setup.Admin, setup.Url, collection, name)).Id;
    }

    private async Task<Guid> RegisterNestedAsync(Setup setup, string name)
    {
        var collection = await SeedAsync(SampleCollections.CreateNested, SampleCollections.Nested);
        return (await KnowledgeBaseApi.RegisterOkAsync(setup.Admin, setup.Url, collection, name, mapping: SearchApi.NestedMapping)).Id;
    }

    /// <summary>A knowledge base with no mapped metadata field at all.</summary>
    private async Task<Guid> RegisterBareAsync(Setup setup, string name)
    {
        var collection = await SeedAsync(SampleCollections.CreateFlat, SampleCollections.Flat);
        return (await KnowledgeBaseApi.RegisterOkAsync(setup.Admin, setup.Url, collection, name, mapping: KnowledgeBaseApi.TextOnly(), createFields: [])).Id;
    }

    [Fact] // AC-24
    public async Task AQueryFindsTheChunkThatSaysIt()
    {
        await using var setup = await StartAsync();
        var kb = await RegisterFlatAsync(setup, "policies");

        var result = await SearchApi.RunAsync(setup.App, setup.TenantId, SearchApi.Request(setup.TenantId, setup.AssistantId, Text(7), [kb]));

        Assert.True(result.IsSuccess);
        var best = result.Value.Chunks[0];
        Assert.Equal(("policies", Text(7), "travel", "Travel expense rules", "2"), (best.KnowledgeBase, best.Text, best.DocumentId, best.Title, best.Position));
        Assert.Equal("https://intranet.example.test/policies/travel", best.Url);
        Assert.Equal(5, result.Value.Chunks.Count);
        Assert.Empty(result.Value.SkippedKnowledgeBases);
        Assert.Empty(result.Value.FailedKnowledgeBases);
        Assert.Equal(0, result.Value.UnusableChunks);
    }

    [Fact] // AC-27
    public async Task SeveralKnowledgeBasesAreInterleavedOneChunkOfEachInTurnAndEveryChunkNamesItsKnowledgeBase()
    {
        await using var setup = await StartAsync();
        var beta = await RegisterNestedAsync(setup, "beta");
        var alpha = await RegisterFlatAsync(setup, "alpha");

        var both = await SearchApi.RunAsync(setup.App, setup.TenantId, SearchApi.Request(setup.TenantId, setup.AssistantId, Text(1), [beta, alpha], topK: 6));
        var onlyBeta = await SearchApi.RunAsync(setup.App, setup.TenantId, SearchApi.Request(setup.TenantId, setup.AssistantId, Text(1), [beta], topK: 6));

        // By name, not in the order the ids came: alpha first.
        Assert.Equal(["alpha", "beta", "alpha", "beta", "alpha", "beta"], both.Value.Chunks.Select(c => c.KnowledgeBase));
        Assert.All(onlyBeta.Value.Chunks, chunk => Assert.Equal("beta", chunk.KnowledgeBase));
        Assert.Equal(6, onlyBeta.Value.Chunks.Count);
        var fromAlpha = both.Value.Chunks.Where(c => c.KnowledgeBase == "alpha").Select(c => c.Text);
        var fromBeta = both.Value.Chunks.Where(c => c.KnowledgeBase == "beta").Select(c => c.Text);
        Assert.Equal(onlyBeta.Value.Chunks.Take(3).Select(c => c.Text), fromBeta);
        Assert.Equal(3, fromAlpha.Count());
    }

    [Fact] // AC-25, AC-26
    public async Task TheFixedFilterAlwaysAppliesAndTheModelCanOnlyNarrowIt()
    {
        await using var setup = await StartAsync();
        var kb = await RegisterFlatAsync(setup, "policies");

        async Task<string[]> DocumentsAsync(object? filters, object? fixedFilters)
        {
            var result = await SearchApi.RunAsync(
                setup.App,
                setup.TenantId,
                SearchApi.Request(setup.TenantId, setup.AssistantId, Text(1), [kb], filters, fixedFilters, topK: 20));
            return [.. result.Value.Chunks.Select(c => c.DocumentId!).Distinct().Order(StringComparer.Ordinal)];
        }

        Assert.Equal(["leave", "nda", "onboarding", "remote"], await DocumentsAsync(filters: null, new { department = HrAndLegal }));
        // The model asks for finance, which the binding excluded: nothing, and certainly not finance.
        Assert.Empty(await DocumentsAsync(new { department = Finance }, new { department = HrAndLegal }));
        // A range the model widens does not reach past the fixed one.
        Assert.Equal(["leave", "onboarding"], await DocumentsAsync(new { year_min = 2000 }, new { year_min = 2024, department = Hr }));
        Assert.Equal(["onboarding"], await DocumentsAsync(new { year_min = 2025 }, new { department = Hr }));
    }

    [Fact] // AC-28
    public async Task AKnowledgeBaseThatDidNotMapAFilteredFieldIsSkippedAndNamedAndNeverSearchedWithoutTheFilter()
    {
        await using var setup = await StartAsync();
        var complete = await RegisterFlatAsync(setup, "complete");
        var bare = await RegisterBareAsync(setup, "bare");

        var result = await SearchApi.RunAsync(
            setup.App,
            setup.TenantId,
            SearchApi.Request(setup.TenantId, setup.AssistantId, Text(1), [bare, complete], new { year_min = 2024 }, topK: 20));

        Assert.Equal(["bare"], result.Value.SkippedKnowledgeBases);
        Assert.All(result.Value.Chunks, chunk => Assert.Equal("complete", chunk.KnowledgeBase));
        Assert.Equal(["leave", "onboarding", "security"], result.Value.Chunks.Select(c => c.DocumentId).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact] // AC-28
    public async Task WhenEveryKnowledgeBaseIsSkippedTheResultIsAnError()
    {
        await using var setup = await StartAsync();
        var bare = await RegisterBareAsync(setup, "bare");
        await RegisterFlatAsync(setup, "complete");
        var before = setup.App.ModelServer.EmbeddingRequests.Count;

        var result = await SearchApi.RunAsync(setup.App, setup.TenantId, SearchApi.Request(setup.TenantId, setup.AssistantId, Text(1), [bare], new { year_min = 2024 }));

        Assert.False(result.IsSuccess);
        Assert.Equal("knowledge_base.filter_unsupported", result.Error.Code);
        Assert.Equal(before, setup.App.ModelServer.EmbeddingRequests.Count);
    }

    [Fact] // AC-29
    public async Task AtMostTopKChunksComeBackAndOnlyWholeOnesThatFitTheCap()
    {
        await using var setup = await StartAsync();
        var kb = await RegisterFlatAsync(setup, "policies");

        async Task<IReadOnlyList<RetrievedChunk>> ChunksAsync(int topK, int? cap) =>
            (await SearchApi.RunAsync(setup.App, setup.TenantId, SearchApi.Request(setup.TenantId, setup.AssistantId, Text(1), [kb], topK: topK, maxResultCharacters: cap))).Value.Chunks;

        var all = await ChunksAsync(topK: 5, cap: null);
        var firstTwo = all[0].Text.Length + all[1].Text.Length;

        Assert.Equal(3, (await ChunksAsync(topK: 3, cap: null)).Count);
        // The cap drops from the end and never cuts a chunk: the third no longer fits.
        var capped = await ChunksAsync(topK: 5, cap: firstTwo + 1);
        Assert.Equal(all.Take(2).Select(c => c.Text), capped.Select(c => c.Text));
        Assert.True(capped.Sum(c => c.Text.Length) <= firstTwo + 1);
        // Exactly fitting is fitting.
        Assert.Equal(2, (await ChunksAsync(topK: 5, cap: firstTwo)).Count);
    }

    [Fact] // AC-29
    public async Task WhenEvenTheFirstChunkIsTooLargeForTheCapNothingIsReturnedAndTheResultSaysSo()
    {
        await using var setup = await StartAsync();
        var kb = await RegisterFlatAsync(setup, "policies");

        var result = await SearchApi.RunAsync(
            setup.App,
            setup.TenantId,
            SearchApi.Request(setup.TenantId, setup.AssistantId, Text(1), [kb], maxResultCharacters: Text(1).Length - 1));

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Chunks);
        Assert.True(result.Value.FirstChunkExceededCap);
    }

    [Fact] // AC-30
    public async Task ChunksBelowTheScoreThresholdAreNotReturnedAndAnEmptyResultIsNotAnError()
    {
        await using var setup = await StartAsync();
        var kb = await RegisterFlatAsync(setup, "policies");

        var strict = await SearchApi.RunAsync(setup.App, setup.TenantId, SearchApi.Request(setup.TenantId, setup.AssistantId, Text(4), [kb], scoreThreshold: 0.99));
        var impossible = await SearchApi.RunAsync(setup.App, setup.TenantId, SearchApi.Request(setup.TenantId, setup.AssistantId, Text(4), [kb], scoreThreshold: 1.5));

        var only = Assert.Single(strict.Value.Chunks);
        Assert.Equal(Text(4), only.Text);
        Assert.True(only.Score >= 0.99);
        Assert.True(impossible.IsSuccess);
        Assert.Empty(impossible.Value.Chunks);
        Assert.Empty(impossible.Value.FailedKnowledgeBases);
    }

    [Fact] // AC-31
    public async Task AFilterValueThatDoesNotFitIsAnErrorAndNothingIsEmbeddedOrSent()
    {
        await using var setup = await StartAsync();
        var kb = await RegisterFlatAsync(setup, "policies");
        var before = setup.App.ModelServer.EmbeddingRequests.Count;

        var result = await SearchApi.RunAsync(setup.App, setup.TenantId, SearchApi.Request(setup.TenantId, setup.AssistantId, Text(1), [kb], new { published_on_from = "last spring" }));

        Assert.False(result.IsSuccess);
        Assert.Equal("knowledge_base.invalid_filter", result.Error.Code);
        Assert.Contains("published_on_from", result.Error.Description, StringComparison.Ordinal);
        Assert.Equal(before, setup.App.ModelServer.EmbeddingRequests.Count);
    }

    [Fact] // AC-31
    public async Task AFixedFilterThatDoesNotFitIsAnErrorThatDoesNotSayWhatIsInIt()
    {
        await using var setup = await StartAsync();
        var kb = await RegisterFlatAsync(setup, "policies");

        var result = await SearchApi.RunAsync(
            setup.App,
            setup.TenantId,
            SearchApi.Request(setup.TenantId, setup.AssistantId, Text(1), [kb], fixedFilters: new { confidential_only = "yes" }));

        Assert.Equal("knowledge_base.invalid_fixed_filter", result.Error.Code);
        Assert.DoesNotContain("confidential_only", result.Error.Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", 5)]
    [InlineData("   ", 5)]
    [InlineData("leave", 0)]
    [InlineData("leave", 21)]
    public async Task ARequestWithoutAQueryOrWithOutOfRangeLimitsIsRefused(string query, int topK)
    {
        await using var setup = await StartAsync();
        var kb = await RegisterFlatAsync(setup, "policies");

        var result = await SearchApi.RunAsync(setup.App, setup.TenantId, SearchApi.Request(setup.TenantId, setup.AssistantId, query, [kb], topK: topK));

        Assert.Equal("knowledge_base.invalid_search", result.Error.Code);
    }

    [Fact] // AC-34
    public async Task WhenOneKnowledgeBaseFailsTheOthersAnswerAndTheFailedOneIsNamedWithoutItsAddress()
    {
        await using var setup = await StartAsync();
        var good = await RegisterFlatAsync(setup, "good");
        var dead = (await KnowledgeBaseApi.RegisterOkAsync(setup.Admin, "http://localhost:1", "hidden_collection", "dead", mapping: KnowledgeBaseApi.TextOnly(), createFields: [])).Id;

        var result = await SearchApi.RunAsync(setup.App, setup.TenantId, SearchApi.Request(setup.TenantId, setup.AssistantId, Text(2), [dead, good]));

        Assert.True(result.IsSuccess);
        Assert.All(result.Value.Chunks, chunk => Assert.Equal("good", chunk.KnowledgeBase));
        Assert.Equal(5, result.Value.Chunks.Count);
        var failure = Assert.Single(result.Value.FailedKnowledgeBases);
        Assert.Equal(("dead", "knowledge_base.qdrant_unreachable"), (failure.Name, failure.Code));
        Assert.DoesNotContain("localhost", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hidden_collection", failure.Message, StringComparison.Ordinal);
    }

    [Fact] // AC-33, AC-34
    public async Task WhenEveryKnowledgeBaseFailsTheResultIsAnErrorThatNamesThem()
    {
        await using var setup = await StartAsync();
        var first = (await KnowledgeBaseApi.RegisterOkAsync(setup.Admin, "http://localhost:1", "docs", "first", mapping: KnowledgeBaseApi.TextOnly(), createFields: [])).Id;
        var second = (await KnowledgeBaseApi.RegisterOkAsync(setup.Admin, "http://localhost:2", "docs", "second", mapping: KnowledgeBaseApi.TextOnly(), createFields: [])).Id;

        var result = await SearchApi.RunAsync(setup.App, setup.TenantId, SearchApi.Request(setup.TenantId, setup.AssistantId, "leave", [first, second]));

        Assert.False(result.IsSuccess);
        Assert.Equal("knowledge_base.unavailable", result.Error.Code);
        Assert.Contains("first", result.Error.Description, StringComparison.Ordinal);
        Assert.Contains("second", result.Error.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost", result.Error.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // AC-33
    public async Task ADownEmbeddingEndpointIsAnErrorThatDoesNotCarryTheQuery()
    {
        await using var setup = await StartAsync();
        var kb = await RegisterFlatAsync(setup, "policies");
        setup.App.ModelServer.FailEmbeddings(QuorumApp.ModelBaseUrl, 503);

        var result = await SearchApi.RunAsync(setup.App, setup.TenantId, SearchApi.Request(setup.TenantId, setup.AssistantId, "a confidential question about layoffs", [kb]));

        Assert.Equal("knowledge_base.unavailable", result.Error.Code);
        Assert.DoesNotContain("layoffs", result.Error.Description, StringComparison.Ordinal);
    }

    [Fact] // AC-36
    public async Task ASearchThatTakesLongerThanFifteenSecondsEndsAsAFailedResultForTheKnowledgeBase()
    {
        var clock = new FakeTimeProvider();
        await using var setup = await StartAsync(clock);
        var kb = await RegisterFlatAsync(setup, "policies");
        setup.App.ModelServer.StallEmbeddings(QuorumApp.ModelBaseUrl, KnowledgeBaseApi.EmbeddingModel);
        var before = setup.App.ModelServer.EmbeddingRequests.Count;

        var search = SearchApi.RunAsync(setup.App, setup.TenantId, SearchApi.Request(setup.TenantId, setup.AssistantId, Text(1), [kb]));
        // The embedding request has reached the model and is hanging: only now does the time pass.
        await WaitUntilAsync(() => setup.App.ModelServer.EmbeddingRequests.Count > before);
        Assert.False(search.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(14));
        await Task.Delay(100, Ct);
        Assert.False(search.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(2));
        var result = await search.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.False(result.IsSuccess);
        Assert.Equal("knowledge_base.unavailable", result.Error.Code);
        Assert.Contains("15 seconds", result.Error.Description, StringComparison.Ordinal);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not met in time.");
            await Task.Delay(20, Ct);
        }
    }

    [Fact] // AC-44
    public async Task AnAssistantScopedKnowledgeBaseIsSearchedOnlyForTheAssistantsItListsEvenAfterTheScopeIsNarrowed()
    {
        await using var setup = await StartAsync();
        var collection = await SeedAsync(SampleCollections.CreateFlat, SampleCollections.Flat);
        var scope = new { kind = "Assistant", assistantIds = new[] { setup.AssistantId } };
        var kb = (await KnowledgeBaseApi.RegisterOkAsync(setup.Admin, setup.Url, collection, "assistant-only", scope: scope)).Id;
        var request = SearchApi.Request(setup.TenantId, setup.AssistantId, Text(1), [kb]);

        Assert.True((await SearchApi.RunAsync(setup.App, setup.TenantId, request)).IsSuccess);

        var narrowed = new { kind = "Assistant", assistantIds = new[] { Guid.NewGuid() } };
        using var update = await setup.Admin.PutAsJsonAsync(
            KnowledgeBaseApi.One(kb),
            KnowledgeBaseApi.Body(setup.Url, collection, "assistant-only", scope: narrowed),
            Ct);
        Assert.True(update.IsSuccessStatusCode);
        var before = setup.App.ModelServer.EmbeddingRequests.Count;

        var result = await SearchApi.RunAsync(setup.App, setup.TenantId, request);

        Assert.Equal("knowledge_base.not_found", result.Error.Code);
        Assert.Equal(before, setup.App.ModelServer.EmbeddingRequests.Count);
    }

    [Fact] // AC-44
    public async Task ADeletedKnowledgeBaseIsIgnoredAndTheOthersStillAnswer()
    {
        await using var setup = await StartAsync();
        var kept = await RegisterFlatAsync(setup, "kept");
        var gone = await RegisterFlatAsync(setup, "gone");
        using var deleted = await setup.Admin.DeleteAsync(KnowledgeBaseApi.One(gone), Ct);
        Assert.True(deleted.IsSuccessStatusCode);

        var result = await SearchApi.RunAsync(setup.App, setup.TenantId, SearchApi.Request(setup.TenantId, setup.AssistantId, Text(1), [gone, kept]));

        Assert.True(result.IsSuccess);
        Assert.All(result.Value.Chunks, chunk => Assert.Equal("kept", chunk.KnowledgeBase));
        Assert.Empty(result.Value.FailedKnowledgeBases);
    }

    [Fact] // AC-43, AC-44
    public async Task AKnowledgeBaseOfAnotherTenantIsSearchedOnlyWhenItsScopeReachesTheTenant()
    {
        await using var setup = await StartAsync();
        var collection = await SeedAsync(SampleCollections.CreateFlat, SampleCollections.Flat);

        var global = await AddForeignKnowledgeBaseAsync(setup, "global", collection, KnowledgeBaseScope.Global);
        var listed = await AddForeignKnowledgeBaseAsync(setup, "listed", collection, KnowledgeBaseScope.Tenant, setup.TenantId);
        var elsewhere = await AddForeignKnowledgeBaseAsync(setup, "elsewhere", collection, KnowledgeBaseScope.Tenant, Guid.NewGuid());
        var private_ = await AddForeignKnowledgeBaseAsync(setup, "private", collection, KnowledgeBaseScope.Tenant);
        var assistantOnly = await AddForeignKnowledgeBaseAsync(setup, "assistant-only", collection, KnowledgeBaseScope.Assistant);

        var result = await SearchApi.RunAsync(
            setup.App,
            setup.TenantId,
            SearchApi.Request(setup.TenantId, setup.AssistantId, Text(1), [global, listed, elsewhere, private_, assistantOnly], topK: 10));

        Assert.True(result.IsSuccess);
        Assert.Equal(["global", "listed"], result.Value.Chunks.Select(c => c.KnowledgeBase).Distinct().Order(StringComparer.Ordinal));
        Assert.Empty(result.Value.FailedKnowledgeBases);
    }

    [Fact] // AC-35 and the tenant isolation of the status
    public async Task ASearchOfAnotherTenantsKnowledgeBaseNeverChangesItsStatus()
    {
        await using var setup = await StartAsync();
        var foreign = await AddForeignKnowledgeBaseAsync(setup, "shared", "unused", KnowledgeBaseScope.Global, url: "http://localhost:1");

        var result = await SearchApi.RunAsync(setup.App, setup.TenantId, SearchApi.Request(setup.TenantId, setup.AssistantId, "leave", [foreign]));

        Assert.Equal("knowledge_base.unavailable", result.Error.Code);
        Assert.Equal("Available", await ToolFixtures.ScalarAsync<string>(setup.App, $"SELECT \"Status\" FROM knowledge_base.knowledge_bases WHERE \"Id\" = '{foreign}'"));
    }

    [Fact]
    public async Task ASearchForAnotherTenantThanTheRequestsIsRefusedAndNothingIsEmbedded()
    {
        await using var setup = await StartAsync();
        var kb = await RegisterFlatAsync(setup, "policies");
        var before = setup.App.ModelServer.EmbeddingRequests.Count;

        var result = await SearchApi.RunAsync(setup.App, setup.TenantId, SearchApi.Request(Guid.NewGuid(), setup.AssistantId, Text(1), [kb]));

        Assert.Equal("knowledge_base.not_found", result.Error.Code);
        Assert.Equal(before, setup.App.ModelServer.EmbeddingRequests.Count);
    }

    [Fact] // payload values that do not fit the mapping
    public async Task ChunksWithoutUsableTextAreSkippedAndCountedAndAMissingReferenceOnlyLeavesTheSourceLessPrecise()
    {
        await using var setup = await StartAsync();
        var collection = QdrantFixture.NewPrefix() + "ragged";
        const string good = "Contractors submit timesheets every Friday.";
        const string alsoGood = "Contractors are paid on the fifteenth of the month.";
        await qdrant.SeedAsync(
        [
            new SampleDataset(
                collection,
                "Payloads that do not all fit the mapping.",
                [new SampleVector(null, DeterministicEmbedding.DefaultDimensions)],
                [],
                [
                    new SamplePoint(1, good, new JsonObject { ["text"] = good, ["title"] = "Contractors", ["page"] = 3 }),
                    new SamplePoint(2, "Contractors without text", new JsonObject { ["title"] = "No text here" }),
                    new SamplePoint(3, "Contractors with text of the wrong type", new JsonObject { ["text"] = 42, ["title"] = "Wrong type" }),
                    new SamplePoint(4, alsoGood, new JsonObject { ["text"] = alsoGood, ["title"] = new JsonObject { ["nested"] = "object" } }),
                ]),
        ]);
        var mapping = new { roles = new { text = "text", title = "title", position = "page" }, fields = Array.Empty<object>() };
        var kb = (await KnowledgeBaseApi.RegisterOkAsync(setup.Admin, setup.Url, collection, "ragged", mapping: mapping, createFields: [])).Id;

        var result = await SearchApi.RunAsync(setup.App, setup.TenantId, SearchApi.Request(setup.TenantId, setup.AssistantId, "Contractors", [kb]));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.UnusableChunks);
        Assert.Equal(new[] { good, alsoGood }.Order(StringComparer.Ordinal), result.Value.Chunks.Select(c => c.Text).Order(StringComparer.Ordinal));
        var withReferences = result.Value.Chunks.Single(c => c.Text == good);
        Assert.Equal(("Contractors", "3"), (withReferences.Title, withReferences.Position));
        var withoutThem = result.Value.Chunks.Single(c => c.Text == alsoGood);
        Assert.Null(withoutThem.Title);
        Assert.Null(withoutThem.Position);
        Assert.Null(withoutThem.DocumentId);
        Assert.Null(withoutThem.Url);
    }

    private static async Task<Guid> AddForeignKnowledgeBaseAsync(
        Setup setup,
        string name,
        string collection,
        KnowledgeBaseScope scope,
        Guid? listedTenant = null,
        string? url = null)
    {
        var now = DateTimeOffset.UtcNow;
        var knowledgeBase = KnowledgeBase.Create(
            Guid.NewGuid(),
            name,
            $"{name} of another tenant.",
            new QdrantConnectionSettings(url ?? setup.Url, collection, VectorName: null, ProtectedApiKey: null),
            KnowledgeBaseApi.EmbeddingModel,
            queryPrefix: null,
            new KnowledgeBaseMapping(new MappingRoles("text", "document_id", "title", "page", "source_url"), []),
            scope,
            listedTenant is { } tenant ? [tenant] : [],
            scopeAssistantIds: [],
            now);
        knowledgeBase.MarkAvailable(now);

        await using var services = setup.App.Services.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
        db.KnowledgeBases.Add(knowledgeBase);
        await db.SaveChangesAsync(Ct);
        return knowledgeBase.Id;
    }
}
