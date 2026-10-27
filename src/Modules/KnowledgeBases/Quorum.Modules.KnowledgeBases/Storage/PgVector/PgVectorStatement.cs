using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace Quorum.Modules.KnowledgeBases.Storage.PgVector;

/// <summary>
/// What every statement over a relation shares (spec 008 D-11, A1-2, A2-2): the parameters, finding a path in the relation, and the
/// translation of a <see cref="FilterCondition"/> into SQL. The search and the lookups both build their <c>WHERE</c> from it, so a
/// filter, a counted point or a <c>document_ids</c> list means the same in each. Identifiers are only names read from the catalog,
/// quoted; every value is a parameter. A condition over a <c>json</c> key looks at the JSON type before it casts, inside a
/// <c>CASE</c> (the order in which an <c>AND</c> is evaluated is not guaranteed), so a row whose key has another type does not match
/// and never fails the statement.
/// </summary>
internal abstract class PgVectorStatement(PgVectorRelation relation)
{
    // The path of the value of a json scalar, for #>>.
    protected const string Scalar = "'{}'";

    // A date in a json key is an ISO 8601 text; only one that starts like a real calendar date is cast.
    private const string IsoDate = "'^\\d{4}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])'";

    private readonly List<NpgsqlParameter> _parameters = [];

    protected PgVectorRelation Relation => relation;

    public IReadOnlyList<NpgsqlParameter> Parameters => _parameters;

    // A path is a column, or a column and the keys inside it; a column with a '.' in its name cannot be addressed, so the first '.' splits.
    protected (PgVectorColumn? Column, string[]? Key) Find(string path) => FindPath(relation, path);

    internal static (PgVectorColumn? Column, string[]? Key) FindPath(PgVectorRelation relation, string path)
    {
        var dot = path.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
        {
            return (relation.Columns.FirstOrDefault(c => c.Name == path), null);
        }

        return (relation.Columns.FirstOrDefault(c => c.Name == path[..dot]), path[(dot + 1)..].Split('.'));
    }

    protected PgVectorOutput Resolve(string path)
    {
        var (column, key) = Find(path);
        return new PgVectorOutput(path, column ?? throw PgVectorConnector.Failure(StoreFailure.NotFound), key);
    }

    // A path the relation no longer has: null, for a caller that can do without it.
    protected PgVectorOutput? TryResolve(string path) =>
        Find(path) is ({ } column, var key) ? new PgVectorOutput(path, column, key) : null;

    // The expression that reads one path of a row: the column, or the key inside a json column as text.
    protected string Select(PgVectorOutput output) => output.Key is { } key
        ? $"({Json(output.Column)} #> {Add(key, NpgsqlDbType.Array | NpgsqlDbType.Text)})::text"
        : PgVectorRows.Quote(output.Column.Name);

    protected static string Json(PgVectorColumn column) => $"{PgVectorRows.Quote(column.Name)}::jsonb";

    protected string Condition(FilterCondition condition)
    {
        var (column, key) = Find(condition.Path);
        if (column is null)
        {
            throw PgVectorConnector.Failure(StoreFailure.NotFound);
        }

        if (key is not null)
        {
            // A key can only be inside a json column: in any other there is nothing to find.
            return column.Kind == PgVectorColumnKind.Json ? OverKey($"({Json(column)} #> {Add(key, NpgsqlDbType.Array | NpgsqlDbType.Text)})", condition) : "FALSE";
        }

        return OverColumn(column, PgVectorRows.Quote(column.Name), condition);
    }

    private string OverColumn(PgVectorColumn column, string quoted, FilterCondition condition) => condition switch
    {
        MatchAnyCondition any => Any(column, quoted, any.Values),
        DocumentIdsCondition ids => Any(column, quoted, ids.Ids),
        MatchBooleanCondition flag => column.Kind == PgVectorColumnKind.Boolean ? $"{quoted} = {Add(flag.Value, NpgsqlDbType.Boolean)}" : "FALSE",
        NumberRangeCondition range => Numbers(column, quoted, range),
        DateRangeCondition range => Dates(column, quoted, range),
        _ => throw new NotSupportedException($"A {condition.GetType().Name} is not a condition of a PgVector search."),
    };

