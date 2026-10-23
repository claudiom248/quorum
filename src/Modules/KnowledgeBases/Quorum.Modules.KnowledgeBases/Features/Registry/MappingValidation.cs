using System.Text.Json;
using System.Text.Json.Nodes;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Features.Inspect;
using Quorum.Modules.KnowledgeBases.Features.Lookups;
using Quorum.Modules.KnowledgeBases.Mapping;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBases.Features.Registry;

/// <summary>The checks of a mapping that need no dictionary and no store: its shape, and how it fits the points that were sampled.</summary>
internal static class MappingValidation
{
    private const int MaxPathLength = 255;

    public static Error InvalidMapping(string message) => Error.Validation("knowledge_base.invalid_mapping", message);

    /// <summary>The roles as the administrator confirmed them, trimmed: <see langword="null"/> in the result's value is never returned, the text role is required.</summary>
    public static Result<MappingRoles> Roles(RolesDto? roles)
    {
        var text = Clean(roles?.Text);
        if (text is null)
        {
            return InvalidMapping("The mapping needs the text role: the payload field that holds the text of the chunk.");
        }

        var result = new MappingRoles(text, Clean(roles!.DocumentId), Clean(roles.Title), Clean(roles.Position), Clean(roles.SourceUrl));
        var paths = new[] { result.Text, result.DocumentId, result.Title, result.Position, result.SourceUrl }.OfType<string>().ToList();
        return paths.Any(p => p.Length > MaxPathLength)
            ? InvalidMapping($"A payload path can have up to {MaxPathLength} characters.")
            : result;
    }

    public static Error InvalidListableField(string path, DictionaryFieldType type) => Error.Validation(
        "knowledge_base.invalid_listable_field",
        $"'{path}' is mapped to a {Describe(type)} field: only text and list of texts fields can be listed, because a lookup shows the values as they are stored.");

    public static Error InvalidCountedPoint(string message) => Error.Validation("knowledge_base.invalid_counted_point", message);

    /// <summary>The entity name role, trimmed; <see langword="null"/> when there is none.</summary>
    public static Result<string?> EntityName(string? entityName)
    {
        var path = Clean(entityName);
        return path?.Length > MaxPathLength
            ? InvalidMapping($"A payload path can have up to {MaxPathLength} characters.")
            : path;
    }

    public static Error TooManyEntityAttributes() => Error.Validation(
        "knowledge_base.too_many_entity_attributes",
        $"An entity lookup lists up to {EntityAttributes.MaxAttributes} attributes: unmark some of the fields.");

    /// <summary>The attributes of the entity lookup: they belong to a knowledge base that names its entities, and there are at most ten.</summary>
    public static Error? EntityAttributeProblem(string? entityName, IReadOnlyList<MappedFieldRequest> fields)
    {
        var count = fields.Count(f => f.EntityAttribute);
        if (count > EntityAttributes.MaxAttributes)
        {
            return TooManyEntityAttributes();
        }

        return count > 0 && entityName is null
            ? InvalidMapping("Entity attributes need the entity name role: map the payload field that holds the name of each entity.")
            : null;
    }

    /// <summary>The shape of the counted point: a path and a scalar. Whether the scalar fits the field comes later, <see cref="CountedPointFits"/>.</summary>
    public static Result<CountedPoint?> ReadCountedPoint(CountedPointRequest? request)
    {
        if (request is null)
        {
            return (CountedPoint?)null;
        }

        var path = Clean(request.Path);
        if (path is null || path.Length > MaxPathLength)
        {
            return InvalidCountedPoint($"The counted point needs the payload path it reads, up to {MaxPathLength} characters.");
        }

        return (request.Value is { } value ? CountedPoint.TryCreate(path, value) : null) is { } point
            ? point
            : InvalidCountedPoint("The value of the counted point must be true, a number or a text of up to 200 characters.");
    }

    /// <summary>
    /// Whether the value of the counted point fits the field it reads: the type of the dictionary field when the path is mapped as a
    /// metadata field, otherwise what the sampled points hold. A path no sampled point has cannot be checked and is accepted.
    /// </summary>
    public static Error? CountedPointFits(CountedPoint? point, IEnumerable<(string Path, DictionaryFieldType Type)> mapped, IReadOnlyList<JsonObject> sample)
    {
        if (point is null)
        {
            return null;
        }

        var kind = point.ToJsonElement().ValueKind;
        foreach (var (path, type) in mapped)
        {
            if (path == point.Path)
            {
                return FitsScalar(type, kind)
                    ? null
                    : InvalidCountedPoint($"The value of the counted point does not fit the {Describe(type)} dictionary field '{path}' is mapped to.");
            }
        }

        foreach (var payload in sample)
        {
            if (TryRead(payload, point.Path, out var value) && value is not null && Category(value.GetValueKind()) != Category(kind))
            {
                return InvalidCountedPoint($"The value of the counted point does not fit the values of '{point.Path}' in the sampled points.");
            }
        }

        return null;
    }

