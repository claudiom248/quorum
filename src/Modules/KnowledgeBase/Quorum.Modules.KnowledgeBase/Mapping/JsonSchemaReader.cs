using System.Text;
using System.Text.Json;
using Json.Schema;
using Quorum.Modules.KnowledgeBase.Domain;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBase.Mapping;

/// <summary>
/// Reads the properties of a pasted plain JSON Schema (draft 07 or 2020-12) with inline <c>properties</c>: whoever runs the
/// ingestion knows nothing about Quorum, so there are no Quorum keywords. <c>type</c>, nested <c>properties</c>, <c>items</c> of
/// scalars and <c>format: date</c> / <c>date-time</c> are read; other structural keywords are ignored with a warning and
/// <c>$ref</c> is not resolved.
/// </summary>
internal static class JsonSchemaReader
{
    public const string InvalidSchemaCode = "knowledge_base.invalid_schema";

    public const int MaxBytes = 100 * 1024;

    private const int MaxDepth = 8;

    private const int MaxProperties = 500;

    // Keywords that describe or constrain a value without changing which properties exist: ignoring them loses nothing the mapping needs.
    private static readonly HashSet<string> HarmlessKeywords = new(StringComparer.Ordinal)
    {
        "$schema", "$id", "$comment", "$anchor", "title", "description", "default", "examples", "required", "enum", "const",
        "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf", "minLength", "maxLength", "pattern",
        "minItems", "maxItems", "uniqueItems", "minProperties", "maxProperties", "additionalProperties", "readOnly", "writeOnly",
        "deprecated", "contentMediaType", "contentEncoding",
    };

