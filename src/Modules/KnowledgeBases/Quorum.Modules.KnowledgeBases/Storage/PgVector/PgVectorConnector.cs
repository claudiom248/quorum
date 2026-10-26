using System.Globalization;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Quorum.Modules.KnowledgeBases.Storage.PgVector;

/// <param name="Column">The leading column of the index: the only one a filter on its own can use.</param>
internal sealed record PgVectorIndex(string Column, string Method, string OperatorClass);

/// <param name="Kind">The catalog's letter: <c>r</c> table, <c>p</c> partitioned table, <c>v</c> view, <c>m</c> materialized view.</param>
/// <param name="ApproximateRows">What the catalog estimates; negative when the relation was never analysed or has no rows of its own (a view).</param>
internal sealed record PgVectorRelation(string Schema, string Name, string Kind, long ApproximateRows, IReadOnlyList<PgVectorColumn> Columns, IReadOnlyList<PgVectorIndex> Indexes)
{
    /// <summary>A plain view has no index of its own, so what it is missing cannot be told from it.</summary>
    public bool IsView => Kind == "v";

    public string KindName => Kind switch
    {
        "r" => "table",
        "p" => "partitioned table",
        "v" => "view",
        "m" => "materialized view",
        _ => "relation",
    };
}

/// <param name="Extension">The version of the <c>vector</c> extension, or <see langword="null"/> when it is not installed.</param>
/// <param name="Relation">The relation, or <see langword="null"/> when the database has none by that name that is a table, a view, ...</param>
/// <param name="MeasuredDimensions">For the vector columns that declare none: the dimensions of the first row that has a value.</param>
/// <param name="Rows">The first rows, without the vector columns and with long texts cut; empty unless sampling was asked for.</param>
internal sealed record PgVectorProbe(Version? Extension, PgVectorRelation? Relation, IReadOnlyDictionary<string, int> MeasuredDimensions, IReadOnlyList<JsonObject> Rows);

internal sealed record PgVectorRelationListing(Version? Extension, IReadOnlyList<ListedSource> Sources, bool Truncated);

/// <summary>
/// Quorum's access to a customer's PostgreSQL (spec 008 D-2): every call is a catalog read or a <c>SELECT</c> inside a
/// <c>READ ONLY</c> transaction with a statement timeout. What is ours is the edge: that transaction, rows as JSON, and failures
/// mapped to a few outcomes with messages that are safe to show. Identifiers are only ever names read from the catalog, quoted;
/// values are parameters.
/// </summary>
internal sealed class PgVectorConnector(PgVectorDataSources dataSources)
{
    /// <summary>How many rows discovery reads: a relation has many columns and a <c>jsonb</c> column may carry keys that appear in few rows (D-10).</summary>
    public const int SampleSize = 100;

