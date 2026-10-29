using System.Text.Json;
using System.Text.RegularExpressions;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.Agents.Tools.Lookup;
using Quorum.Modules.KnowledgeBases.Contracts;
using Quorum.Shared.Results;

namespace Quorum.Modules.Agents.Features.Tools;

/// <param name="Policy">The policy asked for, or <see langword="null"/> when the request left it out.</param>
internal sealed record ValidatedLookupTool(string Name, string Description, ToolPolicy? Policy, LookupConfiguration Configuration);

/// <summary>What an administrator may enter for a lookup tool (spec 007 D-12, D-6 and the rule between scopes).</summary>
internal sealed partial class LookupToolValidation(AgentsDbContext db, IKnowledgeBaseCatalog catalog, IKnowledgeBaseLookups lookups, ToolFilterRules filters)
{
    public const int MaxNameLength = 40;

    public const int MaxDescriptionLength = 1_000;

    private static readonly Error InvalidName = Error.Validation(
        "lookup_tool.invalid_name",
        $"The name must be 1 to {MaxNameLength} characters: lower-case letters, digits, '-' and '_', starting with a letter or digit.");

    private static readonly Error InvalidDescription = Error.Validation(
        "lookup_tool.invalid_description",
        $"The description is required, up to {MaxDescriptionLength} characters: the model reads it to decide when to read the lists.");

    private static readonly Error InvalidPolicy = Error.Validation("lookup_tool.invalid_policy", "The policy must be Auto or Confirm.");

    private static readonly Error NoLookup = Error.Validation("lookup_tool.no_lookup", "A lookup tool needs at least one lookup.");

    private static readonly Error TooManyLookups = Error.Validation(
        "lookup_tool.too_many_lookups",
        $"A lookup tool can bind up to {LookupTool.MaxLookups} lookups.");

    private static readonly Error TooManyKnowledgeBases = Error.Validation(
        "lookup_tool.too_many_knowledge_bases",
        $"A lookup tool can query up to {LookupTool.MaxKnowledgeBases} knowledge bases.");

    private static readonly Error NotVisible = Error.Validation(
        "lookup_tool.lookup_not_visible",
        "Every lookup, knowledge base and field of a lookup tool must be visible to everyone the tool is for. Check the scope of the lookups, the knowledge bases and the fields, or narrow the scope of the tool.");

