using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace Quorum.SampleKnowledgeBase;

/// <summary>
/// Writes sample relations into a PostgreSQL database that has (or can have) the pgvector extension. Like <see cref="QdrantSeeder"/>
/// this is what an ingestion pipeline does and what Quorum never does, so the product has no such code. The data source must come from a
/// builder that called <c>UseVector()</c>, as <see cref="CreateDataSource"/> does.
/// </summary>
public sealed class PgVectorSeeder(NpgsqlDataSource dataSource, ModelEmbedder? model = null)
{
    /// <summary>A data source for <paramref name="connectionString"/> that can send vectors.</summary>
    public static NpgsqlDataSource CreateDataSource(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseVector();
        return builder.Build();
    }

    /// <summary>Creates the schema and the extension if they are missing, then drops and recreates each relation with its rows.</summary>
    public async Task SeedAsync(string schema, IEnumerable<SampleRelation> relations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(relations);

        await ExecuteAsync("CREATE EXTENSION IF NOT EXISTS vector", cancellationToken);
        await ExecuteAsync($"CREATE SCHEMA IF NOT EXISTS {SampleRelations.Quote(schema)}", cancellationToken);
        await ReloadTypesAsync(cancellationToken);

        foreach (var relation in relations)
        {
            await SeedAsync(schema, relation, cancellationToken);
        }
    }

    public async Task SeedAsync(string schema, SampleRelation relation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(relation);

        foreach (var statement in relation.Statements)
        {
            await ExecuteAsync(statement, cancellationToken);
        }

        foreach (var row in relation.Rows)
        {
            await InsertAsync(schema, row, cancellationToken);
        }

        foreach (var statement in relation.AfterRows)
        {
            await ExecuteAsync(statement, cancellationToken);
        }

        if (relation.Rows.Count > 0)
        {
            // The planner and the catalog's row estimate read these statistics, as they would after any real ingestion.
            await ExecuteAsync($"ANALYZE {SampleRelations.Quote(schema)}.{SampleRelations.Quote(relation.Name)}", cancellationToken);
        }
    }

    /// <summary>Adds one row to an existing relation (a test that needs a row of its own).</summary>
    public async Task InsertAsync(string schema, SampleRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        var columns = row.Values.Keys.ToList();
        await using var command = dataSource.CreateCommand(
            $"INSERT INTO {SampleRelations.Quote(schema)}.{SampleRelations.Quote(row.Table)} ({string.Join(", ", columns.Select(SampleRelations.Quote))}) "
            + $"VALUES ({string.Join(", ", columns.Select((_, i) => $"@p{i}"))})");
        for (var i = 0; i < columns.Count; i++)
        {
            command.Parameters.Add(await ParameterAsync($"p{i}", row.Values[columns[i]], cancellationToken));
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<NpgsqlParameter> ParameterAsync(string name, object? value, CancellationToken cancellationToken)
    {
        switch (value)
        {
            case SampleVectorValue vector:
                var values = vector.FromModel
                    ? await (model ?? throw new InvalidOperationException("A vector made by a model needs an embedder.")).EmbedAsync(vector.Text, cancellationToken)
                    : DeterministicEmbedding.Embed(vector.Text, vector.Size);
                if (values.Length != vector.Size)
                {
                    throw new InvalidOperationException($"The model answered {values.Length} dimensions, the column has {vector.Size}.");
                }

                return new NpgsqlParameter(name, vector.Half ? new HalfVector(values.Select(x => (Half)x).ToArray()) : new Vector(values));
            case JsonNode node:
                return new NpgsqlParameter(name, NpgsqlDbType.Jsonb) { Value = node.ToJsonString() };
            case null:
                return new NpgsqlParameter(name, DBNull.Value);
            default:
                return new NpgsqlParameter(name, value);
        }
    }

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The vector types exist only after the extension is created, and a connection loads the types once, when it opens.</summary>
    private async Task ReloadTypesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ReloadTypesAsync(cancellationToken);
    }
}
