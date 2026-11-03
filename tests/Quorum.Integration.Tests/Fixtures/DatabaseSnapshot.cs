using System.Diagnostics.CodeAnalysis;
using System.Text;
using Npgsql;

namespace Quorum.Integration.Tests.Fixtures;

/// <summary>
/// A copy of every table's rows, taken once the app is set up, that a test restores before it runs.
/// It lets the tests of a class share one app instead of starting one each.
/// </summary>
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Every identifier comes from the catalog of the test database, never from input.")]
public sealed class DatabaseSnapshot
{
    private const string SnapshotSchema = "quorum_snapshot";

    private readonly string _connectionString;
    private readonly string _restoreScript;

    private DatabaseSnapshot(string connectionString, string restoreScript)
    {
        _connectionString = connectionString;
        _restoreScript = restoreScript;
    }

    public static async Task<DatabaseSnapshot> TakeAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var tables = new List<(string Schema, string Table)>();
        await using (var list = new NpgsqlCommand(
            "SELECT schemaname, tablename FROM pg_tables WHERE schemaname NOT IN ('pg_catalog', 'information_schema') AND tablename <> '__EFMigrationsHistory' ORDER BY 1, 2",
            connection))
        await using (var reader = await list.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                tables.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var copy = new StringBuilder("CREATE SCHEMA ").Append(SnapshotSchema).Append(';');
        var restore = new StringBuilder();
        var truncate = string.Join(", ", tables.Select(t => $"\"{t.Schema}\".\"{t.Table}\""));
        restore.Append("TRUNCATE ").Append(truncate).Append(';');

        foreach (var (schema, table) in tables)
        {
            var columns = new List<string>();
            await using (var cmd = new NpgsqlCommand(
                "SELECT column_name FROM information_schema.columns WHERE table_schema = @s AND table_name = @t AND is_generated = 'NEVER' ORDER BY ordinal_position",
                connection))
            {
                cmd.Parameters.AddWithValue("s", schema);
                cmd.Parameters.AddWithValue("t", table);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    columns.Add($"\"{reader.GetString(0)}\"");
                }
            }

            var list = string.Join(", ", columns);
            var copyName = $"{SnapshotSchema}.\"{schema}__{table}\"";
            copy.Append(FormattableString.Invariant($"CREATE TABLE {copyName} AS SELECT {list} FROM \"{schema}\".\"{table}\";"));
            restore.Append(FormattableString.Invariant($"INSERT INTO \"{schema}\".\"{table}\" ({list}) OVERRIDING SYSTEM VALUE SELECT {list} FROM {copyName};"));
        }

        await using (var create = new NpgsqlCommand(copy.ToString(), connection))
        {
            await create.ExecuteNonQueryAsync();
        }

        // Foreign keys are not checked while the rows go back, so the tables can be filled in any order.
        return new DatabaseSnapshot(connectionString, $"SET LOCAL session_replication_role = replica;{restore}");
    }

    public async Task RestoreAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(_restoreScript, connection, transaction))
        {
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }
}