    public async Task<Result<ValidatedLookupTool>> ValidateAsync(LookupToolRequest request, Guid tenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = request.Name?.Trim();
        if (name is null || !ValidNamePattern().IsMatch(name))
        {
            return InvalidName;
        }

        var description = request.Description?.Trim();
        if (string.IsNullOrEmpty(description) || description.Length > MaxDescriptionLength)
        {
            return InvalidDescription;
        }

        ToolPolicy? policy = null;
        if (request.Policy is not null)
        {
            if (!Enum.TryParse<ToolPolicy>(request.Policy, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed) || int.TryParse(request.Policy, out _))
            {
                return InvalidPolicy;
            }

            policy = parsed;
        }

        var bound = ParseLookups(request.Lookups);
        if (!bound.IsSuccess)
        {
            return bound.Error;
        }

        var knowledgeBaseIds = (request.KnowledgeBaseIds ?? []).Distinct().ToList();
        if (knowledgeBaseIds.Contains(Guid.Empty))
        {
            return NotVisible;
        }

        if (knowledgeBaseIds.Count > LookupTool.MaxKnowledgeBases)
        {
            return TooManyKnowledgeBases;
        }

        var exposed = ToolFilterRules.ParseExposed(request.ExposedFields, FilterErrorCodes.Lookup, "lookup tool", LookupTool.MaxExposedFields, LookupToolDefinition.ReservedParameters);
        if (!exposed.IsSuccess)
        {
            return exposed.Error;
        }

        var scope = await ToolScopeRules.ValidateAsync(request.Scope, db, cancellationToken);
        if (!scope.IsSuccess)
        {
            return scope.Error;
        }

        var lookupIds = bound.Value.Ids;
        var seenLookups = await lookups.ListAsync(new LookupListQuery(tenantId, AssistantId: null, lookupIds), cancellationToken);
        if (seenLookups.Count != lookupIds.Count)
        {
            return NotVisible;
        }

        // The model tells the lists apart by name: the alias, or the lookup's own. Two lists of one name need an alias on one of them.
        var names = seenLookups.GroupBy(l => bound.Value.Aliases.GetValueOrDefault(l.Id) ?? l.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (names is not null)
        {
            return Error.Validation("lookup_tool.alias_conflict", $"Two lookups would be called '{names.Key}'. Give one of them an alias.");
        }

        // The filters name fields of the knowledge bases the tool queries and of those its lookups come from.
        var lookupBases = seenLookups.Select(l => l.KnowledgeBaseId).OfType<Guid>();
        var seen = await catalog.DescribeAsync(
            new BindingQuery(tenantId, AssistantId: null, [.. knowledgeBaseIds.Concat(lookupBases).Distinct()], exposed.Value.Ids),
            cancellationToken);
        if (knowledgeBaseIds.Any(id => seen.KnowledgeBases.All(k => k.Id != id)))
        {
            return NotVisible;
        }

        // A field that a single value fixes is not offered to the model at all: exposing it would only invite a value it cannot change (spec 012 D-7).
        if (exposed.Value.Ids.Count > 0 && request.FixedFilters is { ValueKind: JsonValueKind.Object } fixedJson)
        {
            foreach (var id in exposed.Value.Ids)
            {
                var field = seen.Fields.FirstOrDefault(f => f.Id == id);
                var parameter = exposed.Value.Aliases.GetValueOrDefault(id) ?? field?.Name;
                if (parameter is not null && fixedJson.TryGetProperty(parameter, out var fixedValue) && IsSingleValue(fixedValue))
                {
                    return Error.Validation("lookup_tool.field_fixed", $"The field '{parameter}' is fixed by a filter with one value: remove it from the exposed fields or widen the filter.");
                }
            }
        }

        var lookupFilters = ValidateLookupFilters(request.LookupFilters, lookupIds, seenLookups);
        if (!lookupFilters.IsSuccess)
        {
            return lookupFilters.Error;
        }

        var fixedFilters = await filters.ValidateAsync(
            seen,
            exposed.Value,
            LookupToolDefinition.ReservedParameters,
            request.FixedFilters,
            tenantId,
            FilterErrorCodes.Lookup,
            "lookup tool",
            cancellationToken);
        if (!fixedFilters.IsSuccess)
        {
            return fixedFilters.Error;
        }

        var bindingScope = new BindingScope(ToolScopeRules.KindOf(scope.Value.Kind), scope.Value.TenantIds, scope.Value.AssistantIds);
        var check = await catalog.CheckScopeAsync(
            new ScopeCheckQuery(tenantId, bindingScope, knowledgeBaseIds, [.. exposed.Value.Ids.Concat(fixedFilters.Value.FieldIds).Distinct()]),
            cancellationToken);
        if (!check.IsVisible || (await lookups.HiddenAsync(new LookupScopeQuery(tenantId, bindingScope, lookupIds), cancellationToken)).Count > 0)
        {
            return NotVisible;
        }

        return new ValidatedLookupTool(
            name,
            description,
            policy,
            new LookupConfiguration(
                lookupIds,
                bound.Value.Aliases,
                knowledgeBaseIds,
                exposed.Value.Ids,
                exposed.Value.Aliases,
                fixedFilters.Value.Json,
                fixedFilters.Value.FieldIds,
                scope.Value.Kind,
                scope.Value.TenantIds,
                scope.Value.AssistantIds,
                lookupFilters.Value));
    }

    private static readonly Error InvalidLookupFilter = Error.Validation("lookup_tool.invalid_filter", "The fixed filters on lookup attributes do not match the lookups the tool binds.");

    /// <summary>
    /// The fixed conditions on attributes of entity and manual lookups (spec 012 D-7): each names a bound lookup and one of its attributes,
    /// with an operator that the attribute's type takes and a value that fits the type. Returns the JSON to store.
    /// </summary>
    private static Result<string> ValidateLookupFilters(JsonElement? json, IReadOnlyList<Guid> bound, IReadOnlyList<LookupInfo> seen)
    {
        if (json is null || json.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return "{}";
        }

        if (json.Value.ValueKind != JsonValueKind.Object)
        {
            return InvalidLookupFilter;
        }

        foreach (var perLookup in json.Value.EnumerateObject())
        {
            if (!Guid.TryParse(perLookup.Name, out var lookupId) || !bound.Contains(lookupId) || seen.FirstOrDefault(l => l.Id == lookupId) is not { } info)
            {
                return InvalidLookupFilter;
            }

            if (perLookup.Value.ValueKind != JsonValueKind.Object)
            {
                return InvalidLookupFilter;
            }

            foreach (var attribute in perLookup.Value.EnumerateObject())
            {
                var definition = info.Attributes?.FirstOrDefault(a => a.Key == attribute.Name);
                if (definition is null || attribute.Value.ValueKind != JsonValueKind.Object)
                {
                    return InvalidLookupFilter;
                }

                foreach (var condition in attribute.Value.EnumerateObject())
                {
                    if (!FitsCondition(definition.Type, condition.Name, condition.Value))
                    {
                        return InvalidLookupFilter;
                    }
                }
            }
        }

        return json.Value.GetRawText();
    }

    /// <summary>The operators of spec 012 D-5 that the type takes, with a value of the type; <c>min</c> and <c>max</c> are for numbers and dates.</summary>
    private static bool FitsCondition(string type, string operatorName, JsonElement value) => operatorName switch
    {
        "eq" => FitsValue(type, value),
        "in" => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0 && value.EnumerateArray().All(item => FitsValue(type, item)),
        "min" or "max" => type is "Number" or "Date" && FitsValue(type, value),
        _ => false,
    };

    /// <summary>A fixed filter with one value: a scalar, or a list of exactly one.</summary>
    private static bool IsSingleValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => value.GetArrayLength() == 1,
        JsonValueKind.Null or JsonValueKind.Undefined => false,
        _ => true,
    };