    // The values are text as the caller has them: they are converted to the type of the column and never the column to text, so an index can be used.
    // A value that cannot be converted matches nothing.
    private string Any(PgVectorColumn column, string quoted, IReadOnlyList<string> values)
    {
        switch (column.Kind)
        {
            case PgVectorColumnKind.Text:
                return $"{quoted} = ANY({Add(values.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text)})";
            case PgVectorColumnKind.Enum:
                return $"{quoted}::text = ANY({Add(values.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text)})";
            case PgVectorColumnKind.TextArray:
                var cast = column.TypeName == "_text" ? quoted : $"{quoted}::text[]";
                return $"{cast} && {Add(values.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text)}";
            case PgVectorColumnKind.Uuid:
                return Convertible(values, (string text, out Guid value) => Guid.TryParse(text, out value), NpgsqlDbType.Uuid, quoted);
            case PgVectorColumnKind.Integer:
                return Convertible(values, (string text, out long value) => long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value), NpgsqlDbType.Bigint, quoted);
            case PgVectorColumnKind.Decimal:
                return Convertible(values, (string text, out decimal value) => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value), NpgsqlDbType.Numeric, quoted);
            case PgVectorColumnKind.Float:
                return Convertible(values, (string text, out double value) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value), NpgsqlDbType.Double, quoted);
            default:
                return "FALSE";
        }
    }

    private delegate bool TryConvert<T>(string text, out T value);

    private string Convertible<T>(IReadOnlyList<string> values, TryConvert<T> convert, NpgsqlDbType type, string quoted)
    {
        var converted = new List<T>();
        foreach (var text in values)
        {
            if (convert(text, out var value))
            {
                converted.Add(value);
            }
        }

        return converted.Count == 0 ? "FALSE" : $"{quoted} = ANY({Add(converted.ToArray(), NpgsqlDbType.Array | type)})";
    }

    private string Numbers(PgVectorColumn column, string quoted, NumberRangeCondition range)
    {
        List<string> bounds = [];
        switch (column.Kind)
        {
            case PgVectorColumnKind.Integer:
                // Whole numbers: the bounds are rounded inwards, so a range of 1.5 to 2.5 holds exactly 2.
                if (range.Min is { } lowInteger)
                {
                    bounds.Add($"{quoted} >= {Add(ToLong(Math.Ceiling(lowInteger)), NpgsqlDbType.Bigint)}");
                }

                if (range.Max is { } highInteger)
                {
                    bounds.Add($"{quoted} <= {Add(ToLong(Math.Floor(highInteger)), NpgsqlDbType.Bigint)}");
                }

                break;
            case PgVectorColumnKind.Decimal:
                if (range.Min is { } lowDecimal)
                {
                    bounds.Add($"{quoted} >= {Add(ToDecimal(lowDecimal), NpgsqlDbType.Numeric)}");
                }

                if (range.Max is { } highDecimal)
                {
                    bounds.Add($"{quoted} <= {Add(ToDecimal(highDecimal), NpgsqlDbType.Numeric)}");
                }

                break;
            case PgVectorColumnKind.Float:
                if (range.Min is { } lowFloat)
                {
                    bounds.Add($"{quoted} >= {Add(lowFloat, NpgsqlDbType.Double)}");
                }

                if (range.Max is { } highFloat)
                {
                    bounds.Add($"{quoted} <= {Add(highFloat, NpgsqlDbType.Double)}");
                }

                break;
            default:
                return "FALSE";
        }

        return bounds.Count == 0 ? "TRUE" : string.Join(" AND ", bounds);
    }

    private string Dates(PgVectorColumn column, string quoted, DateRangeCondition range)
    {
        List<string> bounds = [];
        switch (column.Kind)
        {
            case PgVectorColumnKind.Date:
                AddBounds(bounds, quoted, range.From is { } from ? DateOnly.FromDateTime(from) : null, range.To is { } to ? DateOnly.FromDateTime(to) : null, NpgsqlDbType.Date);
                break;
            case PgVectorColumnKind.TimestampTz:
                AddBounds(bounds, quoted, range.From is { } start ? DateTime.SpecifyKind(start, DateTimeKind.Utc) : null, range.To is { } end ? DateTime.SpecifyKind(end, DateTimeKind.Utc) : null, NpgsqlDbType.TimestampTz);
                break;
            case PgVectorColumnKind.Timestamp:
                // Without a time zone the column is read as UTC (D-9).
                AddBounds(bounds, quoted, range.From is { } first ? DateTime.SpecifyKind(first, DateTimeKind.Unspecified) : null, range.To is { } last ? DateTime.SpecifyKind(last, DateTimeKind.Unspecified) : null, NpgsqlDbType.Timestamp);
                break;
            default:
                return "FALSE";
        }

        return bounds.Count == 0 ? "TRUE" : string.Join(" AND ", bounds);
    }

    private void AddBounds(List<string> bounds, string expression, object? from, object? to, NpgsqlDbType type)
    {
        if (from is not null)
        {
            bounds.Add($"{expression} >= {Add(from, type)}");
        }

        if (to is not null)
        {
            bounds.Add($"{expression} <= {Add(to, type)}");
        }
    }

    // x is a jsonb value picked out of the column. Each branch checks the JSON type of x before it casts.
    private string OverKey(string x, FilterCondition condition)
    {
        switch (condition)
        {
            case MatchAnyCondition any:
                var values = Add(any.Values.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text);
                return $"""
                    CASE jsonb_typeof({x})
                        WHEN 'string' THEN ({x} #>> {Scalar}) = ANY({values})
                        WHEN 'array' THEN EXISTS (SELECT 1 FROM jsonb_array_elements_text({x}) AS element WHERE element = ANY({values}))
                        ELSE FALSE END
                    """;
            case DocumentIdsCondition ids:
                return $"CASE WHEN jsonb_typeof({x}) IN ('string', 'number') THEN ({x} #>> {Scalar}) = ANY({Add(ids.Ids.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text)}) ELSE FALSE END";
            case MatchBooleanCondition flag:
                return $"CASE WHEN jsonb_typeof({x}) = 'boolean' THEN ({x})::boolean = {Add(flag.Value, NpgsqlDbType.Boolean)} ELSE FALSE END";
            case NumberRangeCondition range:
                List<string> numbers = [];
                if (range.Min is { } min)
                {
                    numbers.Add($"({x})::float8 >= {Add(min, NpgsqlDbType.Double)}");
                }

                if (range.Max is { } max)
                {
                    numbers.Add($"({x})::float8 <= {Add(max, NpgsqlDbType.Double)}");
                }

                return numbers.Count == 0
                    ? $"jsonb_typeof({x}) = 'number'"
                    : $"CASE WHEN jsonb_typeof({x}) = 'number' THEN {string.Join(" AND ", numbers)} ELSE FALSE END";
            case DateRangeCondition range:
                var date = $"left({x} #>> {Scalar}, 10)::date";
                List<string> dates = [];
                AddBounds(dates, date, range.From is { } from ? DateOnly.FromDateTime(from) : null, range.To is { } to ? DateOnly.FromDateTime(to) : null, NpgsqlDbType.Date);
                var guard = $"jsonb_typeof({x}) = 'string' AND ({x} #>> {Scalar}) ~ {IsoDate}";
                return dates.Count == 0
                    ? guard
                    : $"CASE WHEN {guard} THEN {string.Join(" AND ", dates)} ELSE FALSE END";
            default:
                throw new NotSupportedException($"A {condition.GetType().Name} is not a condition of a PgVector search.");
        }
    }

    private static long ToLong(double value) => value >= long.MaxValue ? long.MaxValue : value <= long.MinValue ? long.MinValue : (long)value;

    private static decimal ToDecimal(double value) => value >= (double)decimal.MaxValue ? decimal.MaxValue : value <= (double)decimal.MinValue ? decimal.MinValue : (decimal)value;

    protected string Add(object value, NpgsqlDbType type)
    {
        var name = "p" + _parameters.Count.ToString(CultureInfo.InvariantCulture);
        _parameters.Add(new NpgsqlParameter(name, type) { Value = value });
        return "@" + name;
    }
}