    public async Task<PgVectorProbe> ProbeAsync(PgVectorConnection connection, bool sample, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return await ReadAsync(
            connection,
            StoreLimits.RequestTimeout,
            async (db, transaction, token) =>
            {
                var extension = await ExtensionVersionAsync(db, transaction, token);
                var relation = await FindRelationAsync(db, transaction, connection.Schema, connection.Table ?? string.Empty, token);
                if (relation is null)
                {
                    return new PgVectorProbe(extension, null, new Dictionary<string, int>(), []);
                }

                var measured = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var column in relation.Columns.Where(column => column.IsVector && column.Dimensions is null))
                {
                    if (await MeasureAsync(db, transaction, relation, column, token) is { } dimensions)
                    {
                        measured[column.Name] = dimensions;
                    }
                }

                var rows = sample ? await SampleAsync(db, transaction, relation, token) : [];
                return new PgVectorProbe(extension, relation, measured, rows);
            },
            cancellationToken);
    }

    public async Task<PgVectorRelationListing> ListAsync(PgVectorConnection connection, string? filter, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return await ReadAsync(
            connection,
            StoreLimits.RequestTimeout,
            async (db, transaction, token) =>
            {
                var extension = await ExtensionVersionAsync(db, transaction, token);
                if (extension is null)
                {
                    // Without the extension no column has a vector type, so there is nothing to list.
                    return new PgVectorRelationListing(null, [], false);
                }

                await using var command = new NpgsqlCommand(
                    $"""
                    WITH rel AS (
                        SELECT c.oid, n.nspname, c.relname, c.relkind, c.reltuples::bigint AS reltuples
                        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                        WHERE c.relkind IN ('r', 'v', 'm', 'p') AND NOT c.relispartition
                          AND n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg\_toast%'
                          AND has_table_privilege(c.oid, 'SELECT')
                          AND EXISTS (
                              SELECT 1 FROM pg_attribute a JOIN pg_type t ON t.oid = a.atttypid
                              WHERE a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped AND t.typname IN ('vector', 'halfvec'))
                          AND (@filter::text IS NULL OR (n.nspname || '.' || c.relname) ILIKE @filter ESCAPE '\')
                        ORDER BY n.nspname, c.relname
                        LIMIT @limit)
                    SELECT rel.nspname, rel.relname, rel.relkind::text, rel.reltuples, a.attname, t.typname, format_type(a.atttypid, a.atttypmod), a.atttypmod
                    FROM rel
                    JOIN pg_attribute a ON a.attrelid = rel.oid AND a.attnum > 0 AND NOT a.attisdropped
                    JOIN pg_type t ON t.oid = a.atttypid AND t.typname IN ('vector', 'halfvec')
                    ORDER BY rel.nspname, rel.relname, a.attnum
                    """,
                    db,
                    transaction);
                command.Parameters.AddWithValue("filter", string.IsNullOrWhiteSpace(filter) ? DBNull.Value : "%" + EscapeLike(filter.Trim()) + "%");
                command.Parameters.AddWithValue("limit", limit + 1);

                var sources = new List<ListedSource>();
                string? currentKey = null;
                List<ListedVector> vectors = [];
                (string Schema, string Name, string Kind, long Rows)? current = null;
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    var schema = reader.GetString(0);
                    var name = reader.GetString(1);
                    var key = schema + "\0" + name;
                    if (key != currentKey)
                    {
                        Flush();
                        currentKey = key;
                        current = (schema, name, reader.GetString(2), reader.GetInt64(3));
                        vectors = [];
                    }

                    var modifier = reader.GetInt32(7);
                    vectors.Add(new ListedVector(reader.GetString(4), reader.GetString(6), modifier > 0 ? modifier : null));
                }

                Flush();
                var truncated = sources.Count > limit;
                return new PgVectorRelationListing(extension, truncated ? sources[..limit] : sources, truncated);

                void Flush()
                {
                    if (current is { } relation)
                    {
                        var type = new PgVectorRelation(relation.Schema, relation.Name, relation.Kind, relation.Rows, [], []).KindName;
                        sources.Add(new ListedSource(relation.Schema, relation.Name, type, relation.Kind is "r" or "p" or "m" && relation.Rows >= 0 ? relation.Rows : null, vectors));
                    }
                }
            },
            cancellationToken);
    }

    /// <summary>Runs <paramref name="work"/> in a read-only transaction with a statement timeout, and turns every failure of the database into a <see cref="StoreException"/>.</summary>
    private async Task<T> ReadAsync<T>(
        PgVectorConnection connection,
        TimeSpan limit,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        try
        {
            await using var db = await dataSources.For(connection).OpenConnectionAsync(timeout.Token);
            await using var transaction = await db.BeginTransactionAsync(timeout.Token);
            await ExecuteAsync(db, transaction, "SET TRANSACTION READ ONLY", timeout.Token);
            await using (var statementTimeout = new NpgsqlCommand("SELECT set_config('statement_timeout', @milliseconds, true)", db, transaction))
            {
                statementTimeout.Parameters.AddWithValue("milliseconds", ((int)limit.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));
                await statementTimeout.ExecuteNonQueryAsync(timeout.Token);
            }

            var result = await work(db, transaction, timeout.Token);

            // Nothing was written, so there is nothing to commit; the transaction ends with the connection.
            await transaction.RollbackAsync(timeout.Token);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure(StoreFailure.Unreachable);
        }
        catch (NpgsqlException exception)
        {
            throw Translate(exception);
        }
    }

    private static async Task<Version?> ExtensionVersionAsync(NpgsqlConnection db, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT extversion FROM pg_extension WHERE extname = 'vector'", db, transaction);
        return await command.ExecuteScalarAsync(cancellationToken) is string text && Version.TryParse(text, out var version) ? version : null;
    }

    private static async Task<PgVectorRelation?> FindRelationAsync(NpgsqlConnection db, NpgsqlTransaction transaction, string schema, string name, CancellationToken cancellationToken)
    {
        uint oid;
        string kind;
        long rows;
        bool canRead;
        await using (var command = new NpgsqlCommand(
            """
            SELECT c.oid, c.relkind::text, c.reltuples::bigint, has_table_privilege(c.oid, 'SELECT')
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relname = @name AND c.relkind IN ('r', 'v', 'm', 'p')
            """,
            db,
            transaction))
        {
            command.Parameters.AddWithValue("schema", schema);
            command.Parameters.AddWithValue("name", name);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            oid = reader.GetFieldValue<uint>(0);
            kind = reader.GetString(1);
            rows = reader.GetInt64(2);
            canRead = reader.GetBoolean(3);
        }

        if (!canRead)
        {
            throw Failure(StoreFailure.Unauthorized);
        }

        var columns = new List<PgVectorColumn>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT a.attname, COALESCE(bt.typname, t.typname), COALESCE(bt.typtype, t.typtype)::text, format_type(a.atttypid, a.atttypmod), a.atttypmod
            FROM pg_attribute a
            JOIN pg_type t ON t.oid = a.atttypid
            LEFT JOIN pg_type bt ON bt.oid = t.typbasetype AND t.typtype = 'd'
            WHERE a.attrelid = @oid AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY a.attnum
            """,
            db,
            transaction))
        {
            command.Parameters.Add(new NpgsqlParameter("oid", NpgsqlDbType.Oid) { Value = oid });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(new PgVectorColumn(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4)));
            }
        }

        var indexes = new List<PgVectorIndex>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT a.attname, am.amname, oc.opcname
            FROM pg_index i
            JOIN pg_class ic ON ic.oid = i.indexrelid
            JOIN pg_am am ON am.oid = ic.relam
            JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = i.indkey[0]
            JOIN pg_opclass oc ON oc.oid = i.indclass[0]
            WHERE i.indrelid = @oid AND i.indisvalid AND i.indpred IS NULL AND i.indkey[0] <> 0
            """,
            db,
            transaction))
        {
            command.Parameters.Add(new NpgsqlParameter("oid", NpgsqlDbType.Oid) { Value = oid });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                indexes.Add(new PgVectorIndex(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        return new PgVectorRelation(schema, name, kind, rows, columns, indexes);
    }

    // vector_dims takes a vector or a halfvec; the first row that has a value says how many dimensions the column holds.
    private static async Task<int?> MeasureAsync(NpgsqlConnection db, NpgsqlTransaction transaction, PgVectorRelation relation, PgVectorColumn column, CancellationToken cancellationToken)
    {
        var quoted = PgVectorRows.Quote(column.Name);
        await using var command = new NpgsqlCommand(
            $"SELECT vector_dims({quoted}) FROM {Table(relation)} WHERE {quoted} IS NOT NULL LIMIT 1",
            db,
            transaction);
        return await command.ExecuteScalarAsync(cancellationToken) is int dimensions ? dimensions : null;
    }

    private static async Task<IReadOnlyList<JsonObject>> SampleAsync(NpgsqlConnection db, NpgsqlTransaction transaction, PgVectorRelation relation, CancellationToken cancellationToken)
    {
        var columns = relation.Columns.Where(column => column.IsSampled).ToList();
        if (columns.Count == 0)
        {
            return [];
        }

        // Physical order, without the vector columns: the first rows are representative enough and cost nothing to find (D-10).
        await using var command = new NpgsqlCommand(
            $"SELECT {string.Join(", ", columns.Select(PgVectorRows.SampleExpression))} FROM {Table(relation)} LIMIT {SampleSize.ToString(CultureInfo.InvariantCulture)}",
            db,
            transaction);
        var rows = new List<JsonObject>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new JsonObject();
            for (var i = 0; i < columns.Count; i++)
            {
                if (PgVectorRows.Read(reader, i, columns[i].Kind) is { } value)
                {
                    row[columns[i].Name] = value;
                }
            }

            rows.Add(row);
        }

        return rows;
    }

    private static string Table(PgVectorRelation relation) => $"{PgVectorRows.Quote(relation.Schema)}.{PgVectorRows.Quote(relation.Name)}";

    private static async Task ExecuteAsync(NpgsqlConnection db, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, db, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>The outcome of a failure of the database, by what the server answered (its SQLSTATE) and never by what it said.</summary>
    internal static StoreException Translate(NpgsqlException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is PostgresException postgres)
        {
            return Failure(postgres.SqlState switch
            {
                // invalid_authorization_specification, invalid_password, insufficient_privilege
                "28000" or "28P01" or "42501" => StoreFailure.Unauthorized,

                // invalid_catalog_name, invalid_schema_name, undefined_table, undefined_column
                "3D000" or "3F000" or "42P01" or "42703" => StoreFailure.NotFound,

                // data_exception: pgvector says a vector has other dimensions than the column this way
                { Length: 5 } state when state.StartsWith("22", StringComparison.Ordinal) => StoreFailure.Rejected,

                // connection_exception, cannot_connect_now, too_many_connections, query_canceled (the statement timeout), admin_shutdown
                { Length: 5 } state when state.StartsWith("08", StringComparison.Ordinal) => StoreFailure.Unreachable,
                "57014" or "57P01" or "57P03" or "53300" => StoreFailure.Unreachable,
                _ => StoreFailure.Unexpected,
            });
        }

        return Failure(exception.InnerException is SocketException or IOException or TimeoutException || exception.IsTransient
            ? StoreFailure.Unreachable
            : StoreFailure.Unexpected);
    }

    /// <summary>A failure with the words every kind shows for it: none of them carries the address, a name, the SQL or what the server said.</summary>
    internal static StoreException Failure(StoreFailure failure) => new(failure, failure switch
    {
        StoreFailure.Unreachable => "The PostgreSQL server could not be reached, or did not answer in time.",
        StoreFailure.Unauthorized => "The PostgreSQL server refused the credentials, or the user is not allowed to read the relation.",
        StoreFailure.NotFound => "The database, the schema, the relation or one of its columns does not exist.",
        StoreFailure.Rejected => "The PostgreSQL server rejected the query: the vector probably has another number of dimensions than the column.",
        _ => "The PostgreSQL server answered in a way Quorum does not expect.",
    });
}