    private static bool FitsValue(string type, JsonElement value) => type switch
    {
        "Number" => value.ValueKind == JsonValueKind.Number,
        "Date" => value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out _),
        _ => value.ValueKind == JsonValueKind.String,
    };

    private static Result<BoundLookups> ParseLookups(List<LookupBindingRequest>? requests)
    {
        var bound = requests ?? [];
        if (bound.Count == 0)
        {
            return NoLookup;
        }

        if (bound.Count > LookupTool.MaxLookups)
        {
            return TooManyLookups;
        }

        if (bound.Any(b => b.LookupId is null || b.LookupId == Guid.Empty))
        {
            return NotVisible;
        }

        if (bound.GroupBy(b => b.LookupId).Any(g => g.Count() > 1))
        {
            return Error.Validation("lookup_tool.alias_conflict", "A lookup can be bound only once.");
        }

        var aliases = new Dictionary<Guid, string>();
        foreach (var item in bound.Where(b => !string.IsNullOrWhiteSpace(b.Alias)))
        {
            var alias = item.Alias!.Trim();
            if (!ValidAliasPattern().IsMatch(alias))
            {
                return Error.Validation(
                    "lookup_tool.invalid_alias",
                    $"The alias '{(alias.Length <= 40 ? alias : alias[..40])}' is not allowed: 1 to 40 lower-case letters, digits and '_', starting with a letter.");
            }

            aliases[item.LookupId!.Value] = alias;
        }

        return new BoundLookups([.. bound.Select(b => b.LookupId!.Value)], aliases);
    }

    private sealed record BoundLookups(List<Guid> Ids, Dictionary<Guid, string> Aliases);

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex ValidAliasPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,39}$")]
    private static partial Regex ValidNamePattern();
}

/// <param name="FixedFilters">The filters the model never sees, in the shape of the tool's parameters: <c>{"department": ["hr"], "year_min": 2024}</c>.</param>
/// <param name="Lookups">The lists the tool reads, each with the alias the model sees for it (the lookup's own name when left out).</param>
/// <param name="KnowledgeBaseIds">The knowledge bases the live operations query.</param>
internal sealed record LookupToolRequest(
    string? Name,
    string? Description,
    List<LookupBindingRequest>? Lookups,
    List<Guid>? KnowledgeBaseIds,
    List<ExposedFieldRequest>? ExposedFields,
    JsonElement? FixedFilters,
    ScopeRequest? Scope,
    string? Policy,
    JsonElement? LookupFilters = null);

internal sealed record LookupBindingRequest(Guid? LookupId, string? Alias = null);
