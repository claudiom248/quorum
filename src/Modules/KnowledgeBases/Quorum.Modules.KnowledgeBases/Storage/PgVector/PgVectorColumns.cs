using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Quorum.Modules.KnowledgeBases.Domain;

namespace Quorum.Modules.KnowledgeBases.Storage.PgVector;

/// <summary>How the module reads a PostgreSQL column (spec 008 D-9).</summary>
internal enum PgVectorColumnKind
{
    Text,
    Integer,
    Decimal,
    Float,
    Boolean,
    Date,
    Timestamp,
    TimestampTz,
    Uuid,
    Enum,
    TextArray,
    Json,

    /// <summary>A <c>vector</c> or <c>halfvec</c> column: searched, never returned and never mapped.</summary>
    Vector,

    /// <summary>A type that cannot be mapped (<c>bytea</c>, geometric types, ranges, <c>interval</c>, arrays of other types, ...).</summary>
    Other,
}

/// <summary>A column of a relation as the catalog describes it.</summary>
/// <param name="TypeName">The type's name in the catalog, resolved through a domain to its base type (<c>int4</c>, <c>_text</c>, <c>vector</c>).</param>
/// <param name="DisplayType">What <c>format_type</c> shows (<c>vector(64)</c>, <c>character varying(20)</c>).</param>
/// <param name="TypeModifier">For <c>vector</c> and <c>halfvec</c>: the dimensions, or -1 when the column declares none.</param>
internal sealed record PgVectorColumn(string Name, string TypeName, string TypeKind, string DisplayType, int TypeModifier)
{
    public PgVectorColumnKind Kind => Classify(TypeName, TypeKind);

    public bool IsVector => Kind == PgVectorColumnKind.Vector;

    /// <summary>The dimensions the column fixes, or <see langword="null"/> when it does not.</summary>
    public int? Dimensions => IsVector && TypeModifier > 0 ? TypeModifier : null;

    /// <summary>A path is split on dots, so a column whose name has one cannot be addressed.</summary>
    public bool HasUnsupportedName => Name.Length == 0 || Name.Contains('.', StringComparison.Ordinal);

    /// <summary>The dictionary type of the column, or <see langword="null"/> when its values are read from inside (a <c>json</c> column), or not at all.</summary>
    public DictionaryFieldType? FieldType => Kind switch
    {
        PgVectorColumnKind.Text or PgVectorColumnKind.Uuid or PgVectorColumnKind.Enum => DictionaryFieldType.Text,
        PgVectorColumnKind.Integer or PgVectorColumnKind.Decimal or PgVectorColumnKind.Float => DictionaryFieldType.Number,
        PgVectorColumnKind.Boolean => DictionaryFieldType.Boolean,
        PgVectorColumnKind.Date or PgVectorColumnKind.Timestamp or PgVectorColumnKind.TimestampTz => DictionaryFieldType.Date,
        PgVectorColumnKind.TextArray => DictionaryFieldType.TextList,
        _ => null,
    };

    /// <summary>Whether a sample reads the column: a mappable one, or a <c>json</c> column whose keys are mappable.</summary>
    public bool IsSampled => !HasUnsupportedName && (FieldType is not null || Kind == PgVectorColumnKind.Json);

    private static PgVectorColumnKind Classify(string typeName, string typeKind) => typeName switch
    {
        "text" or "varchar" or "bpchar" => PgVectorColumnKind.Text,
        "int2" or "int4" or "int8" => PgVectorColumnKind.Integer,
        "numeric" => PgVectorColumnKind.Decimal,
        "float4" or "float8" => PgVectorColumnKind.Float,
        "bool" => PgVectorColumnKind.Boolean,
        "date" => PgVectorColumnKind.Date,
        "timestamp" => PgVectorColumnKind.Timestamp,
        "timestamptz" => PgVectorColumnKind.TimestampTz,
        "uuid" => PgVectorColumnKind.Uuid,
        "_text" or "_varchar" or "_bpchar" => PgVectorColumnKind.TextArray,
        "json" or "jsonb" => PgVectorColumnKind.Json,
        "vector" or "halfvec" => PgVectorColumnKind.Vector,
        _ when typeKind == "e" => PgVectorColumnKind.Enum,
        _ => PgVectorColumnKind.Other,
    };
}

