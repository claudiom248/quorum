using System.Globalization;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Quorum.Modules.KnowledgeBases.Storage.PgVector;

internal sealed partial class PgVectorConnector
{
    /// <summary>
    /// The ceiling of the statement timeout of a lookup (spec 008 A2-2): what a background computation may take. What a caller really
    /// gives is its cancellation token, which cancels the statement on the server.
    /// </summary>
    public static readonly TimeSpan LookupTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Why a lookup over <paramref name="paths"/> cannot run on the relation, in words for the administrator, or <see langword="null"/>
    /// when it can (A2-7): the relation, a column or its type is missing. An index is never needed.
    /// </summary>
    /// <exception cref="StoreException">The database cannot be used.</exception>
    public async Task<string?> CheckLookupsAsync(PgVectorConnection connection, IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(paths);

        var relation = await ReadAsync(
            connection,
            StoreLimits.RequestTimeout,
            (db, transaction, token) => FindRelationAsync(db, transaction, connection.Schema, connection.Table ?? string.Empty, token),
            cancellationToken);
        if (relation is null)
        {
            return "The table or view does not exist in that database.";
        }

        List<string> missing = [];
        List<string> unusable = [];
        foreach (var path in paths.Order(StringComparer.Ordinal))
        {
            var (column, key) = PgVectorStatement.FindPath(relation, path);
            if (column is null || (key is not null && column.Kind != PgVectorColumnKind.Json))
            {
                missing.Add(path);
            }
            else if (key is null && column.Kind is PgVectorColumnKind.Vector or PgVectorColumnKind.Other)
            {
                unusable.Add(path);
            }
        }

        if (missing.Count > 0)
        {
            return $"'{string.Join("', '", missing)}' {(missing.Count == 1 ? "is" : "are")} not in the relation: the mapping no longer fits it. Correct the mapping and refresh the lookup.";
        }

        return unusable.Count > 0
            ? $"'{string.Join("', '", unusable)}' {(unusable.Count == 1 ? "has" : "have")} a type that lookups cannot read. Map a text, number, boolean or date column."
            : null;
    }

    /// <exception cref="StoreException">The database cannot be used, or the relation or a path is gone.</exception>
    public async Task<DistinctValues> DistinctValuesAsync(PgVectorConnection connection, DistinctValuesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(query);

        return await ReadAsync(
            connection,
            LookupTimeout,
            async (db, transaction, token) =>
            {
                var relation = await RequireRelationAsync(db, transaction, connection, token);
                var statement = PgVectorDistinctStatement.Build(relation, query);
                await using var command = Command(statement.Sql, statement.Parameters, db, transaction);
                await using var reader = await command.ExecuteReaderAsync(token);

                var values = new List<DistinctValue>();
                while (await reader.ReadAsync(token))
                {
                    values.Add(new DistinctValue(reader.GetString(0), (int)Math.Min(reader.GetInt64(1), int.MaxValue)));
                }

                return new DistinctValues([.. values.Take(query.MaxValues)], values.Count > query.MaxValues);
            },
            cancellationToken);
    }

    /// <exception cref="StoreException">The database cannot be used, or the relation or a path is gone.</exception>
    public async Task<long> CountAsync(PgVectorConnection connection, IReadOnlyList<FilterCondition> conditions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(conditions);

        return await ReadAsync(
            connection,
            LookupTimeout,
            async (db, transaction, token) =>
            {
                var relation = await RequireRelationAsync(db, transaction, connection, token);
                var statement = PgVectorCountStatement.Build(relation, conditions);
                await using var command = Command(statement.Sql, statement.Parameters, db, transaction);
                return Convert.ToInt64(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
            },
            cancellationToken);
    }

    /// <exception cref="StoreException">The database cannot be used, the relation or a path is gone, or the cursor is not this relation's.</exception>
    public async Task<RecordPage> ScanAsync(PgVectorConnection connection, RecordQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(query);

        return await ReadAsync(
            connection,
            LookupTimeout,
            async (db, transaction, token) =>
            {
                var relation = await RequireRelationAsync(db, transaction, connection, token);
                var order = PgVectorOrder.Choose(relation, await OrderKeyAsync(db, transaction, relation, token));
                var statement = PgVectorScanStatement.Build(relation, order, query);
                await using var command = Command(statement.Sql, statement.Parameters, db, transaction);
                await using var reader = await command.ExecuteReaderAsync(token);

                var rows = new List<JsonObject>();
                string[]? lastKey = null;
                var more = false;
                while (await reader.ReadAsync(token))
                {
                    if (rows.Count == query.Limit)
                    {
                        more = true;
                        break;
                    }

                    var row = new JsonObject();
                    for (var i = 0; i < statement.Outputs.Count; i++)
                    {
                        if (Value(reader, i, statement.Outputs[i]) is { } value)
                        {
                            Place(row, statement.Outputs[i], value);
                        }
                    }

                    lastKey = [.. Enumerable.Range(statement.Outputs.Count, statement.KeyColumns).Select(i => reader.IsDBNull(i) ? string.Empty : reader.GetString(i))];
                    rows.Add(row);
                }

                var next = !more
                    ? null
                    : statement.Order.Kind == PgVectorOrderKind.Offset
                        ? new PgVectorCursor(null, statement.StartOffset + rows.Count).Encode()
                        : new PgVectorCursor(lastKey, 0).Encode();
                return new RecordPage(rows, next);
            },
            cancellationToken);
    }

    private static async Task<PgVectorRelation> RequireRelationAsync(NpgsqlConnection db, NpgsqlTransaction transaction, PgVectorConnection connection, CancellationToken cancellationToken) =>
        await FindRelationAsync(db, transaction, connection.Schema, connection.Table ?? string.Empty, cancellationToken)
        ?? throw Failure(StoreFailure.NotFound);

    private static NpgsqlCommand Command(string sql, IReadOnlyList<NpgsqlParameter> parameters, NpgsqlConnection db, NpgsqlTransaction transaction)
    {
        var command = new NpgsqlCommand(sql, db, transaction);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        return command;
    }

    // The columns that tell rows apart, to resume a scan after one of them: the primary key, else the first unique index whose
    // columns are all NOT NULL. Only the key columns of a plain index count, never an expression or an included column.
    private static async Task<IReadOnlyList<PgVectorColumn>?> OrderKeyAsync(NpgsqlConnection db, NpgsqlTransaction transaction, PgVectorRelation relation, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT array_agg(a.attname ORDER BY k.ord)
            FROM pg_index i
            CROSS JOIN LATERAL unnest(i.indkey::int2[]) WITH ORDINALITY AS k(attnum, ord)
            JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum
            WHERE i.indrelid = @oid AND i.indisunique AND i.indisvalid AND i.indpred IS NULL AND i.indexprs IS NULL AND k.ord <= i.indnkeyatts
            GROUP BY i.indexrelid, i.indisprimary
            HAVING bool_and(a.attnotnull)
            ORDER BY i.indisprimary DESC, i.indexrelid
            LIMIT 1
            """,
            db,
            transaction);
        command.Parameters.Add(new NpgsqlParameter("oid", NpgsqlDbType.Oid) { Value = relation.Oid });
        if (await command.ExecuteScalarAsync(cancellationToken) is not string[] names)
        {
            return null;
        }

        var columns = names.Select(name => relation.Columns.FirstOrDefault(column => column.Name == name)).ToList();
        return columns.Any(column => column is null) ? null : [.. columns.Select(column => column!)];
    }
}
