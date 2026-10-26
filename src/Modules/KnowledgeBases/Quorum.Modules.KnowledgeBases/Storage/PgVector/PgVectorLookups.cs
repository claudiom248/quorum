using Quorum.Modules.KnowledgeBases.Mapping;

namespace Quorum.Modules.KnowledgeBases.Storage.PgVector;

/// <summary>
/// The lookups of a relation (spec 008 Amendment 2): distinct values with counts, a count and a scan, as exact statements in the
/// read-only transaction of the connector. They never need an index: without one they only take longer, so the advice about indexes is
/// a warning (A2-8) and not a failure of the check (A2-7).
/// </summary>
internal sealed class PgVectorLookups(PgVectorConnector connector) : ILookupsCapability
{
    public Task<string?> CheckAsync(IStoreConnection connection, IReadOnlyCollection<string> requiredPaths, CancellationToken cancellationToken) =>
        connector.CheckLookupsAsync(Own(connection), requiredPaths, cancellationToken);

    public InspectWarning UnindexedWarning(string path, string role) => new(
        WarningCodes.LookupFieldUnindexed,
        $"'{path}' ({role}) has no index: lookups read the whole relation to compute it, which can take long on a large one. Ask the data owner to index it.",
        path);

    public Task<DistinctValues> DistinctValuesAsync(IStoreConnection connection, DistinctValuesQuery query, CancellationToken cancellationToken) =>
        connector.DistinctValuesAsync(Own(connection), query, cancellationToken);

    public Task<long> CountAsync(IStoreConnection connection, IReadOnlyList<FilterCondition> conditions, CancellationToken cancellationToken) =>
        connector.CountAsync(Own(connection), conditions, cancellationToken);

    public Task<RecordPage> ScanAsync(IStoreConnection connection, RecordQuery query, CancellationToken cancellationToken) =>
        connector.ScanAsync(Own(connection), query, cancellationToken);

    private static PgVectorConnection Own(IStoreConnection connection) =>
        connection as PgVectorConnection ?? throw new ArgumentException($"A {connection.GetType().Name} is not a PgVector connection.", nameof(connection));
}
