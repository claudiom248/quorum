using System.Globalization;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Quorum.Modules.KnowledgeBases.Storage.PgVector;

/// <summary>
/// How a scan resumes (spec 008 A2-6): after the last key of the page, after the last physical address, or by counting rows when the
/// relation has nothing stable to compare.
/// </summary>
internal enum PgVectorOrderKind
{
    Keyset,
    Ctid,
    Offset,
}

/// <param name="Columns">The columns of the key, for <see cref="PgVectorOrderKind.Keyset"/>.</param>
internal sealed record PgVectorOrder(PgVectorOrderKind Kind, IReadOnlyList<PgVectorColumn> Columns)
{
    /// <summary>The types a keyset can compare with a text parameter cast back to them; any other makes the order fall back.</summary>
    internal static string? CastOf(PgVectorColumn column) => column.TypeName switch
    {
        "int2" or "int4" or "int8" => "bigint",
        "numeric" => "numeric",
        "text" or "varchar" or "bpchar" => "text",
        "uuid" => "uuid",
        "date" => "date",
        "timestamp" => "timestamp",
        "timestamptz" => "timestamptz",
        _ => null,
    };

    public static PgVectorOrder Choose(PgVectorRelation relation, IReadOnlyList<PgVectorColumn>? key)
    {
        ArgumentNullException.ThrowIfNull(relation);

        if (key is { Count: > 0 } && key.All(column => CastOf(column) is not null))
        {
            return new PgVectorOrder(PgVectorOrderKind.Keyset, key);
        }

        // A table's physical address is stable enough for a pass over it; a partitioned table's is not unique, and a view has none.
        return relation.Kind is "r" or "m" ? new PgVectorOrder(PgVectorOrderKind.Ctid, []) : new PgVectorOrder(PgVectorOrderKind.Offset, []);
    }
}

/// <summary>Where the next page of a scan starts: the key of the last row, or how many rows were passed. It travels as opaque text.</summary>
internal sealed record PgVectorCursor(string[]? After, long Offset)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public string Encode() => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this, Options)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static PgVectorCursor? Decode(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        try
        {
            var padded = text.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
            return JsonSerializer.Deserialize<PgVectorCursor>(Convert.FromBase64String(padded), Options);
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw PgVectorConnector.Failure(StoreFailure.Unexpected);
        }
    }
}

/// <summary>The distinct values of a path with the number of rows, or of documents, that carry each (spec 008 A2-3, A2-4): one <c>GROUP BY</c>.</summary>
internal sealed class PgVectorDistinctStatement : PgVectorStatement
{
    private const string Value = "\"__value\"";

    private PgVectorDistinctStatement(PgVectorRelation relation)
        : base(relation)
    {
    }

    public string Sql { get; private set; } = string.Empty;

    /// <exception cref="StoreException">A path is not in the relation or has a type that cannot be listed.</exception>
    public static PgVectorDistinctStatement Build(PgVectorRelation relation, DistinctValuesQuery query)
    {
        var statement = new PgVectorDistinctStatement(relation);
        statement.Compose(query);
        return statement;
    }

    private void Compose(DistinctValuesQuery query)
    {
        var values = ValuesOf(query.Path);
        var filters = new List<string> { $"vals.{Value} <> ''" };
        filters.AddRange(query.Conditions.Select(Condition));
        var count = query.DocumentIdPath is { } document ? $"count(DISTINCT {DocumentOf(document)})" : "count(*)";

        // One more than the ceiling tells whether the list was cut. Ties are ordered by value in the C collation, as the other kinds do.
        Sql = $"""
            SELECT vals.{Value}, {count} AS "__count"
            FROM {PgVectorConnector.QualifiedName(Relation)} CROSS JOIN LATERAL {values} AS vals
            WHERE {string.Join(" AND ", filters)}
            GROUP BY vals.{Value}
            ORDER BY "__count" DESC, vals.{Value} COLLATE "C"
            LIMIT {Add(query.MaxValues + 1, NpgsqlDbType.Integer)}
            """;
    }

    // The values one row contributes, as a subquery with the column "__value": a scalar one, each element of a list, and for a key of a
    // json column its string or each string of its list; any other JSON type contributes nothing.
    private string ValuesOf(string path)
    {
        var (column, key) = Find(path);
        if (column is null)
        {
            throw PgVectorConnector.Failure(StoreFailure.NotFound);
        }

        var quoted = PgVectorRows.Quote(column.Name);
        if (key is not null)
        {
            if (column.Kind != PgVectorColumnKind.Json)
            {
                throw PgVectorConnector.Failure(StoreFailure.NotFound);
            }

            var x = $"({Json(column)} #> {Add(key, NpgsqlDbType.Array | NpgsqlDbType.Text)})";
            return $"""
                (SELECT {x} #>> {Scalar} AS {Value} WHERE jsonb_typeof({x}) = 'string'
                    UNION ALL
                    SELECT element #>> {Scalar} FROM jsonb_array_elements(CASE WHEN jsonb_typeof({x}) = 'array' THEN {x} ELSE '[]'::jsonb END) AS element
                    WHERE jsonb_typeof(element) = 'string')
                """;
        }

        return column.Kind switch
        {
            PgVectorColumnKind.TextArray => $"(SELECT item::text AS {Value} FROM unnest({quoted}) AS item)",
            PgVectorColumnKind.Vector or PgVectorColumnKind.Other or PgVectorColumnKind.Json => throw PgVectorConnector.Failure(StoreFailure.Rejected),
            _ => $"(SELECT {quoted}::text AS {Value} WHERE {quoted} IS NOT NULL)",
        };
    }

