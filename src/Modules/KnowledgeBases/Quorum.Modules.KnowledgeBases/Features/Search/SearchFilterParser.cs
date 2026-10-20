using System.Globalization;
using System.Text.Json;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Mapping;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBases.Features.Search;

/// <summary>
/// Reads the filter parameters of a rag tool call (spec 003 D-11) and checks each against the type of its dictionary field, so that
/// nothing that does not fit is ever sent to a store. Messages say what to fix, because the model reads them.
/// </summary>
internal static class SearchFilterParser
{
    public const int MaxValues = 20;

    public const int MaxValueLength = 200;

    private static readonly string[] Suffixes = ["_min", "_max", "_from", "_to"];

    /// <param name="arguments">The parameters by name: <c>department</c>, <c>year_min</c>, <c>published_on_to</c>...</param>
    /// <param name="fields">The dictionary fields that may be filtered on, by name.</param>
    /// <param name="errorCode">The code of the error returned for a value that does not fit.</param>
    /// <param name="ignoreEmpty">Whether a null, an empty list or an empty text counts as no filter (the model sends them for "nothing to filter"). A fixed filter must never be ignored: one that restricts nothing is a mistake that would widen access.</param>
    public static Result<IReadOnlyList<FieldFilter>> Parse(
        IReadOnlyDictionary<string, JsonElement> arguments,
        IReadOnlyDictionary<string, FilterField> fields,
        string errorCode,
        bool ignoreEmpty)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(fields);

        var exact = new List<FieldFilter>();
        var minimums = new Dictionary<FilterField, double>();
        var maximums = new Dictionary<FilterField, double>();
        var starts = new Dictionary<FilterField, DateTimeOffset>();
        var ends = new Dictionary<FilterField, DateTimeOffset>();

        // Sorted, so the same call is always read (and refused) in the same order.
        foreach (var (name, value) in arguments.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            if (IsNothing(value) && ignoreEmpty)
            {
                continue;
            }

            string? problem;
            if (fields.TryGetValue(name, out var field))
            {
                problem = ReadExact(field, name, value, exact);
            }
            else if (SplitSuffix(name, fields) is var (target, suffix))
            {
                problem = suffix switch
                {
                    "_min" or "_max" when target.Type == DictionaryFieldType.Number => ReadNumber(name, value, suffix == "_min" ? minimums : maximums, target),
                    "_from" or "_to" when target.Type == DictionaryFieldType.Date => ReadDate(name, value, suffix == "_from" ? starts : ends, target, endOfDay: suffix == "_to"),
                    _ => $"'{name}' is not a filter.",
                };
            }
            else
            {
                problem = $"'{name}' is not a filter.";
            }

            if (problem is not null)
            {
                return Error.Validation(errorCode, problem);
            }
        }

        var filters = new List<FieldFilter>(exact);
        foreach (var field in minimums.Keys.Concat(maximums.Keys).Distinct())
        {
            double? min = minimums.TryGetValue(field, out var low) ? low : null;
            double? max = maximums.TryGetValue(field, out var high) ? high : null;
            if (min > max)
            {
                return Error.Validation(errorCode, $"'{field.Name}_min' is greater than '{field.Name}_max'.");
            }

            filters.Add(new NumberRange(field, min, max));
        }

        foreach (var field in starts.Keys.Concat(ends.Keys).Distinct())
        {
            DateTimeOffset? from = starts.TryGetValue(field, out var start) ? start : null;
            DateTimeOffset? to = ends.TryGetValue(field, out var end) ? end : null;
            if (from > to)
            {
                return Error.Validation(errorCode, $"'{field.Name}_from' is later than '{field.Name}_to'.");
            }

            filters.Add(new DateRange(field, from, to));
        }

        return filters;
    }

    /// <summary>A model that has nothing to filter on often sends <c>null</c>, an empty list or an empty string instead of leaving the parameter out.</summary>
    private static bool IsNothing(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        JsonValueKind.Array => value.GetArrayLength() == 0,
        JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()),
        _ => false,
    };

    private static (FilterField Field, string Suffix)? SplitSuffix(string name, IReadOnlyDictionary<string, FilterField> fields)
    {
        foreach (var suffix in Suffixes)
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal) && fields.TryGetValue(name[..^suffix.Length], out var field))
            {
                return (field, suffix);
            }
        }

        return null;
    }

    private static string? ReadExact(FilterField field, string name, JsonElement value, List<FieldFilter> filters)
    {
        switch (field.Type)
        {
            case DictionaryFieldType.Text or DictionaryFieldType.TextList:
                return ReadTexts(field, name, value, filters);

            case DictionaryFieldType.Boolean:
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    filters.Add(new IsBoolean(field, value.GetBoolean()));
                    return null;
                }

                return $"'{name}' must be true or false.";

            case DictionaryFieldType.Number:
                return $"'{name}' is a number: use '{name}_min' and/or '{name}_max'.";

            default:
                return $"'{name}' is a date: use '{name}_from' and/or '{name}_to'.";
        }
    }

    private static string? ReadTexts(FilterField field, string name, JsonElement value, List<FieldFilter> filters)
    {
        // A single text is accepted as a list of one: the schema says list, but the meaning is the same.
        var items = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToList() : [value];
        if (items.Count == 0)
        {
            return $"'{name}' needs at least one value.";
        }

        if (items.Count > MaxValues)
        {
            return $"'{name}' can have at most {MaxValues} values.";
        }

        var texts = new List<string>(items.Count);
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(item.GetString()))
            {
                return $"'{name}' must be a list of non-empty texts.";
            }

            var text = item.GetString()!;
            if (text.Length > MaxValueLength)
            {
                return $"The values of '{name}' can have at most {MaxValueLength} characters.";
            }

            texts.Add(text);
        }

        filters.Add(new AnyOf(field, texts));
        return null;
    }

    private static string? ReadNumber(string name, JsonElement value, Dictionary<FilterField, double> bounds, FilterField field)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
        {
            return $"'{name}' must be a number.";
        }

        bounds[field] = number;
        return null;
    }

    private static string? ReadDate(string name, JsonElement value, Dictionary<FilterField, DateTimeOffset> bounds, FilterField field, bool endOfDay)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
        if (!Iso8601.IsDateOrDateTime(text))
        {
            return $"'{name}' must be an ISO 8601 date such as 2024-01-31.";
        }

        var instant = DateTimeOffset.Parse(text!.Replace(' ', 'T'), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        // Asked for up to a day, the whole of that day is meant.
        bounds[field] = endOfDay && text.Length == 10 ? instant.AddDays(1).AddTicks(-1) : instant;
        return null;
    }
}
