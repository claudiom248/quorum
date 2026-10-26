using System.Text.Json.Nodes;

namespace Quorum.Modules.KnowledgeBases.Storage;

/// <summary>The longest one call may take while inspecting or verifying (non-functional requirements); searching has its own limit.</summary>
internal static class StoreLimits
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
}

/// <param name="Name">The vector's name, or <see langword="null"/> for the source's single unnamed vector.</param>
/// <param name="Size">The dimensions, or 0 when the source does not say and no record showed them.</param>
/// <param name="Distance">The store's name for the metric, shown as it is.</param>
internal sealed record VectorInfo(string? Name, int Size, string Distance);

/// <summary>What a store says about a source (a collection, a relation).</summary>
/// <param name="IndexedPaths">Path → the store's data type, for the paths that have an index.</param>
/// <param name="RecordCount">How many records the source holds, when the store says.</param>
/// <param name="IndexesKnown"><see langword="false"/> when the source has no indexes of its own to look at (a view), so a missing index says nothing about it.</param>
internal sealed record SourceInfo(IReadOnlyList<VectorInfo> Vectors, IReadOnlyDictionary<string, string> IndexedPaths, long? RecordCount, bool IndexesKnown = true);

/// <param name="Score">The store's score; whether higher or lower is better depends on the metric.</param>
/// <param name="Row">Only the paths asked for.</param>
internal sealed record StoreHit(double Score, JsonObject Row);
