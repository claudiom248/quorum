using System.Globalization;
using System.Text.Json;
using Quorum.Web.Client.Api;

namespace Quorum.Web.Client;

public enum FilterInputKind
{
    /// <summary>One or several texts, separated by commas or new lines.</summary>
    Texts,
    Number,
    Date,
    Boolean,
}

/// <param name="Name">The parameter of the generated tool schema (spec 003 D-11): <c>department</c>, <c>year_min</c>...</param>
/// <param name="Label">What the admin reads next to the input.</param>
public sealed record FilterParameter(string Name, string FieldName, FilterInputKind Kind, string Label);

/// <summary>
/// The filter parameters of a dictionary field and the reading of what is typed for them. Fixed filters of a rag tool and the
/// filters of a test search have the same shapes as the parameters of the generated tool schema (spec 003 D-11).
/// </summary>
public static class FilterInputs
{
    public const int MaxValues = 20;
    public const int MaxValueLength = 200;

    /// <summary>The parameters a field gives: text and list → one, number → <c>_min</c>/<c>_max</c>, date → <c>_from</c>/<c>_to</c>, boolean → one.</summary>
    public static IReadOnlyList<FilterParameter> ParametersOf(string fieldName, string type)
    {
        ArgumentException.ThrowIfNullOrEmpty(fieldName);

        return type switch
        {
            DictionaryFieldTypes.Number =>
            [
                new($"{fieldName}_min", fieldName, FilterInputKind.Number, $"{fieldName} from"),
                new($"{fieldName}_max", fieldName, FilterInputKind.Number, $"{fieldName} up to"),
            ],
            DictionaryFieldTypes.Date =>
            [
                new($"{fieldName}_from", fieldName, FilterInputKind.Date, $"{fieldName} from"),
                new($"{fieldName}_to", fieldName, FilterInputKind.Date, $"{fieldName} until"),
            ],
            DictionaryFieldTypes.Boolean => [new(fieldName, fieldName, FilterInputKind.Boolean, fieldName)],
            DictionaryFieldTypes.TextList => [new(fieldName, fieldName, FilterInputKind.Texts, $"{fieldName} contains any of")],
            _ => [new(fieldName, fieldName, FilterInputKind.Texts, $"{fieldName} is any of")],
        };
    }

    /// <summary>
    /// Reads the typed values into the filters to send. An empty input is no filter. Text values are split on commas and new lines,
    /// so a value cannot contain a comma.
    /// </summary>
    public static (Dictionary<string, object>? Filters, string? Error) Read(IReadOnlyDictionary<string, string> inputs, IEnumerable<FilterParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(parameters);

        var filters = new Dictionary<string, object>();
        foreach (var parameter in parameters)
        {
            var text = inputs.GetValueOrDefault(parameter.Name)?.Trim() ?? string.Empty;
            if (text.Length == 0)
            {
                continue;
            }

            switch (parameter.Kind)
            {
                case FilterInputKind.Texts:
                    var values = text.Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (values.Length > MaxValues || values.Any(v => v.Length > MaxValueLength))
                    {
                        return (null, $"{parameter.Label}: at most {MaxValues} values of {MaxValueLength} characters.");
                    }

                    if (values.Length > 0)
                    {
                        filters[parameter.Name] = values;
                    }

                    break;
                case FilterInputKind.Number:
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                    {
                        return (null, $"{parameter.Label}: \"{text}\" is not a number.");
                    }

                    filters[parameter.Name] = number;
                    break;
                case FilterInputKind.Date:
                    if (!IsIsoDate(text))
                    {
                        return (null, $"{parameter.Label}: \"{text}\" is not a date. Use the form 2025-01-31.");
                    }

                    filters[parameter.Name] = text;
                    break;
                default:
                    if (!bool.TryParse(text, out var flag))
                    {
                        return (null, $"{parameter.Label}: choose yes or no.");
                    }

                    filters[parameter.Name] = flag;
                    break;
            }
        }

        return (filters, null);
    }

    private static bool IsIsoDate(string text) =>
        text.Length >= 10 && text[4] == '-' && text[7] == '-'
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _);

    /// <summary>The inputs that show stored filters again (the fixed filters of a saved rag tool).</summary>
    public static Dictionary<string, string> Show(JsonElement filters)
    {
        var inputs = new Dictionary<string, string>();
        if (filters.ValueKind != JsonValueKind.Object)
        {
            return inputs;
        }

        foreach (var property in filters.EnumerateObject())
        {
            inputs[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.Array => string.Join(", ", property.Value.EnumerateArray().Select(v => v.ToString())),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => string.Empty,
                _ => property.Value.ToString(),
            };
        }

        return inputs;
    }
}
