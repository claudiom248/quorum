using System.Text.Json.Nodes;
using Quorum.Modules.KnowledgeBases.Contracts;
using Quorum.Modules.KnowledgeBases.Features.Search;
using Quorum.Modules.KnowledgeBases.Mapping;

namespace Quorum.Integration.Tests.KnowledgeBases.Search;

/// <summary>The parts of a search that need no store: how results of several knowledge bases are merged and cut, and how a payload becomes a chunk.</summary>
public sealed class SearchResultShapingTests
{
    private static RetrievedChunk Chunk(string knowledgeBase, int rank, int length = 10) =>
        new(knowledgeBase, Score: 1.0 / (rank + 1), Text: new string('x', length) + rank, DocumentId: null, Title: null, Position: null, Url: null);

    private static IReadOnlyList<RetrievedChunk> Chunks(string knowledgeBase, int count) => [.. Enumerable.Range(0, count).Select(rank => Chunk(knowledgeBase, rank))];

    [Fact] // AC-27
    public void TheBestOfEveryKnowledgeBaseComesBeforeTheSecondBestOfAny()
    {
        var merged = KnowledgeBaseSearchEngine.Interleave([Chunks("a", 3), Chunks("b", 3), Chunks("c", 3)]).ToList();

        Assert.Equal(
            ["a0", "b0", "c0", "a1", "b1", "c1", "a2", "b2", "c2"],
            merged.Select(c => c.KnowledgeBase + c.Text[^1]));
    }

    [Fact] // AC-27
    public void AShorterListJustRunsOutAndTheOthersGoOn()
    {
        var merged = KnowledgeBaseSearchEngine.Interleave([Chunks("a", 1), Chunks("b", 4), Chunks("c", 0)]).ToList();

        Assert.Equal(["a0", "b0", "b1", "b2", "b3"], merged.Select(c => c.KnowledgeBase + c.Text[^1]));
    }

    [Fact] // AC-27: the scores of different knowledge bases are never compared
    public void ABetterScoreInAnotherKnowledgeBaseDoesNotMoveAChunkUp()
    {
        var weak = new RetrievedChunk("a", 0.1, "weak", null, null, null, null);
        var strongSecond = new RetrievedChunk("b", 0.9, "strong second", null, null, null, null);
        var strongFirst = new RetrievedChunk("b", 0.95, "strong first", null, null, null, null);

        var merged = KnowledgeBaseSearchEngine.Interleave([[weak], [strongFirst, strongSecond]]).ToList();

        Assert.Equal(["weak", "strong first", "strong second"], merged.Select(c => c.Text));
    }

    [Fact] // AC-29
    public void WithoutACapEverythingIsKept()
    {
        var (kept, tooLarge) = KnowledgeBaseSearchEngine.ApplyCap(Chunks("a", 3), maxCharacters: null);

        Assert.Equal(3, kept.Count);
        Assert.False(tooLarge);
    }

    [Theory] // AC-29
    [InlineData(10_000, 3)]
    [InlineData(33, 3)]
    [InlineData(32, 2)]
    [InlineData(22, 2)]
    [InlineData(21, 1)]
    [InlineData(11, 1)]
    public void ChunksAreKeptFromTheFrontForAsLongAsTheirTextFitsAndNeverCut(int cap, int expected)
    {
        // Three chunks of 11 characters each.
        var (kept, tooLarge) = KnowledgeBaseSearchEngine.ApplyCap(Chunks("a", 3), cap);

        Assert.Equal(expected, kept.Count);
        Assert.False(tooLarge);
        Assert.All(kept, chunk => Assert.Equal(11, chunk.Text.Length));
    }

    [Fact] // AC-29
    public void AChunkThatFitsDoesNotPullInOneBehindItThatDoesNot()
    {
        var chunks = new[] { Chunk("a", 0, length: 5), Chunk("a", 1, length: 100), Chunk("a", 2, length: 5) };

        var (kept, _) = KnowledgeBaseSearchEngine.ApplyCap(chunks, maxCharacters: 50);

        // The cap drops from the end of the ranked list: the small chunk behind the large one is not a better answer than the large one.
        Assert.Single(kept);
    }

