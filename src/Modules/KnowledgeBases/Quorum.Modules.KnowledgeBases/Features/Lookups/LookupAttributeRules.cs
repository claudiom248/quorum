using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBases.Features.Lookups;

/// <summary>An attribute as the administrator sent it, before validation.</summary>
internal sealed record LookupAttributeRequest(string? Key, string? Label, string? Type, Guid? DictionaryFieldId);

/// <summary>An attribute of a lookup as the administrator reads it.</summary>
internal sealed record LookupAttributeResponse(string Key, string? Label, string Type, Guid? DictionaryFieldId);

/// <summary>The rules every attribute of a lookup follows (spec 012 D-3), for the entity and the manual lookups alike.</summary>
internal static partial class LookupAttributeRules
{
    public const int MaxKeyLength = 40;

    private const int MaxTextLength = LookupValue.MaxValueLength;

    private static readonly Error TooMany = Error.Validation("lookup.invalid_attribute", $"A lookup has at most {Lookup.MaxAttributes} attributes.");

    private static readonly Error InvalidKey = Error.Validation(
        "lookup.invalid_attribute",
        $"An attribute key is 1 to {MaxKeyLength} characters: lower-case letters, digits and '_', starting with a letter.");

    public static Result<List<LookupAttribute>> Validate(IReadOnlyList<LookupAttributeRequest>? requests, bool entity)
    {
        var all = requests ?? [];
        if (all.Count > Lookup.MaxAttributes)
        {
            return TooMany;
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var attributes = new List<LookupAttribute>(all.Count);
        foreach (var request in all)
        {
            if (request.Key is null || !KeyPattern().IsMatch(request.Key))
            {
                return InvalidKey;
            }

            if (!keys.Add(request.Key))
            {
                return Error.Validation("lookup.invalid_attribute", $"The attribute '{request.Key}' is listed twice.");
            }

            if (!DictionaryFieldRules.TryParseMember<LookupAttributeType>(request.Type, out var type))
            {
                return Error.Validation("lookup.invalid_attribute", "An attribute type is Text, Number, Date or TextList.");
            }

            if (request.Label?.Length > DictionaryField.MaxNameLength * 2)
            {
                return Error.Validation("lookup.invalid_attribute", "An attribute label can have up to 80 characters.");
            }

            if (!entity && request.DictionaryFieldId is not null)
            {
                return Error.Validation("lookup.invalid_attribute", "Only an entity lookup takes attributes from the mapping of its knowledge base.");
            }

            attributes.Add(new LookupAttribute(request.Key, string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim(), type, request.DictionaryFieldId));
        }

        return attributes;
    }

    public static bool IsValidKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    /// <summary>
    /// The attributes of one value of a manual lookup (spec 012 D-3): each one defined by the lookup, of its type, cut to the length of a value.
    /// Returns the stored JSON, or <see langword="null"/> when the value has none. A failure names the row and the column.
    /// </summary>
    public static Result<string?> Values(JsonObject? values, IReadOnlyList<LookupAttribute> attributes, string row)
    {
        if (values is null)
        {
            return (string?)null;
        }

        var kept = new JsonObject();
        foreach (var (key, node) in values)
        {
            if (node is null)
            {
                continue;
            }

            if (attributes.All(a => a.Key != key))
            {
                return InvalidValue($"Row '{row}' has a value for '{key}', which is not an attribute of this lookup.");
            }

            var attribute = attributes.First(a => a.Key == key);
            if (!LookupFilters.FitsType(attribute.Type, node) || (node is JsonValue text && text.GetValueKind() == JsonValueKind.String && node.GetValue<string>().Length > MaxTextLength))
            {
                return InvalidValue($"Row '{row}', column '{key}': the value does not fit the type {attribute.Type} (a text has at most {MaxTextLength} characters).");
            }

            kept[key] = node.DeepClone();
        }

        return kept.Count == 0 ? (string?)null : kept.ToJsonString();
    }

    private static Error InvalidValue(string description) => Error.Validation("lookup.invalid_value", description);

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex KeyPattern();
}
