using System.Text.Json.Nodes;
using Quorum.Modules.KnowledgeBases.Mapping;

namespace Quorum.Modules.KnowledgeBases.Storage;

/// <summary>
/// What a lookup counts is decided by the caller: with <see cref="DocumentIdPath"/> the distinct documents that carry each value,
/// otherwise the records that satisfy <see cref="Conditions"/> (a counted point makes that one record per document).
/// </summary>
/// <param name="Path">The field whose values are listed; a list field contributes each of its values.</param>
/// <param name="Conditions">All of them must hold.</param>
/// <param name="MaxValues">The ceiling of values to return, the ones with the most records first.</param>
/// <param name="MaxRecords">With <paramref name="DocumentIdPath"/>: how many records are read at most before the answer is cut.</param>
internal sealed record DistinctValuesQuery(string Path, IReadOnlyList<FilterCondition> Conditions, string? DocumentIdPath, int MaxValues, int MaxRecords);

internal sealed record DistinctValue(string Value, int Count);

/// <param name="Truncated">There are more values than <see cref="DistinctValuesQuery.MaxValues"/>, or the records read stopped at the cap.</param>
internal sealed record DistinctValues(IReadOnlyList<DistinctValue> Values, bool Truncated);

/// <param name="Paths">The paths to read from each record, and nothing else.</param>
/// <param name="Cursor">The <see cref="RecordPage.Cursor"/> of the previous page, or <see langword="null"/> for the first.</param>
internal sealed record RecordQuery(IReadOnlyList<FilterCondition> Conditions, IReadOnlyList<string> Paths, int Limit, string? Cursor);

/// <param name="Rows">Only the paths asked for.</param>
/// <param name="Cursor">Opaque; <see langword="null"/> on the last page.</param>
internal sealed record RecordPage(IReadOnlyList<JsonObject> Rows, string? Cursor);

/// <summary>
/// The optional capability of a kind that can answer questions about the values of its records without searching by meaning (spec 007
/// D-15). A kind that does not declare it has no lookups: its fields cannot be marked listable.
/// </summary>
internal interface ILookupsCapability : IStoreCapability
{
    /// <summary>
    /// Why a computation over <paramref name="requiredPaths"/> would fail on this source, in words for the administrator, or
    /// <see langword="null"/> when it can run: the store is too old, or a path has no index it needs.
    /// </summary>
    /// <exception cref="StoreException">The store cannot be used.</exception>
    Task<string?> CheckAsync(IStoreConnection connection, IReadOnlyCollection<string> requiredPaths, CancellationToken cancellationToken);

    /// <summary>What an inspection says of a path a lookup reads (the counted point, the document id) that the source has no index for.</summary>
    InspectWarning UnindexedWarning(string path, string role);

    /// <summary>The distinct values of a field with the number of documents (or records) of each, most first.</summary>
    /// <exception cref="StoreException">The store cannot be used.</exception>
    Task<DistinctValues> DistinctValuesAsync(IStoreConnection connection, DistinctValuesQuery query, CancellationToken cancellationToken);

    /// <summary>How many records satisfy every condition, exactly.</summary>
    /// <exception cref="StoreException">The store cannot be used.</exception>
    Task<long> CountAsync(IStoreConnection connection, IReadOnlyList<FilterCondition> conditions, CancellationToken cancellationToken);

    /// <summary>A page of the records that satisfy every condition, in the store's own order.</summary>
    /// <exception cref="StoreException">The store cannot be used.</exception>
    Task<RecordPage> ScanAsync(IStoreConnection connection, RecordQuery query, CancellationToken cancellationToken);
}