/// <summary>Turns the columns of a row into the JSON the mapping works on (spec 008 D-9): a column is a property, a <c>json</c> column a nested object.</summary>
internal static class PgVectorRows
{
    /// <summary>The longest text a sample keeps: only the type of a value matters there.</summary>
    public const int MaxSampledLength = 256;

    /// <summary>
    /// The select-list entry of a column for a sample. Long texts are cut by the server, so a table of long chunks does not travel
    /// whole to be thrown away.
    /// </summary>
    public static string SampleExpression(PgVectorColumn column) =>
        column.Kind == PgVectorColumnKind.Text
            ? string.Create(CultureInfo.InvariantCulture, $"left({Quote(column.Name)}, {MaxSampledLength})")
            : Quote(column.Name);

    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>The value of column <paramref name="ordinal"/> of the current row, or <see langword="null"/> when it is null or cannot be written as JSON.</summary>
    public static JsonNode? Read(NpgsqlDataReader reader, int ordinal, PgVectorColumnKind kind)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        switch (kind)
        {
            case PgVectorColumnKind.Text:
            case PgVectorColumnKind.Enum:
                return Cut(reader.GetValue(ordinal).ToString());
            case PgVectorColumnKind.Uuid:
                return JsonValue.Create(reader.GetGuid(ordinal).ToString());
            case PgVectorColumnKind.Integer:
                return JsonValue.Create(Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture));
            case PgVectorColumnKind.Decimal:
                return JsonValue.Create(reader.GetDecimal(ordinal));
            case PgVectorColumnKind.Float:
                var number = Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
                return double.IsFinite(number) ? JsonValue.Create(number) : null;
            case PgVectorColumnKind.Boolean:
                return JsonValue.Create(reader.GetBoolean(ordinal));
            case PgVectorColumnKind.Date:
                return JsonValue.Create(reader.GetFieldValue<DateOnly>(ordinal).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            case PgVectorColumnKind.Timestamp:
            case PgVectorColumnKind.TimestampTz:
                // A timestamp without a time zone is read as UTC (D-9).
                var moment = reader.GetFieldValue<DateTime>(ordinal);
                var utc = moment.Kind == DateTimeKind.Local ? moment.ToUniversalTime() : DateTime.SpecifyKind(moment, DateTimeKind.Utc);
                return JsonValue.Create(utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            case PgVectorColumnKind.TextArray:
                return new JsonArray(reader.GetFieldValue<string?[]>(ordinal).Where(item => item is not null).Select(item => Cut(item)).ToArray());
            case PgVectorColumnKind.Json:
                return ParseJson(reader.GetString(ordinal));
            default:
                return null;
        }
    }

    private static JsonValue? Cut(string? text) =>
        text is null ? null : JsonValue.Create(text.Length > MaxSampledLength ? text[..MaxSampledLength] : text);

    // A json column that does not hold an object or an array carries one value for the whole column, which is rare and still addressable.
    private static JsonNode? ParseJson(string json)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        return parsed is null ? null : CutTexts(parsed);
    }

    private static JsonNode CutTexts(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(property => property.Key).ToList())
                {
                    if (obj[key] is { } child && CutTexts(child) is var cut && !ReferenceEquals(cut, child))
                    {
                        obj[key] = cut;
                    }
                }

                return obj;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is { } item && CutTexts(item) is var cut && !ReferenceEquals(cut, item))
                    {
                        array[i] = cut;
                    }
                }

                return array;
            case JsonValue value when value.TryGetValue<string>(out var text) && text.Length > MaxSampledLength:
                return JsonValue.Create(text[..MaxSampledLength]);
            default:
                return node;
        }
    }
}
