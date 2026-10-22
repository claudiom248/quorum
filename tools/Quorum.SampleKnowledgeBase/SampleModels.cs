using System.Text.Json.Nodes;

namespace Quorum.SampleKnowledgeBase;

/// <summary>
/// A vector of a collection. A <see langword="null"/> <paramref name="Name"/> is the collection's single unnamed vector;
/// <paramref name="FromModel"/> says its values come from a real embedding model instead of <see cref="DeterministicEmbedding"/>.
/// </summary>
public sealed record SampleVector(string? Name, int Size, string Distance = "Cosine", bool FromModel = false);

/// <summary>A payload index Qdrant keeps for faster filters. <paramref name="Schema"/> is Qdrant's: keyword, integer, float, bool or datetime.</summary>
public sealed record SamplePayloadIndex(string Path, string Schema);

/// <summary>A point: the payload as the data owner's ingestion wrote it, and the text its vectors are computed from.</summary>
public sealed record SamplePoint(ulong Id, string EmbeddingText, JsonObject Payload);

/// <summary>A collection as an ETL would leave it, with the pasteable JSON Schema of its payload when it has one.</summary>
public sealed record SampleDataset(
    string Name,
    string Description,
    IReadOnlyList<SampleVector> Vectors,
    IReadOnlyList<SamplePayloadIndex> Indexes,
    IReadOnlyList<SamplePoint> Points,
    string? Schema = null)
{
    /// <summary>
    /// The same collection with every vector of the default size made by a model of <paramref name="dimensions"/> dimensions.
    /// The vectors that are different on purpose (another size, the 'summary' vector) stay deterministic.
    /// </summary>
    public SampleDataset UsingModel(int dimensions) => this with
    {
        Vectors = Vectors
            .Select(vector => vector.Size == DeterministicEmbedding.DefaultDimensions ? vector with { Size = dimensions, FromModel = true } : vector)
            .ToList(),
    };
}