    [Fact] // edge case: the result cap
    public void WhenTheFirstChunkAloneIsOverTheCapNothingIsKeptAndTheResultSaysSo()
    {
        var (kept, tooLarge) = KnowledgeBaseSearchEngine.ApplyCap(Chunks("a", 3), maxCharacters: 10);

        Assert.Empty(kept);
        Assert.True(tooLarge);
    }

    [Fact]
    public void NothingFoundIsNotTooLarge()
    {
        var (kept, tooLarge) = KnowledgeBaseSearchEngine.ApplyCap([], maxCharacters: 10);

        Assert.Empty(kept);
        Assert.False(tooLarge);
    }

    private static readonly MappingRoles AllRoles = new("text", "doc.id", "meta.title", "page", "link");

    [Fact]
    public void APayloadBecomesAChunkWithTheReferencesOfItsMappedPaths()
    {
        var payload = JsonNode.Parse("""{"text": "Leave is 25 days.", "doc": {"id": "hr-7"}, "meta": {"title": "Leave policy"}, "page": 4, "link": "https://example.test/leave"}""")!.AsObject();

        var chunk = ChunkReader.Read("handbook", AllRoles, 0.75, payload);

        Assert.Equal(new RetrievedChunk("handbook", 0.75, "Leave is 25 days.", "hr-7", "Leave policy", "4", "https://example.test/leave"), chunk);
    }

    [Theory]
    [InlineData("""{"title": "no text"}""")]
    [InlineData("""{"text": ""}""")]
    [InlineData("""{"text": "   "}""")]
    [InlineData("""{"text": 7}""")]
    [InlineData("""{"text": ["a", "b"]}""")]
    [InlineData("""{"text": {"nested": "x"}}""")]
    [InlineData("""{"text": null}""")]
    public void APayloadWithoutUsableTextIsNoChunk(string json)
    {
        Assert.Null(ChunkReader.Read("handbook", AllRoles, 1, JsonNode.Parse(json)!.AsObject()));
    }

    [Fact]
    public void AReferenceOfTheWrongTypeIsLeftOutAndTheChunkStays()
    {
        var payload = JsonNode.Parse("""{"text": "Body.", "doc": {"id": ["a"]}, "meta": {"title": {"x": 1}}, "page": true, "link": ["u"]}""")!.AsObject();

        var chunk = ChunkReader.Read("handbook", AllRoles, 1, payload)!;

        Assert.Equal("Body.", chunk.Text);
        Assert.Null(chunk.DocumentId);
        Assert.Null(chunk.Title);
        Assert.Null(chunk.Position);
        Assert.Null(chunk.Url);
    }

    [Fact]
    public void ANumberedPositionAndDocumentIdAreWrittenAsPlainText()
    {
        var roles = new MappingRoles("text", "id", null, "page", null);

        var chunk = ChunkReader.Read("handbook", roles, 1, JsonNode.Parse("""{"text": "Body.", "id": 1234567890123, "page": 2.5}""")!.AsObject())!;

        Assert.Equal(("1234567890123", "2.5"), (chunk.DocumentId, chunk.Position));
    }

    [Fact]
    public void ALongTitleIsCutButALongLinkIsDroppedBecauseACutLinkPointsElsewhere()
    {
        var payload = new JsonObject
        {
            ["text"] = "Body.",
            ["meta"] = new JsonObject { ["title"] = new string('t', 500) },
            ["link"] = "https://example.test/" + new string('p', 2100),
        };

        var chunk = ChunkReader.Read("handbook", AllRoles, 1, payload)!;

        Assert.Equal(300, chunk.Title!.Length);
        Assert.Null(chunk.Url);
    }

    [Fact]
    public void OnlyTheMappedPathsAreAskedOfTheStoreOnceEach()
    {
        var paths = ChunkReader.PathsOf(new MappingRoles("body", null, "body", "page", null));

        Assert.Equal(["body", "page"], paths);
    }
}
