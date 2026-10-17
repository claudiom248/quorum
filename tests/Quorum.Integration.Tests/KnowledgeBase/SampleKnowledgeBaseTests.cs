using System.Net.Http.Json;
using System.Text.Json;
using Quorum.Integration.Tests.Fixtures;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBase;

/// <summary>AC-51 (the generator part): the sample collections of D-20 exist in Qdrant and can be queried with the deterministic embedding.</summary>
public sealed class SampleKnowledgeBaseTests(QdrantFixture qdrant)
{
    private static readonly string[] Departments = ["hr", "it"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> GetAsync(HttpClient http, string path) =>
        (await http.GetFromJsonAsync<JsonElement>(path, Ct)).GetProperty("result");

    [Fact]
    public async Task EveryCaseOfTheSpecIsSeeded()
    {
        var prefix = QdrantFixture.NewPrefix();
        await qdrant.SeedAsync(SampleCollections.All(prefix));
        using var http = new HttpClient { BaseAddress = await qdrant.GetRestUrlAsync() };

        var flat = await GetAsync(http, $"collections/{prefix}{SampleCollections.Flat}");
        var nested = await GetAsync(http, $"collections/{prefix}{SampleCollections.Nested}");
        var tags = await GetAsync(http, $"collections/{prefix}{SampleCollections.Tags}");
        var unindexed = await GetAsync(http, $"collections/{prefix}{SampleCollections.Unindexed}");
        var named = await GetAsync(http, $"collections/{prefix}{SampleCollections.NamedVectors}");
        var otherSize = await GetAsync(http, $"collections/{prefix}{SampleCollections.OtherVectorSize}");
        var empty = await GetAsync(http, $"collections/{prefix}{SampleCollections.Empty}");

        Assert.Equal(10, flat.GetProperty("points_count").GetInt32());
        Assert.Equal(64, flat.GetProperty("config").GetProperty("params").GetProperty("vectors").GetProperty("size").GetInt32());
        Assert.Equal("datetime", flat.GetProperty("payload_schema").GetProperty("published_on").GetProperty("data_type").GetString());

        Assert.True(nested.GetProperty("payload_schema").TryGetProperty("meta.year", out _));

        Assert.Equal("keyword", tags.GetProperty("payload_schema").GetProperty("tags").GetProperty("data_type").GetString());

        Assert.Empty(unindexed.GetProperty("payload_schema").EnumerateObject());

        var vectors = named.GetProperty("config").GetProperty("params").GetProperty("vectors");
        Assert.Equal(64, vectors.GetProperty("dense").GetProperty("size").GetInt32());
        Assert.Equal(32, vectors.GetProperty("summary").GetProperty("size").GetInt32());

        Assert.Equal(128, otherSize.GetProperty("config").GetProperty("params").GetProperty("vectors").GetProperty("size").GetInt32());
        Assert.Equal(0, empty.GetProperty("points_count").GetInt32());
    }

    [Theory]
    [InlineData("How many days of annual leave do employees get", "leave")]
    [InlineData("Passwords need multi factor authentication", "security")]
    [InlineData("Which travel expenses and meals are reimbursed", "travel")]
    [InlineData("Visitors must wear a badge at the reception", "office")]
    public async Task AQuestionFindsItsOwnChunkWithTheDeterministicEmbedding(string question, string expectedDocument)
    {
        var name = QdrantFixture.NewPrefix() + SampleCollections.Flat;
        await qdrant.SeedAsync([SampleCollections.CreateFlat(name)]);
        using var http = new HttpClient { BaseAddress = await qdrant.GetRestUrlAsync() };

        using var response = await http.PostAsJsonAsync(
            $"collections/{name}/points/query",
            new { query = DeterministicEmbedding.Embed(question), limit = 1, with_payload = true },
            Ct);
        var best = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("result").GetProperty("points")[0];

        Assert.Equal(expectedDocument, best.GetProperty("payload").GetProperty("document_id").GetString());
    }

    [Fact]
    public async Task AFilterOnTheIndexedFieldsNarrowsTheResults()
    {
        var name = QdrantFixture.NewPrefix() + SampleCollections.Flat;
        await qdrant.SeedAsync([SampleCollections.CreateFlat(name)]);
        using var http = new HttpClient { BaseAddress = await qdrant.GetRestUrlAsync() };

        using var response = await http.PostAsJsonAsync(
            $"collections/{name}/points/query",
            new
            {
                query = DeterministicEmbedding.Embed("policy"),
                limit = 20,
                with_payload = true,
                filter = new { must = new object[] { new { key = "department", match = new { any = Departments } }, new { key = "year", range = new { gte = 2024 } } } },
            },
            Ct);
        var points = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("result").GetProperty("points").EnumerateArray().ToList();

        Assert.NotEmpty(points);
        Assert.All(points, point =>
        {
            Assert.Contains(point.GetProperty("payload").GetProperty("department").GetString(), Departments);
            Assert.True(point.GetProperty("payload").GetProperty("year").GetInt32() >= 2024);
        });
    }
}