    public static Error? Paths(MappingRoles roles, string? entityName, IReadOnlyList<MappedFieldRequest> fields)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rolePaths = new[] { roles.Text, roles.DocumentId, roles.Title, roles.Position, roles.SourceUrl, entityName }.OfType<string>();
        foreach (var path in rolePaths)
        {
            if (!seen.Add(path))
            {
                return InvalidMapping($"'{path}' is used for two roles: each payload field can have only one meaning.");
            }
        }

        foreach (var field in fields)
        {
            var path = Clean(field.Path);
            if (path is null || path.Length > MaxPathLength)
            {
                return InvalidMapping($"Every metadata field needs the payload path it reads, up to {MaxPathLength} characters.");
            }

            if (!seen.Add(path))
            {
                return InvalidMapping($"'{path}' is mapped twice: each payload field can have only one meaning.");
            }
        }

        return null;
    }

    /// <summary>Whether the sampled values of every mapped metadata field fit the type of their dictionary field (ISO 8601 for dates).</summary>
    public static Error? Values(IReadOnlyList<JsonObject> sample, IEnumerable<(string Path, DictionaryFieldType Type)> fields)
    {
        foreach (var (path, type) in fields)
        {
            foreach (var payload in sample)
            {
                if (TryRead(payload, path, out var value) && value is not null && !Fits(type, value))
                {
                    return InvalidMapping($"The values of '{path}' do not fit the {Describe(type)} dictionary field it is mapped to. Map it to a field of the right type.");
                }
            }
        }

        return null;
    }

    /// <summary>Warnings for mapped paths that none of the sampled points has: the payload changed since the mapping was made (D-8).</summary>
    public static IEnumerable<InspectWarning> MissingPaths(IReadOnlyList<JsonObject> sample, KnowledgeBaseMapping mapping)
    {
        if (sample.Count == 0)
        {
            yield break;
        }

        var roles = mapping.Roles;
        var paths = new[] { roles.Text, roles.DocumentId, roles.Title, roles.Position, roles.SourceUrl, mapping.EntityName, mapping.CountedPoint?.Path }
            .OfType<string>()
            .Concat(mapping.Fields.Select(f => f.Path))
            .Distinct(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (!sample.Any(payload => TryRead(payload, path, out _)))
            {
                yield return new InspectWarning(
                    WarningCodes.MappedPathMissing,
                    $"'{path}' is in none of the sampled points: the payload may have changed since the mapping was made. Until the mapping is corrected, RAG answers may lack what it should read from there.",
                    path);
            }
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Finds a payload value by its path in dot notation; <paramref name="value"/> is <see langword="null"/> for an explicit JSON null.</summary>
    private static bool TryRead(JsonObject payload, string path, out JsonNode? value)
    {
        JsonNode? current = payload;
        foreach (var segment in path.Split('.'))
        {
            if (current is not JsonObject node || !node.TryGetPropertyValue(segment, out current))
            {
                value = null;
                return false;
            }
        }

        value = current;
        return true;
    }

    private static bool Fits(DictionaryFieldType type, JsonNode value)
    {
        var kind = value.GetValueKind();
        return type switch
        {
            DictionaryFieldType.Text => kind == JsonValueKind.String,
            DictionaryFieldType.Number => kind == JsonValueKind.Number,
            DictionaryFieldType.Boolean => kind is JsonValueKind.True or JsonValueKind.False,
            DictionaryFieldType.Date => kind == JsonValueKind.String && Iso8601.IsDateOrDateTime(value.GetValue<string>()),
            DictionaryFieldType.TextList => value is JsonArray list && list.All(item => item?.GetValueKind() == JsonValueKind.String),
            _ => false,
        };
    }

    /// <summary>Booleans are one category: a counted point of <c>true</c> reads a field whose points hold <c>true</c> or <c>false</c>.</summary>
    private static JsonValueKind Category(JsonValueKind kind) => kind == JsonValueKind.False ? JsonValueKind.True : kind;

    private static bool FitsScalar(DictionaryFieldType type, JsonValueKind kind) => type switch
    {
        DictionaryFieldType.Text => kind == JsonValueKind.String,
        DictionaryFieldType.Number => kind == JsonValueKind.Number,
        DictionaryFieldType.Boolean => kind == JsonValueKind.True,
        _ => false,
    };

    private static string Describe(DictionaryFieldType type) => type switch
    {
        DictionaryFieldType.TextList => "list of texts",
        DictionaryFieldType.Date => "date (ISO 8601)",
        _ => type.ToString().ToLowerInvariant(),
    };
}
