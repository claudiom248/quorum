using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.KnowledgeBases.Domain;

namespace Quorum.Modules.KnowledgeBases.Features.Lookups;

/// <summary>
/// The filters on the attributes of a snapshot read (spec 012 D-5), as a statement over the rows of one generation. Every key and value is a
/// parameter. A number or a date is cast inside a CASE on its JSON type, so a row of another type is never cast: PostgreSQL does not
/// promise the order in which the parts of an AND are evaluated, a CASE does.
/// </summary>
internal static class LookupFilterSql
{
    /// <param name="filters">Checked against <paramref name="attributes"/> already.</param>
    public static FormattableString Where(Guid sliceId, int generation, IReadOnlyList<LookupFilter> filters)
    {
        ArgumentNullException.ThrowIfNull(filters);

        var arguments = new List<object> { sliceId, generation };
        var conditions = new List<string> { "\"SliceId\" = {0}", "\"Generation\" = {1}" };
        foreach (var filter in filters)
        {
            conditions.Add(Condition(arguments, filter, filter.Type));
        }

        var sql = "SELECT * FROM knowledge_base.lookup_values WHERE " + string.Join(" AND ", conditions);
        return FormattableStringFactory.Create(sql, [.. arguments]);
    }

    private static string Condition(List<object> arguments, LookupFilter filter, LookupAttributeType type)
    {
        var key = Add(arguments, filter.Key);
        var raw = $"(\"Attributes\" -> {key})";
        var text = $"(\"Attributes\" ->> {key})";
        var value = filter.Value;
        var inList = filter.Operator == LookupFilterOperator.In;

        return type switch
        {
            LookupAttributeType.Number => Ordered(arguments, filter, $"(CASE WHEN jsonb_typeof({raw}) = 'number' THEN {text}::double precision END)", inList, value, numeric: true),
            LookupAttributeType.Date => Ordered(arguments, filter, $"(CASE WHEN jsonb_typeof({raw}) = 'string' THEN {text}::timestamptz END)", inList, value, numeric: false),
            LookupAttributeType.TextList => Texts(arguments, filter, raw, text, inList, containsOne: true),
            _ => Texts(arguments, filter, raw, text, inList, containsOne: false),
        };
    }

    private static string Ordered(List<object> arguments, LookupFilter filter, string column, bool inList, System.Text.Json.Nodes.JsonNode? value, bool numeric)
    {
        if (filter.Operator is LookupFilterOperator.Min or LookupFilterOperator.Max)
        {
            var comparison = filter.Operator == LookupFilterOperator.Min ? ">=" : "<=";
            return $"{column} {comparison} {Add(arguments, Scalar(value!, numeric))}";
        }

        if (inList)
        {
            var items = value!.AsArray().Select(item => Scalar(item!, numeric)).ToArray();
            return numeric
                ? $"{column} = ANY({Add(arguments, items.Cast<double>().ToArray())}::double precision[])"
                : $"{column} = ANY({Add(arguments, items.Cast<DateTimeOffset>().ToArray())}::timestamptz[])";
        }

        return $"{column} = {Add(arguments, Scalar(value!, numeric))}";
    }

    private static string Texts(List<object> arguments, LookupFilter filter, string raw, string text, bool inList, bool containsOne)
    {
        var value = filter.Value!;
        if (inList)
        {
            var items = Add(arguments, value.AsArray().Select(item => item!.GetValue<string>()).ToArray());
            return containsOne
                ? $"({raw} ?| {items}::text[])"
                : $"({text} = ANY({items}::text[]))";
        }

        var one = Add(arguments, value.GetValue<string>());
        return containsOne
            ? $"({text} = {one} OR {raw} @> jsonb_build_array({one}::text))"
            : $"({text} = {one})";
    }

    private static object Scalar(System.Text.Json.Nodes.JsonNode value, bool numeric) => numeric
        ? value.GetValue<double>()
        : DateTimeOffset.Parse(value.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);

    private static string Add(List<object> arguments, object value)
    {
        arguments.Add(value);
        return "{" + (arguments.Count - 1) + "}";
    }
}