    public static Result<Discovery> Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (Encoding.UTF8.GetByteCount(text) > MaxBytes)
        {
            return Invalid($"The schema is larger than {MaxBytes / 1024} KB.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException)
        {
            return Invalid("The schema is not valid JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Invalid("The schema must be a JSON object that describes an object.");
            }

            if (MetaSchemaOf(root) is { } metaSchema && FirstProblem(metaSchema, root) is { } problem)
            {
                return Invalid($"The schema is not a valid JSON Schema: {problem}");
            }

            if (root.TryGetProperty("type", out var rootType) && TypeOf(rootType) != "object")
            {
                return Invalid("The schema must describe an object (\"type\": \"object\").");
            }

            if (!root.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object || !properties.EnumerateObject().Any())
            {
                return Invalid("The schema has no \"properties\" to map.");
            }

            var reader = new Walker();
            reader.NoteKeywords(root);
            reader.Walk(properties, prefix: string.Empty, depth: 0);
            return new Discovery(reader.Properties, reader.Warnings);
        }
    }

    /// <summary>
    /// The meta-schema of the draft the schema declares (2020-12 when it declares none), or <see langword="null"/> for a draft that
    /// the library has no meta-schema for: such a schema is read as it is, without the check.
    /// </summary>
    private static JsonSchema? MetaSchemaOf(JsonElement root)
    {
        if (!root.TryGetProperty("$schema", out var declared) || declared.ValueKind != JsonValueKind.String)
        {
            return MetaSchemas.Draft202012;
        }

        var id = declared.GetString()!.TrimEnd('#');
        return id == MetaSchemas.Draft202012Id.OriginalString.TrimEnd('#') ? MetaSchemas.Draft202012
            : id == MetaSchemas.Draft201909Id.OriginalString.TrimEnd('#') ? MetaSchemas.Draft201909
            : id == MetaSchemas.Draft7Id.OriginalString.TrimEnd('#') ? MetaSchemas.Draft7
            : id == MetaSchemas.Draft6Id.OriginalString.TrimEnd('#') ? MetaSchemas.Draft6
            : null;
    }

    private static string? FirstProblem(JsonSchema metaSchema, JsonElement schema)
    {
        var results = metaSchema.Evaluate(schema, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (results.IsValid)
        {
            return null;
        }

        var detail = results.Details?.FirstOrDefault(d => !d.IsValid && d.Errors is { Count: > 0 }) ?? results;
        var message = detail.Errors?.Values.FirstOrDefault() ?? "it does not follow the specification";
        return detail.InstanceLocation.ToString() is { Length: > 0 } where ? $"{message} (at {where})" : message;
    }

    private static Error InvalidError(string detail) => Error.Validation(InvalidSchemaCode, detail);

    private static Result<Discovery> Invalid(string detail) => InvalidError(detail);

    /// <summary>The single type a schema's <c>type</c> keyword names: <c>["string", "null"]</c> is a string that may be missing.</summary>
    private static string? TypeOf(JsonElement type) => type.ValueKind switch
    {
        JsonValueKind.String => type.GetString(),
        JsonValueKind.Array => type.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()).FirstOrDefault(t => t != "null"),
        _ => null,
    };

    private sealed class Walker
    {
        private readonly HashSet<string> _warnedKeywords = new(StringComparer.Ordinal);

        public List<DiscoveredProperty> Properties { get; } = [];

        public List<InspectWarning> Warnings { get; } = [];

        public void Walk(JsonElement properties, string prefix, int depth)
        {
            foreach (var property in properties.EnumerateObject())
            {
                var path = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";
                if (property.Name.Length == 0 || property.Name.Contains('.', StringComparison.Ordinal))
                {
                    Warnings.Add(new InspectWarning(
                        WarningCodes.UnsupportedPropertyName,
                        $"'{path}' has a '.' (or no name) in the property name, which filters cannot address, so it is left out.",
                        path));
                    continue;
                }

                if (Properties.Count >= MaxProperties)
                {
                    Warnings.Add(new InspectWarning(
                        WarningCodes.TooManyProperties,
                        $"The schema has more than {MaxProperties} properties; only the first {MaxProperties} are shown.",
                        Path: null));
                    return;
                }

                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    NotMapped(path, "its schema is not an object");
                    continue;
                }

                NoteKeywords(property.Value);
                Visit(property.Value, path, depth);
            }
        }

        public void NoteKeywords(JsonElement schema)
        {
            foreach (var keyword in schema.EnumerateObject().Select(p => p.Name))
            {
                if (keyword is "type" or "properties" or "items" or "format" || HarmlessKeywords.Contains(keyword))
                {
                    continue;
                }

                if (_warnedKeywords.Add(keyword))
                {
                    Warnings.Add(new InspectWarning(
                        WarningCodes.UnsupportedSchemaKeyword,
                        $"The schema uses '{keyword}', which is ignored (references and combinations are not resolved): properties that depend on it may be missing or have the wrong type.",
                        Path: null));
                }
            }
        }

        private void Visit(JsonElement schema, string path, int depth)
        {
            var type = schema.TryGetProperty("type", out var typeElement) ? TypeOf(typeElement) : null;
            switch (type)
            {
                case "object":
                    if (depth + 1 < MaxDepth && schema.TryGetProperty("properties", out var nested) && nested.ValueKind == JsonValueKind.Object)
                    {
                        Walk(nested, path, depth + 1);
                    }
                    else
                    {
                        NotMapped(path, "it is an object without inline properties");
                    }

                    break;

                case "string":
                    var format = schema.TryGetProperty("format", out var formatElement) && formatElement.ValueKind == JsonValueKind.String ? formatElement.GetString() : null;
                    Properties.Add(new DiscoveredProperty(path, format is "date" or "date-time" ? DictionaryFieldType.Date : DictionaryFieldType.Text));
                    break;

                case "integer" or "number":
                    Properties.Add(new DiscoveredProperty(path, DictionaryFieldType.Number));
                    break;

                case "boolean":
                    Properties.Add(new DiscoveredProperty(path, DictionaryFieldType.Boolean));
                    break;

                case "array":
                    VisitArray(schema, path);
                    break;

                default:
                    NotMapped(path, "its type is missing or not supported");
                    break;
            }
        }

        private void VisitArray(JsonElement schema, string path)
        {
            if (!schema.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object)
            {
                NotMapped(path, "it is a list without an items schema");
                return;
            }

            NoteKeywords(items);
            switch (items.TryGetProperty("type", out var itemType) ? TypeOf(itemType) : null)
            {
                case "string":
                    Properties.Add(new DiscoveredProperty(path, DictionaryFieldType.TextList));
                    break;

                case "integer" or "number" or "boolean":
                    Properties.Add(new DiscoveredProperty(path, DictionaryFieldType.TextList));
                    Warnings.Add(new InspectWarning(
                        WarningCodes.MixedTypes,
                        $"'{path}' is a list whose values are not texts. It is proposed as a list of texts; check the data before relying on filters over it.",
                        path));
                    break;

                default:
                    NotMapped(path, "it is a list of objects or nested lists");
                    break;
            }
        }

        private void NotMapped(string path, string reason) =>
            Warnings.Add(new InspectWarning(WarningCodes.PropertyNotMapped, $"'{path}' cannot be mapped to a metadata field: {reason}.", path));
    }
}
