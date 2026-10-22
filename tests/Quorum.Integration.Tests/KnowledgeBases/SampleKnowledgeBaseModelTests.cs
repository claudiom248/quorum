using System.Net.Http.Json;
using System.Text.Json;
using Quorum.Integration.Tests.Fixtures;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBases;

/// <summary>The sample collections seeded with a real embedding model (the walk-through of docs/knowledge-bases.md), played by the fake model server.</summary>
public sealed class SampleKnowledgeBaseModelTests(QdrantFixture qdrant)
{
    private const string Endpoint = "http://embeddings.test/v1";
    private const string Model = "fake-embed";
    private const string DocumentPrefix = "search_document: ";
    private const int Dimensions = 16;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static float[] Embed(string text) => DeterministicEmbedding.Embed(text, Dimensions);

    private static ModelEmbedder CreateEmbedder(FakeModelServer server)
    {
        server.Serve(Endpoint, Model);
        server.ServeEmbeddings(Endpoint, Model, Embed);
        return ModelEmbedder.Create(new HttpClient(server), new Uri(Endpoint), Model, DocumentPrefix, apiKey: "test-key");
    }

    [Fact]
    public async Task TheModelEmbedsTheDocumentPrefixedTextWithTheKeyAndReportsItsDimensions()
    {
        var server = new FakeModelServer();
        var embedder = CreateEmbedder(server);

        var dimensions = await embedder.ProbeDimensionsAsync(Ct);
        var vector = await embedder.EmbedAsync("annual leave", Ct);

        Assert.Equal(Dimensions, dimensions);
        Assert.Equal(Embed(DocumentPrefix + "annual leave"), vector);
        var request = server.EmbeddingRequests[^1];
        Assert.Equal(Model, request.Model);
        Assert.Equal(DocumentPrefix + "annual leave", request.Input);
        Assert.Equal("Bearer test-key", request.Authorization);
    }

    [Fact]
    public async Task AnEndpointThatFailsIsReportedWithItsStatus()
    {
        var server = new FakeModelServer();
        var embedder = CreateEmbedder(server);
        server.FailEmbeddings(Endpoint, 500);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => embedder.EmbedAsync("anything", Ct));

        Assert.Contains("500", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheVectorsOfTheDefaultSizeFollowTheModel()
    {
        var named = SampleCollections.CreateNamedVectors().UsingModel(Dimensions);
        var otherSize = SampleCollections.CreateOtherVectorSize().UsingModel(Dimensions);

        Assert.Equal(new SampleVector(SampleCollections.DenseVectorName, Dimensions, FromModel: true), named.Vectors[0]);
        Assert.Equal(new SampleVector("summary", 32), named.Vectors[1]);
        Assert.Equal(new SampleVector(null, 128), Assert.Single(otherSize.Vectors));
    }

    [Fact]
    public async Task ACollectionSeededWithTheModelHasItsDimensionAndIsRankedByIt()
    {
        var name = QdrantFixture.NewPrefix() + SampleCollections.Flat;
        var embedder = CreateEmbedder(new FakeModelServer());
        var url = await qdrant.GetRestUrlAsync();
        using var http = new HttpClient { BaseAddress = url };
        await new QdrantSeeder(http, embedder).SeedAsync(SampleCollections.CreateFlat(name).UsingModel(Dimensions), Ct);

        var info = (await http.GetFromJsonAsync<JsonElement>($"collections/{name}", Ct)).GetProperty("result");
        using var response = await http.PostAsJsonAsync(
            $"collections/{name}/points/query",
            new { query = Embed(DocumentPrefix + "Passwords must have at least fourteen characters"), limit = 1, with_payload = true },
            Ct);
        var best = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("result").GetProperty("points")[0];

        Assert.Equal(Dimensions, info.GetProperty("config").GetProperty("params").GetProperty("vectors").GetProperty("size").GetInt32());
        Assert.Equal("security", best.GetProperty("payload").GetProperty("document_id").GetString());
    }

    [Fact]
    public async Task AVectorFromAModelWithoutAnEmbedderIsRefused()
    {
        var name = QdrantFixture.NewPrefix() + SampleCollections.Flat;
        var url = await qdrant.GetRestUrlAsync();
        using var http = new HttpClient { BaseAddress = url };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new QdrantSeeder(http).SeedAsync(SampleCollections.CreateFlat(name).UsingModel(Dimensions), Ct));
    }
}
