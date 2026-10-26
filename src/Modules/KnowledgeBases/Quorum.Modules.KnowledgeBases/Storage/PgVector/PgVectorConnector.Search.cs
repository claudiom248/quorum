using System.Text.Json.Nodes;
using Npgsql;

namespace Quorum.Modules.KnowledgeBases.Storage.PgVector;

internal sealed partial class PgVectorConnector
{
    /// <summary>The first version with iterative index scans: a filtered approximate search keeps scanning until it has enough rows (D-12).</summary>
    private static readonly Version IterativeScanVersion = new(0, 8);

    /// <summary>The most an approximate index is asked to consider at once; the default is 40.</summary>
    private const int MaxEfSearch = 1000;

    internal static string QualifiedName(PgVectorRelation relation) => Table(relation);

    /// <summary>
    /// The rows nearest to the vector that satisfy every condition, best first, with the columns at <paramref name="query"/>'s paths and
    /// nothing else. It is one read-only transaction with a statement timeout, as everything else the connector does.
    /// </summary>
    /// <exception cref="StoreException">The database cannot be used, or the relation or a column the mapping names is gone.</exception>
    public async Task<IReadOnlyList<StoreHit>> SearchAsync(PgVectorConnection connection, StoreQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(query);

        return await ReadAsync(
            connection,
            StoreLimits.SearchTimeout,
            async (db, transaction, token) =>
            {
                var extension = await ExtensionVersionAsync(db, transaction, token);
                var relation = extension is null ? null : await FindRelationAsync(db, transaction, connection.Schema, connection.Table ?? string.Empty, token);
                if (relation is null)
                {
                    throw Failure(StoreFailure.NotFound);
                }

                var statement = PgVectorSearchStatement.Build(relation, connection, query);

                // The settings are numbers and keywords of ours, never text from outside. Before 0.8 the index can only be asked for more candidates.
                await ExecuteAsync(db, transaction, $"SET LOCAL hnsw.ef_search = {Math.Clamp(query.Limit, 40, MaxEfSearch)}", token);
                if (extension >= IterativeScanVersion)
                {
                    await ExecuteAsync(db, transaction, "SET LOCAL hnsw.iterative_scan = relaxed_order", token);
                    await ExecuteAsync(db, transaction, "SET LOCAL ivfflat.iterative_scan = relaxed_order", token);
                }

                await using var command = new NpgsqlCommand(statement.Sql, db, transaction);
                foreach (var parameter in statement.Parameters)
                {
                    command.Parameters.Add(parameter);
                }

                await using var reader = await command.ExecuteReaderAsync(token);

                var hits = new List<StoreHit>();
                var scoreOrdinal = statement.Outputs.Count;
                while (await reader.ReadAsync(token))
                {
                    var row = new JsonObject();
                    for (var i = 0; i < statement.Outputs.Count; i++)
                    {
                        if (Value(reader, i, statement.Outputs[i]) is { } value)
                        {
                            Place(row, statement.Outputs[i], value);
                        }
                    }

                    hits.Add(new StoreHit(reader.GetDouble(scoreOrdinal), row));
                }

                return (IReadOnlyList<StoreHit>)hits;
            },
            cancellationToken);
    }

    // A key inside a json column was selected as text: its JSON is the value. A plain column is read by its own type, whole.
    private static JsonNode? Value(NpgsqlDataReader reader, int ordinal, PgVectorOutput output)
    {
        if (output.Key is null)
        {
            return PgVectorRows.Read(reader, ordinal, output.Column.Kind, whole: true);
        }

        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(reader.GetString(ordinal));
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // The path of a key in a row is the nested object the mapping reads it from: { "metadata": { "department": "..." } }.
    private static void Place(JsonObject row, PgVectorOutput output, JsonNode value)
    {
        if (output.Key is not { } key)
        {
            row[output.Column.Name] = value;
            return;
        }

        var current = row;
        if (current[output.Column.Name] is not JsonObject root)
        {
            root = [];
            current[output.Column.Name] = root;
        }

        current = root;
        for (var i = 0; i < key.Length - 1; i++)
        {
            if (current[key[i]] is not JsonObject next)
            {
                next = [];
                current[key[i]] = next;
            }

            current = next;
        }

        current[key[^1]] = value;
    }
}
