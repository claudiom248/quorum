using System.Globalization;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace Quorum.Modules.KnowledgeBases.Storage.PgVector;

/// <summary>What a search selects for one path of the mapping: the column, and the key inside it when the path goes into a <c>json</c> column.</summary>
internal sealed record PgVectorOutput(string Path, PgVectorColumn Column, string[]? Key);

/// <summary>
/// The one query of a search (spec 008 D-11). Identifiers are only names read from the catalog, quoted; every value, the vector
/// included, is a parameter. A condition over a <c>json</c> key looks at the JSON type before it casts, inside a <c>CASE</c> (the
/// order in which an <c>AND</c> is evaluated is not guaranteed), so a row whose key has another type does not match and never fails the query.
/// </summary>
internal sealed class PgVectorSearchStatement : PgVectorStatement
{
    private const string DistanceColumn = "__distance";

    private PgVectorSearchStatement(PgVectorRelation relation)
        : base(relation)
    {
    }

    public string Sql { get; private set; } = string.Empty;

    public IReadOnlyList<PgVectorOutput> Outputs { get; private set; } = [];

    /// <summary>The name of the column that carries the score, after the paths.</summary>
    public static string ScoreAlias => DistanceColumn;

    /// <exception cref="StoreException">A path or the vector column is not in the relation.</exception>
    public static PgVectorSearchStatement Build(PgVectorRelation relation, PgVectorConnection connection, StoreQuery query)
    {
        var statement = new PgVectorSearchStatement(relation);
        statement.Compose(connection, query);
        return statement;
    }

    private void Compose(PgVectorConnection connection, StoreQuery query)
    {
        var vectors = Relation.Columns.Where(column => column.IsVector).ToList();
        var vector = connection.VectorColumn is { } named
            ? vectors.FirstOrDefault(column => column.Name == named)
            : vectors.Count == 1 ? vectors[0] : null;
        if (vector is null)
        {
            throw PgVectorConnector.Failure(StoreFailure.NotFound);
        }

        var type = vector.TypeName;
        var quotedVector = PgVectorRows.Quote(vector.Name);
        var distance = connection.Distance switch
        {
            PgVectorDistance.L2 => "<->",
            PgVectorDistance.InnerProduct => "<#>",
            _ => "<=>",
        };
        var queryVector = Add(query.Vector, NpgsqlDbType.Array | NpgsqlDbType.Real);
        var measured = $"({quotedVector} {distance} CAST({queryVector} AS {type}))";

        Outputs = [.. query.Paths.Select(Resolve).Where(output => output.Column.Kind is not (PgVectorColumnKind.Vector or PgVectorColumnKind.Other))];

        var filters = new List<string> { $"{quotedVector} IS NOT NULL" };
        filters.AddRange(query.Conditions.Select(Condition));
        if (query.ScoreThreshold is { } threshold)
        {
            // The score is the cosine similarity, the inner product or the L2 distance: the operator returns 1 - similarity, the negative
            // inner product and the distance, so the threshold keeps the rows whose operator value is at most the bound.
            var bound = connection.Distance switch
            {
                PgVectorDistance.L2 => threshold,
                PgVectorDistance.InnerProduct => -threshold,
                _ => 1 - threshold,
            };
            filters.Add($"{measured} <= {Add(bound, NpgsqlDbType.Double)}");
        }

        var inner = new StringBuilder();
        for (var i = 0; i < Outputs.Count; i++)
        {
            inner.Append(Select(Outputs[i])).Append(" AS c").Append(i.ToString(CultureInfo.InvariantCulture)).Append(", ");
        }

        var outer = string.Concat(Enumerable.Range(0, Outputs.Count).Select(i => $"c{i.ToString(CultureInfo.InvariantCulture)}, "));
        var scoreExpression = connection.Distance switch
        {
            PgVectorDistance.Cosine => $"1 - \"{DistanceColumn}\"",
            PgVectorDistance.InnerProduct => $"-\"{DistanceColumn}\"",
            _ => $"\"{DistanceColumn}\"",
        };

        // The nearest rows come out of an approximate index in an order that is only roughly right when the scan is iterative
        // (relaxed_order), so they are sorted again; MATERIALIZED keeps the planner from folding that sort into the index scan.
        Sql = $"""
            WITH nearest AS MATERIALIZED (
                SELECT {inner}{measured} AS "{DistanceColumn}"
                FROM {PgVectorConnector.QualifiedName(Relation)}
                WHERE {string.Join(" AND ", filters)}
                ORDER BY {measured}
                LIMIT {Add(query.Limit, NpgsqlDbType.Integer)})
            SELECT {outer}{scoreExpression}
            FROM nearest
            ORDER BY "{DistanceColumn}"
            """;
    }

}
