using System.Text.Json;
using Quorum.Modules.KnowledgeBases.Contracts;

namespace Quorum.Modules.Agents.Tools;

/// <summary>
/// The filter half of the schema of a tool that filters by dictionary fields (spec 003 D-11): the parameters a field generates under the
/// name the model sees. The rag tool and the lookup tool generate them the same way.
/// </summary>
internal static class FilterParameters
{
    /// <summary>The parameters a field generates under the name the model sees: a range for a number or a date, one parameter for the rest.</summary>
    public static IReadOnlyList<string> ParameterNames(string name, FilterFieldType type) => type switch
    {
        FilterFieldType.Number => [name + "_min", name + "_max"],
        FilterFieldType.Date => [name + "_from", name + "_to"],
        _ => [name],
    };

    /// <summary>Whether an alias would collide with the parameter of a range (<c>year_min</c>...), which a field of another name generates.</summary>
    public static bool EndsLikeRange(string alias) =>
        alias.EndsWith("_min", StringComparison.Ordinal) || alias.EndsWith("_max", StringComparison.Ordinal)
        || alias.EndsWith("_from", StringComparison.Ordinal) || alias.EndsWith("_to", StringComparison.Ordinal);

    public static void WriteFilter(Utf8JsonWriter writer, FilterFieldInfo field)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(field);

        var about = string.IsNullOrWhiteSpace(field.Description) ? field.Name : field.Description.Trim();
        switch (field.Type)
        {
            case FilterFieldType.Text:
                WriteTexts(writer, field.Name, $"{about}. Matches any of the values.");
                break;
            case FilterFieldType.TextList:
                WriteTexts(writer, field.Name, $"{about}. Matches chunks that have any of the values.");
                break;
            case FilterFieldType.Boolean:
                WriteProperty(writer, field.Name, "boolean", about);
                break;
            case FilterFieldType.Number:
                WriteProperty(writer, field.Name + "_min", "number", $"{about}: the lowest value.");
                WriteProperty(writer, field.Name + "_max", "number", $"{about}: the highest value.");
                break;
            case FilterFieldType.Date:
                WriteProperty(writer, field.Name + "_from", "string", $"{about}: the earliest date, ISO 8601 (2024-01-31).");
                WriteProperty(writer, field.Name + "_to", "string", $"{about}: the latest date, ISO 8601 (2024-01-31).");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field.Type, "Unknown field type.");
        }
    }

    public static void WriteProperty(Utf8JsonWriter writer, string name, string type, string description)
    {
        writer.WriteStartObject(name);
        writer.WriteString("type", type);
        writer.WriteString("description", description);
        writer.WriteEndObject();
    }

    public static void WriteTexts(Utf8JsonWriter writer, string name, string description)
    {
        writer.WriteStartObject(name);
        writer.WriteString("type", "array");
        writer.WriteStartObject("items");
        writer.WriteString("type", "string");
        writer.WriteEndObject();
        writer.WriteString("description", description);
        writer.WriteEndObject();
    }
}