    // The document of a row, whatever its type: the column itself so that nothing is cast, or a string or a number inside a json key.
    private string DocumentOf(string path)
    {
        var (column, key) = Find(path);
        if (column is null)
        {
            throw PgVectorConnector.Failure(StoreFailure.NotFound);
        }

        if (key is not null)
        {
            var x = $"({Json(column)} #> {Add(key, NpgsqlDbType.Array | NpgsqlDbType.Text)})";
            return column.Kind == PgVectorColumnKind.Json
                ? $"CASE WHEN jsonb_typeof({x}) IN ('string', 'number') THEN {x} #>> {Scalar} END"
                : throw PgVectorConnector.Failure(StoreFailure.NotFound);
        }

        return column.Kind is PgVectorColumnKind.Vector or PgVectorColumnKind.Other
            ? throw PgVectorConnector.Failure(StoreFailure.Rejected)
            : PgVectorRows.Quote(column.Name);
    }
}

/// <summary>How many rows satisfy every condition (spec 008 A2-5).</summary>
internal sealed class PgVectorCountStatement : PgVectorStatement
{
    private PgVectorCountStatement(PgVectorRelation relation)
        : base(relation)
    {
    }

    public string Sql { get; private set; } = string.Empty;

    public static PgVectorCountStatement Build(PgVectorRelation relation, IReadOnlyList<FilterCondition> conditions)
    {
        var statement = new PgVectorCountStatement(relation);
        var filters = conditions.Select(statement.Condition).ToList();
        statement.Sql = $"SELECT count(*) FROM {PgVectorConnector.QualifiedName(relation)}{(filters.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", filters))}";
        return statement;
    }
}

/// <summary>A page of rows in a stable order, resumed by a cursor (spec 008 A2-6).</summary>
internal sealed class PgVectorScanStatement : PgVectorStatement
{
    private PgVectorScanStatement(PgVectorRelation relation)
        : base(relation)
    {
    }

    public string Sql { get; private set; } = string.Empty;

    public IReadOnlyList<PgVectorOutput> Outputs { get; private set; } = [];

    /// <summary>How many columns after the paths carry the key of the row, for the cursor.</summary>
    public int KeyColumns { get; private set; }

    public PgVectorOrder Order { get; private set; } = new(PgVectorOrderKind.Offset, []);

    public long StartOffset { get; private set; }

    /// <exception cref="StoreException">A path is not in the relation, or the cursor is not one this relation gave.</exception>
    public static PgVectorScanStatement Build(PgVectorRelation relation, PgVectorOrder order, RecordQuery query)
    {
        var statement = new PgVectorScanStatement(relation) { Order = order };
        statement.Compose(query);
        return statement;
    }

    private void Compose(RecordQuery query)
    {
        Outputs = [.. query.Paths.Select(Resolve).Where(output => output.Column.Kind is not (PgVectorColumnKind.Vector or PgVectorColumnKind.Other))];
        var cursor = PgVectorCursor.Decode(query.Cursor);
        var filters = query.Conditions.Select(Condition).ToList();

        var select = new StringBuilder();
        for (var i = 0; i < Outputs.Count; i++)
        {
            select.Append(Select(Outputs[i])).Append(" AS c").Append(i.ToString(CultureInfo.InvariantCulture)).Append(", ");
        }

        string orderBy;
        switch (Order.Kind)
        {
            case PgVectorOrderKind.Keyset:
                var keys = Order.Columns.Select(column => PgVectorRows.Quote(column.Name)).ToList();
                for (var i = 0; i < keys.Count; i++)
                {
                    select.Append(keys[i]).Append("::text AS k").Append(i.ToString(CultureInfo.InvariantCulture)).Append(", ");
                }

                KeyColumns = keys.Count;
                if (cursor?.After is { } after)
                {
                    if (after.Length != keys.Count)
                    {
                        throw PgVectorConnector.Failure(StoreFailure.Unexpected);
                    }

                    var values = after.Select((text, i) => $"CAST({Add(text, NpgsqlDbType.Text)} AS {PgVectorOrder.CastOf(Order.Columns[i])})");
                    filters.Add($"({string.Join(", ", keys)}) > ({string.Join(", ", values)})");
                }

                orderBy = string.Join(", ", keys);
                break;
            case PgVectorOrderKind.Ctid:
                select.Append("ctid::text AS k0, ");
                KeyColumns = 1;
                if (cursor?.After is [{ } address])
                {
                    filters.Add($"ctid > CAST({Add(address, NpgsqlDbType.Text)} AS tid)");
                }
                else if (cursor?.After is not null)
                {
                    throw PgVectorConnector.Failure(StoreFailure.Unexpected);
                }

                orderBy = "ctid";
                break;
            default:
                // Nothing in the relation is stable to compare: the rows are passed by count, in the order of the paths asked for.
                StartOffset = Math.Max(0, cursor?.Offset ?? 0);
                orderBy = Outputs.Count == 0 ? "1" : string.Join(", ", Enumerable.Range(0, Outputs.Count).Select(i => "c" + i.ToString(CultureInfo.InvariantCulture)));
                break;
        }

        if (select.Length == 0)
        {
            select.Append('1');
        }

        // One row more than the page tells whether there is a next one.
        Sql = $"""
            SELECT {select.ToString().TrimEnd(',', ' ')}
            FROM {PgVectorConnector.QualifiedName(Relation)}{(filters.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", filters))}
            ORDER BY {orderBy}
            LIMIT {Add(query.Limit + 1, NpgsqlDbType.Integer)}{(Order.Kind == PgVectorOrderKind.Offset ? $" OFFSET {Add(StartOffset, NpgsqlDbType.Bigint)}" : string.Empty)}
            """;
    }
}
