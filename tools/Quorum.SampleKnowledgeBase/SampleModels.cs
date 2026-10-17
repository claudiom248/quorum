using System.Text.Json.Nodes;

namespace Quorum.SampleKnowledgeBase;

/// <summary>A vector of a collection. A <see langword="null"/> <paramref name="Name"/> is the collection's single unnamed vector.</summary>
public sealed record SampleVector(string? Name, int Size, string Distance = "Cosine");

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
    string? Schema = null);
