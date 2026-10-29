using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Quorum.Modules.KnowledgeBases.Contracts;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBases.Features.Lookups;

/// <summary>The operators on the attributes of a lookup (spec 012 D-5). <c>min</c> and <c>max</c> are for numbers and dates.</summary>
internal enum LookupFilterOperator
{
    Eq,
    In,
    Min,
    Max,
}

/// <param name="Value">A scalar for <c>eq</c>, <c>min</c> and <c>max</c>; an array for <c>in</c>.</param>
internal sealed record LookupFilter(string Key, LookupFilterOperator Operator, JsonNode? Value, LookupAttributeType Type);

/// <summary>
/// The filters on the attributes of a lookup (spec 012 D-5): parsed and checked against the attribute definitions when they arrive,
/// then evaluated row by row. On a textList attribute <c>eq</c> and <c>in</c> mean "contains" and "contains any of".
/// </summary>
internal static class LookupFilters
{
    /// <summary>The row filters of a read, as the contract carries them, checked against the attributes of the lookup.</summary>
    public static Result<List<LookupFilter>> ParseRows(IReadOnlyList<LookupRowFilter>? rows, IReadOnlyList<LookupAttribute> attributes)
    {
        if (rows is null || rows.Count == 0)
        {
            return new List<LookupFilter>();
        }

        var conditions = new JsonObject();
        foreach (var row in rows)
        {
            if (conditions[row.Key] is not JsonObject operators)
            {
                operators = new JsonObject();
                conditions[row.Key] = operators;
            }

            operators[row.Operator] = JsonNode.Parse(row.Value.GetRawText());
        }

        return Parse(conditions.ToJsonString(), attributes);
    }

    /// <summary>The filters of a parsed read, in the shape the contract carries them.</summary>
    public static IReadOnlyList<LookupRowFilter> ToRows(IReadOnlyList<LookupFilter> filters) =>
        [.. filters.Select(f => new LookupRowFilter(f.Key, f.Operator.ToString().ToLowerInvariant(), JsonSerializer.SerializeToElement(f.Value)))];

    /// <summary>Parses <c>{ "&lt;key&gt;": { "eq": v } | { "in": [v] } | { "min": v, "max": v } }</c>.</summary>
    public static Result<List<LookupFilter>> Parse(string? json, IReadOnlyList<LookupAttribute> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<LookupFilter>();
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return Invalid("The filters are not valid JSON.");
        }

        if (root is not JsonObject conditions)
        {
            return Invalid("The filters are an object keyed by attribute.");
        }

        var filters = new List<LookupFilter>();
        foreach (var (key, condition) in conditions)
        {
            var attribute = attributes.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.Ordinal));
            if (attribute is null)
            {
                return Invalid($"'{key}' is not an attribute of this lookup.");
            }

            if (condition is not JsonObject operators || operators.Count == 0)
            {
                return Invalid($"The filter on '{key}' is an object with eq, in, min or max.");
            }

            foreach (var (name, value) in operators)
            {
                if (!Enum.TryParse<LookupFilterOperator>(name, ignoreCase: true, out var op) || !Enum.IsDefined(op))
                {
                    return Invalid($"'{name}' is not an operator: use eq, in, min or max.");
                }

                if (Fit(attribute, op, value) is { } problem)
                {
                    return Invalid(problem);
                }

                filters.Add(new LookupFilter(key, op, value?.DeepClone(), attribute.Type));
            }
        }

        return filters;
    }

    /// <summary>Whether a row's attributes (the stored JSON object, or <see langword="null"/>) satisfy every filter.</summary>
    public static bool Accepts(string? attributes, IReadOnlyList<LookupFilter> filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        if (filters.Count == 0)
        {
            return true;
        }

        var row = attributes is null ? null : JsonNode.Parse(attributes) as JsonObject;
        return filters.All(filter => row?[filter.Key] is { } node && Holds(node, filter));
    }

    /// <summary>Whether a stored value fits the type of its attribute (spec 012 D-3): a textList takes an array of texts.</summary>
    public static bool FitsType(LookupAttributeType type, JsonNode? value) => type == LookupAttributeType.TextList
        ? value is JsonArray items && items.All(item => FitsScalar(LookupAttributeType.Text, item))
        : FitsScalar(type, value);

    private static Result<List<LookupFilter>> Invalid(string description) => Error.Validation("lookup.invalid_filter", description);

    /// <summary>What is wrong with a filter on this attribute, or <see langword="null"/> when it is right.</summary>
    private static string? Fit(LookupAttribute attribute, LookupFilterOperator op, JsonNode? value)
    {
        var ordered = attribute.Type is LookupAttributeType.Number or LookupAttributeType.Date;
        if (op is LookupFilterOperator.Min or LookupFilterOperator.Max && !ordered)
        {
            return $"'{attribute.Key}' takes min and max only when it is a number or a date.";
        }

        if (op == LookupFilterOperator.In)
        {
            return value is JsonArray { Count: > 0 } options && options.All(option => FitsScalar(attribute.Type, option))
                ? null
                : $"'in' on '{attribute.Key}' takes a non-empty list of values of its type.";
        }

        return FitsScalar(attribute.Type, value) ? null : $"The value of '{attribute.Key}' does not fit its type.";
    }

    private static bool FitsScalar(LookupAttributeType type, JsonNode? value) => type switch
    {
        LookupAttributeType.Number => value?.GetValueKind() == JsonValueKind.Number,
        LookupAttributeType.Date => value is not null && value.GetValueKind() == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _),
        _ => value?.GetValueKind() == JsonValueKind.String,
    };

    private static bool Holds(JsonNode node, LookupFilter filter) => filter.Operator switch
    {
        LookupFilterOperator.Eq => Equal(node, filter.Value),
        LookupFilterOperator.In => filter.Value is JsonArray options && options.Any(option => Equal(node, option)),
        LookupFilterOperator.Min => Compare(node, filter.Value) is { } low && low >= 0,
        _ => Compare(node, filter.Value) is { } high && high <= 0,
    };

    private static bool Equal(JsonNode? node, JsonNode? value)
    {
        if (node is JsonArray items)
        {
            return items.Any(item => Equal(item, value));
        }

        return node is not null && value is not null && Compare(node, value) == 0 && node.GetValueKind() == value.GetValueKind();
    }

    /// <summary>Orders two numbers, or two dates, or two texts; <see langword="null"/> when they cannot be ordered.</summary>
    private static int? Compare(JsonNode? node, JsonNode? value)
    {
        if (node is null || value is null || node.GetValueKind() != value.GetValueKind())
        {
            return null;
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.Number => node.GetValue<double>().CompareTo(value.GetValue<double>()),
            JsonValueKind.String when DateTimeOffset.TryParse(node.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var left)
                && DateTimeOffset.TryParse(value.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var right) => left.CompareTo(right),
            JsonValueKind.String => string.CompareOrdinal(node.GetValue<string>(), value.GetValue<string>()),
            _ => null,
        };
    }
}
