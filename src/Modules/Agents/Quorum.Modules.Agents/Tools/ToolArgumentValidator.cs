using System.Globalization;
using System.Text.Json;

namespace Quorum.Modules.Agents.Tools;

/// <summary>
/// Checks the arguments a model sent against the tool's JSON Schema before anything runs. It understands the keywords that
/// models get wrong in practice (<c>type</c>, <c>required</c>, <c>properties</c>, <c>additionalProperties</c>, <c>enum</c>,
/// <c>items</c>) and ignores the rest, so a rich schema from a server never blocks a valid call: the tool validates the details.
/// </summary>
internal static class ToolArgumentValidator
{
    private const string RootPath = "the arguments";

    /// <returns>What is wrong, written for the model, or <see langword="null"/> when the arguments are acceptable.</returns>
    public static string? Validate(string inputSchema, string arguments)
    {
        ArgumentNullException.ThrowIfNull(inputSchema);
        ArgumentNullException.ThrowIfNull(arguments);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
        }
        catch (JsonException)
        {
            return "The arguments are not valid JSON. Send them as one JSON object.";
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return "The arguments must be a JSON object.";
            }

            JsonDocument schema;
            try
            {
                schema = JsonDocument.Parse(inputSchema);
            }
            catch (JsonException)
            {
                return null;
            }

            using (schema)
            {
                return Check(schema.RootElement, document.RootElement, RootPath);
            }
        }
    }

    private static string? Check(JsonElement schema, JsonElement value, string path)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (schema.TryGetProperty("type", out var type) && !MatchesType(type, value))
        {
            return $"{Quote(path)} must be {Describe(type)} but is {KindName(value)}.";
        }

        if (schema.TryGetProperty("enum", out var allowed) && allowed.ValueKind == JsonValueKind.Array
            && !allowed.EnumerateArray().Any(candidate => candidate.GetRawText() == value.GetRawText()))
        {
            return $"{Quote(path)} must be one of: {string.Join(", ", allowed.EnumerateArray().Select(e => e.GetRawText()))}.";
        }

        return value.ValueKind switch
        {
            JsonValueKind.Object => CheckObject(schema, value, path),
            JsonValueKind.Array => CheckArray(schema, value, path),
            _ => null,
        };
    }

    private static string? CheckObject(JsonElement schema, JsonElement value, string path)
    {
        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in required.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String))
            {
                if (!value.TryGetProperty(name.GetString()!, out _))
                {
                    return IsRoot(path)
                        ? $"The required property '{name.GetString()}' is missing."
                        : $"'{path}' is missing the required property '{name.GetString()}'.";
                }
            }
        }

        var properties = schema.TryGetProperty("properties", out var declared) && declared.ValueKind == JsonValueKind.Object
            ? declared
            : (JsonElement?)null;
        var additional = schema.TryGetProperty("additionalProperties", out var extra) ? extra : (JsonElement?)null;

        foreach (var property in value.EnumerateObject())
        {
            if (properties is { } known && known.TryGetProperty(property.Name, out var propertySchema))
            {
                var problem = Check(propertySchema, property.Value, Child(path, property.Name));
                if (problem is not null)
                {
                    return problem;
                }
            }
            else if (additional is { ValueKind: JsonValueKind.False })
            {
                return IsRoot(path)
                    ? $"The property '{property.Name}' is not accepted by this tool."
                    : $"'{path}' has the property '{property.Name}', which this tool does not accept.";
            }
            else if (additional is { ValueKind: JsonValueKind.Object } extraSchema)
            {
                var problem = Check(extraSchema, property.Value, Child(path, property.Name));
                if (problem is not null)
                {
                    return problem;
                }
            }
        }

        return null;
    }

    private static string? CheckArray(JsonElement schema, JsonElement value, string path)
    {
        if (!schema.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            var problem = Check(items, item, $"{path}[{index.ToString(CultureInfo.InvariantCulture)}]");
            if (problem is not null)
            {
                return problem;
            }

            index++;
        }

        return null;
    }

    private static bool MatchesType(JsonElement type, JsonElement value) => type.ValueKind switch
    {
        JsonValueKind.String => MatchesType(type.GetString(), value),
        JsonValueKind.Array => type.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String && MatchesType(t.GetString(), value)),
        _ => true,
    };

    private static bool MatchesType(string? type, JsonElement value) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && IsWhole(value),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true,
    };

    private static bool IsWhole(JsonElement number) =>
        number.TryGetInt64(out _) || (number.TryGetDecimal(out var decimalValue) && decimalValue == decimal.Truncate(decimalValue));

    private static string Describe(JsonElement type) => type.ValueKind == JsonValueKind.Array
        ? "one of " + string.Join(", ", type.EnumerateArray().Select(t => t.GetString()))
        : (type.GetString() is "integer" or "object" or "array" ? "an " : "a ") + type.GetString();

    private static string KindName(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        _ => "null",
    };

    private static string Child(string path, string name) => IsRoot(path) ? name : $"{path}.{name}";

    private static string Quote(string path) => IsRoot(path) ? "The arguments" : $"'{path}'";

    private static bool IsRoot(string path) => path == RootPath;
}
