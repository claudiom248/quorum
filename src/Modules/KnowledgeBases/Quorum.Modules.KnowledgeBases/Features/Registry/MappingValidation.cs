using System.Text.Json;
using System.Text.Json.Nodes;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Features.Inspect;
using Quorum.Modules.KnowledgeBases.Mapping;
using Quorum.Modules.KnowledgeBases.Storage;
using Quorum.Modules.KnowledgeBases.Storage.Qdrant;
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

    public static Error? Paths(MappingRoles roles, IReadOnlyList<MappedFieldRequest> fields)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rolePaths = new[] { roles.Text, roles.DocumentId, roles.Title, roles.Position, roles.SourceUrl }.OfType<string>();
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
        var paths = new[] { roles.Text, roles.DocumentId, roles.Title, roles.Position, roles.SourceUrl }.OfType<string>().Concat(mapping.Fields.Select(f => f.Path));
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

    private static string Describe(DictionaryFieldType type) => type switch
    {
        DictionaryFieldType.TextList => "list of texts",
        DictionaryFieldType.Date => "date (ISO 8601)",
        _ => type.ToString().ToLowerInvariant(),
    };

    /// <summary>The vector the mapping will search must exist: required when the collection has several, and it must be one of them.</summary>
    public static Error? Vector(SourceInfo? info, string? vectorName)
    {
        if (info is null || info.Vectors.Count == 0)
        {
            return null;
        }

        if (vectorName is null)
        {
            return info.Vectors.Count > 1
                ? Error.Validation(
                    WarningCodes.VectorNameRequired,
                    $"The collection has several vectors ({Names(info)}): choose the one to search.")
                : null;
        }

        return info.Vectors.Any(v => v.Name == vectorName)
            ? null
            : Error.Validation(WarningCodes.VectorNotFound, $"The collection has no vector named '{vectorName}'. Its vectors are: {Names(info)}.");
    }

    private static string Names(SourceInfo info) => string.Join(", ", info.Vectors.Select(v => v.Name is null ? "(unnamed)" : $"'{v.Name}'"));
}
