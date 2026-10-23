using System.Globalization;
using System.Text.Json.Nodes;
using Quorum.Modules.KnowledgeBases.Mapping;

namespace Quorum.Modules.KnowledgeBases.Storage.Qdrant;

/// <summary>
/// Qdrant's lookups (spec 007 D-15): the facet API for the distinct values of an indexed field, a payload scroll for the distinct
/// documents when there is no counted point, and exact counts. All of it reads; Quorum never writes to the store.
/// </summary>
internal sealed class QdrantLookups(QdrantConnector connector) : ILookupsCapability
{
    /// <summary>How many records one page of the scan that counts distinct documents reads.</summary>
    internal const int ScanPageSize = 1000;

    /// <summary>The (value, document) pairs a scan keeps in memory at most; past it the list is cut and marked truncated.</summary>
    internal const int MaxTrackedPairs = 10_000_000;

    public async Task<string?> CheckAsync(IStoreConnection connection, IReadOnlyCollection<string> requiredPaths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requiredPaths);

        var qdrant = Own(connection);
        if (await connector.GetVersionAsync(qdrant, cancellationToken) is { } version && !QdrantConnector.SupportsLookups(version))
        {
            return $"The server runs Qdrant {version}; lookups need {QdrantConnector.LookupsMinimumVersion} or later. Upgrade Qdrant and refresh the lookup.";
        }

        if (await connector.GetCollectionAsync(qdrant, cancellationToken) is not { } source)
        {
            return "The collection does not exist on that server.";
        }

        var missing = requiredPaths.Where(path => !source.IndexedPaths.ContainsKey(path)).Order(StringComparer.Ordinal).ToList();
        return missing.Count == 0
            ? null
            : $"'{string.Join("', '", missing)}' {(missing.Count == 1 ? "has" : "have")} no payload index in Qdrant, which lookups need: ask the data owner to create "
              + $"{(missing.Count == 1 ? "it" : "them")} and refresh the lookup.";
    }

    public InspectWarning UnindexedWarning(string path, string role) => new(
        WarningCodes.LookupFieldUnindexed,
        $"'{path}' ({role}) has no payload index in Qdrant: lookups need one on it, and without it they fail or take very long. Ask the data owner to index it.",
        path);

    public async Task<DistinctValues> DistinctValuesAsync(IStoreConnection connection, DistinctValuesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var qdrant = Own(connection);
        if (query.DocumentIdPath is null)
        {
            // One more than the ceiling tells whether the list was cut.
            var hits = await connector.FacetAsync(qdrant, query.Path, query.Conditions, query.MaxValues + 1, cancellationToken);
            var values = hits.Take(query.MaxValues).Select(hit => new DistinctValue(hit.Value, (int)Math.Min(hit.Count, int.MaxValue))).ToList();
            return new DistinctValues(values, hits.Count > query.MaxValues);
        }

        return await CountDocumentsAsync(qdrant, query, query.DocumentIdPath, cancellationToken);
    }

    public Task<long> CountAsync(IStoreConnection connection, IReadOnlyList<FilterCondition> conditions, CancellationToken cancellationToken) =>
        connector.CountAsync(Own(connection), conditions, cancellationToken);

    public Task<RecordPage> ScanAsync(IStoreConnection connection, RecordQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return connector.ScrollPageAsync(Own(connection), query.Conditions, query.Paths, query.Limit, query.Cursor, cancellationToken);
    }

    /// <summary>
    /// Counts the distinct documents of each value by reading the document id and the field of the records, a page at a time, up to the
    /// cap. It is the heavy way and runs in the background only.
    /// </summary>
    private async Task<DistinctValues> CountDocumentsAsync(QdrantConnection connection, DistinctValuesQuery query, string documentIdPath, CancellationToken cancellationToken)
    {
        string[] paths = [documentIdPath, query.Path];
        // Each document id is held once and the values refer to it by number, so memory follows documents plus pairs, not their text.
        var documentNumbers = new Dictionary<string, int>(StringComparer.Ordinal);
        var documentsOf = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var pairs = 0;
        var read = 0;
        var capped = false;
        string? cursor = null;

        do
        {
            var limit = Math.Min(ScanPageSize, query.MaxRecords - read);
            var page = await connector.ScrollPageAsync(connection, query.Conditions, paths, limit, cursor, cancellationToken);
            foreach (var row in page.Rows)
            {
                if (TextOf(PayloadPath.Read(row, documentIdPath)) is not { } document)
                {
                    continue;
                }

                foreach (var value in TextsOf(PayloadPath.Read(row, query.Path)))
                {
                    if (!documentsOf.TryGetValue(value, out var documents))
                    {
                        documents = [];
                        documentsOf[value] = documents;
                    }

                    if (!documentNumbers.TryGetValue(document, out var number))
                    {
                        number = documentNumbers.Count;
                        documentNumbers[document] = number;
                    }

                    if (documents.Add(number))
                    {
                        pairs++;
                    }
                }
            }

            read += page.Rows.Count;
            cursor = page.Cursor;
            capped = cursor is not null && (read >= query.MaxRecords || pairs >= MaxTrackedPairs);
        }
        while (cursor is not null && !capped);

        var ranked = documentsOf
            .Select(pair => new DistinctValue(pair.Key, pair.Value.Count))
            .OrderByDescending(v => v.Count)
            .ThenBy(v => v.Value, StringComparer.Ordinal)
            .ToList();
        return new DistinctValues([.. ranked.Take(query.MaxValues)], capped || ranked.Count > query.MaxValues);
    }

    private static IEnumerable<string> TextsOf(JsonNode? node) => node switch
    {
        JsonArray array => array.Select(TextOf).OfType<string>(),
        _ => TextOf(node) is { } text ? [text] : [],
    };

    // Identifiers are text or numbers in a payload; the same document is the same text either way.
    private static string? TextOf(JsonNode? node) => node is JsonValue value
        ? value.TryGetValue<string>(out var text) ? text
        : value.TryGetValue<long>(out var number) ? number.ToString(CultureInfo.InvariantCulture)
        : null
        : null;

    private static QdrantConnection Own(IStoreConnection connection) =>
        connection as QdrantConnection ?? throw new ArgumentException($"A {connection.GetType().Name} is not a Qdrant connection.", nameof(connection));
}
