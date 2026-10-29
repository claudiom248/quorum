using System.Text.Json.Nodes;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBases.Features.Lookups;

/// <summary>What the administrator sent for a manual lookup (spec 007 D-5, spec 012 D-3), exactly as it arrived.</summary>
internal sealed record ManualLookupRequest(string? Name, string? Description, string? Scope, List<Guid>? ScopeTenantIds, List<ManualValueRequest>? Values, List<LookupAttributeRequest>? Attributes = null);

/// <param name="Attributes">The attribute values of the row, keyed by attribute (spec 012 D-3).</param>
internal sealed record ManualValueRequest(string? Value, string? Label, List<string?>? Synonyms, JsonObject? Attributes = null);

/// <summary>A manual lookup that passed validation: trimmed, with the scope parsed.</summary>
internal sealed record ValidManualLookup(string Name, string Description, LookupScope Scope, List<Guid> ScopeTenantIds, List<ManualValue> Values, List<LookupAttribute> Attributes);

/// <param name="Attributes">The stored JSON of the row's attributes, or <see langword="null"/>.</param>
internal sealed record ManualValue(string Value, string? Label, List<string> Synonyms, string? Attributes = null);

internal static class ManualLookupValidation
{
    private static readonly Error InvalidName = Error.Validation(
        "lookup.invalid_name",
        $"The name must be 1 to {DictionaryField.MaxNameLength} characters: lower-case letters, digits and '_', starting with a letter.");

    private static readonly Error InvalidDescription = Error.Validation(
        "lookup.invalid_description",
        $"The description can have up to {Lookup.MaxDescriptionLength} characters.");

    private static readonly Error TooManyValues = Error.Validation(
        "lookup.too_many_values",
        $"A manual lookup has at most {Lookup.MaxManualValues} values.");

    public static Result<ValidManualLookup> Validate(ManualLookupRequest? request)
    {
        if (request is null || !DictionaryFieldRules.IsValidName(request.Name))
        {
            return InvalidName;
        }

        // Spec 012 D-2: a manual lookup is asked its description on creation, so the model always has one to read.
        if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Trim().Length > Lookup.MaxDescriptionLength)
        {
            return InvalidDescription;
        }

        var scope = ValidateScope(request);
        if (!scope.IsSuccess)
        {
            return scope.Error;
        }

        var attributes = LookupAttributeRules.Validate(request.Attributes, entity: false);
        if (!attributes.IsSuccess)
        {
            return attributes.Error;
        }

        var values = request.Values ?? [];
        if (values.Count > Lookup.MaxManualValues)
        {
            return TooManyValues;
        }

        var valid = new List<ManualValue>(values.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var checkedValue = ValidateValue(value, attributes.Value);
            if (!checkedValue.IsSuccess)
            {
                return checkedValue.Error;
            }

            if (!seen.Add(checkedValue.Value.Value))
            {
                return InvalidValue($"The value '{checkedValue.Value.Value}' is listed twice, whatever its case.");
            }

            valid.Add(checkedValue.Value);
        }

        return new ValidManualLookup(
            request.Name!,
            request.Description.Trim(),
            scope.Value.Scope,
            scope.Value.TenantIds,
            valid,
            attributes.Value);
    }

    private static Result<(LookupScope Scope, List<Guid> TenantIds)> ValidateScope(ManualLookupRequest request)
    {
        if (!DictionaryFieldRules.TryParseMember<LookupScope>(request.Scope, out var scope))
        {
            return Error.Validation("lookup.invalid_scope", "The scope must be Global or Tenant.");
        }

        var tenantIds = (request.ScopeTenantIds ?? []).Distinct().ToList();
        var problem = scope switch
        {
            LookupScope.Global when tenantIds.Count > 0 => "A global scope has no tenants.",
            _ when tenantIds.Contains(Guid.Empty) => "An id in the scope is empty.",
            _ => null,
        };

        return problem is null
            ? (scope, tenantIds)
            : Error.Validation("lookup.invalid_scope", problem);
    }

    private static Result<ManualValue> ValidateValue(ManualValueRequest? request, List<LookupAttribute> attributes)
    {
        var value = request?.Value?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > LookupValue.MaxValueLength)
        {
            return InvalidValue($"A value must have 1 to {LookupValue.MaxValueLength} characters.");
        }

        var label = string.IsNullOrWhiteSpace(request!.Label) ? null : request.Label.Trim();
        if (label?.Length > LookupValue.MaxValueLength)
        {
            return InvalidValue($"A label can have up to {LookupValue.MaxValueLength} characters.");
        }

        var synonyms = new List<string>();
        foreach (var synonym in request.Synonyms ?? [])
        {
            var trimmed = synonym?.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.Length > LookupValue.MaxValueLength)
            {
                return InvalidValue($"A synonym must have 1 to {LookupValue.MaxValueLength} characters.");
            }

            synonyms.Add(trimmed);
        }

        if (synonyms.Count > LookupValue.MaxSynonyms)
        {
            return InvalidValue($"A value has at most {LookupValue.MaxSynonyms} synonyms.");
        }

        var stored = LookupAttributeRules.Values(request.Attributes, attributes, value);
        if (!stored.IsSuccess)
        {
            return stored.Error;
        }

        return new ManualValue(value, label, synonyms, stored.Value);
    }

    private static Error InvalidValue(string description) => Error.Validation("lookup.invalid_value", description);
}
