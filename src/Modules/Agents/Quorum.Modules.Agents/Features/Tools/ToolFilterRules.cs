using System.Text.Json;
using System.Text.RegularExpressions;
using Quorum.Modules.Agents.Tools;
using Quorum.Modules.KnowledgeBases.Contracts;
using Quorum.Shared.Results;

namespace Quorum.Modules.Agents.Features.Tools;

/// <summary>The codes of the errors the filter rules return: each kind of tool has its own prefix.</summary>
internal sealed record FilterErrorCodes(string InvalidFilter, string InvalidAlias, string AliasConflict, string LimitExceeded)
{
    public static FilterErrorCodes Rag { get; } = new("rag.invalid_filter", "rag.invalid_alias", "rag.alias_conflict", "rag.limit_exceeded");

    public static FilterErrorCodes Lookup { get; } = new("lookup_tool.invalid_filter", "lookup_tool.invalid_alias", "lookup_tool.alias_conflict", "lookup_tool.limit_exceeded");
}

/// <summary>The fields a tool exposes to the model, by id, and the aliases they were given.</summary>
internal sealed record ExposedFields(List<Guid> Ids, Dictionary<Guid, string> Aliases);

/// <summary>The fixed filters of a tool as stored, and the dictionary fields they name.</summary>
internal sealed record FixedFilterSet(string Json, List<Guid> FieldIds);

/// <summary>
/// What an administrator may enter for the filters of a tool that filters by dictionary fields (spec 003 D-11, D-13, A1): the exposed
/// fields, their aliases and the fixed filters. The rag tool and the lookup tool follow the same rules; only the parameters they
/// reserve for themselves and the prefix of the error codes differ.
/// </summary>
internal sealed partial class ToolFilterRules(IKnowledgeBaseCatalog catalog)
{
    /// <summary>The structure of the exposed fields and their aliases, before anything is looked up.</summary>
    /// <param name="toolKind">How the tool is called in messages: <c>rag tool</c>, <c>lookup tool</c>.</param>
    /// <param name="reserved">The parameters the tool has of its own, which no alias may take.</param>
    public static Result<ExposedFields> ParseExposed(
        IReadOnlyList<ExposedFieldRequest>? requests,
        FilterErrorCodes codes,
        string toolKind,
        int maxFields,
        IReadOnlyCollection<string> reserved)
    {
        ArgumentNullException.ThrowIfNull(codes);
        ArgumentNullException.ThrowIfNull(reserved);

        var exposedRequests = requests ?? [];
        if (exposedRequests.Any(e => e.FieldId is null || e.FieldId == Guid.Empty))
        {
            return Error.Validation(codes.InvalidFilter, "Every exposed field needs the id of a dictionary field.");
        }

        if (exposedRequests.GroupBy(e => e.FieldId).Any(g => g.Count() > 1))
        {
            return Error.Validation(codes.AliasConflict, "A field can be exposed only once.");
        }

        var ids = exposedRequests.Select(e => e.FieldId!.Value).ToList();
        if (ids.Count > maxFields)
        {
            return Error.Validation(codes.LimitExceeded, $"A {toolKind} can expose up to {maxFields} fields to the model.");
        }

        var aliases = new Dictionary<Guid, string>();
        foreach (var exposed in exposedRequests.Where(e => !string.IsNullOrWhiteSpace(e.Alias)))
        {
            var alias = exposed.Alias!.Trim();
            if (!ValidAliasPattern().IsMatch(alias) || reserved.Contains(alias) || FilterParameters.EndsLikeRange(alias))
            {
                var taken = string.Join(", ", reserved.Select(name => $"'{name}'"));
                return Error.Validation(
                    codes.InvalidAlias,
                    $"The alias '{(alias.Length <= 40 ? alias : alias[..40])}' is not allowed: 1 to 40 lower-case letters, digits and '_', starting with a letter, and not {taken} or ending in _min, _max, _from or _to.");
            }

            aliases[exposed.FieldId!.Value] = alias;
        }

        return new ExposedFields(ids, aliases);
    }

    /// <summary>
    /// The exposed fields and the fixed filters against what the knowledge bases of the tool map.
    /// </summary>
    /// <param name="seen">The tool's knowledge bases and fields as the tenant sees them.</param>
    /// <param name="reservedParameters">The parameters the tool generates besides the filters; every filter parameter must differ from them and from each other.</param>
    public async Task<Result<FixedFilterSet>> ValidateAsync(
        BindingDescription seen,
        ExposedFields exposed,
        IEnumerable<string> reservedParameters,
        JsonElement? fixedFilters,
        Guid tenantId,
        FilterErrorCodes codes,
        string toolKind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seen);
        ArgumentNullException.ThrowIfNull(exposed);
        ArgumentNullException.ThrowIfNull(codes);

        // The model can filter on what at least one of the knowledge bases maps; anything else could never match.
        var mapped = seen.KnowledgeBases.SelectMany(k => k.FieldIds).ToHashSet();
        if (exposed.Ids.Any(id => !mapped.Contains(id)))
        {
            return Error.Validation(codes.InvalidFilter, $"A field exposed to the model must be one that the knowledge bases of the {toolKind} map.");
        }

        // Every parameter the tool generates must be distinct: its own and one group per field under its alias (A1-3).
        var fieldsById = seen.Fields.ToDictionary(f => f.Id);
        var parameters = new HashSet<string>(reservedParameters, StringComparer.Ordinal);
        var aliasByParameter = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var id in exposed.Ids)
        {
            var field = fieldsById[id];
            var alias = exposed.Aliases.GetValueOrDefault(id) ?? field.Name;
            foreach (var parameter in FilterParameters.ParameterNames(alias, field.Type))
            {
                if (!parameters.Add(parameter))
                {
                    return Error.Validation(codes.AliasConflict, $"The parameter '{parameter}' would be generated twice. Give one of the fields a different alias.");
                }
            }

            if (!aliasByParameter.TryAdd(alias, id))
            {
                return Error.Validation(codes.AliasConflict, $"The alias '{alias}' is given to two fields. Give each exposed field its own.");
            }
        }

        return await FixedFiltersAsync(fixedFilters, tenantId, [.. mapped], aliasByParameter, codes, cancellationToken);
    }

    private async Task<Result<FixedFilterSet>> FixedFiltersAsync(
        JsonElement? filters,
        Guid tenantId,
        List<Guid> mappedFieldIds,
        IReadOnlyDictionary<string, Guid> aliases,
        FilterErrorCodes codes,
        CancellationToken cancellationToken)
    {
        if (filters is not { } value || value.ValueKind == JsonValueKind.Null)
        {
            return new FixedFilterSet("{}", []);
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return Error.Validation(codes.InvalidFilter, "The fixed filters must be an object whose keys are filter parameters, like {\"department\": [\"hr\"]}.");
        }

        var parameters = value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        if (parameters.Count == 0)
        {
            return new FixedFilterSet("{}", []);
        }

        var check = await catalog.CheckFixedFiltersAsync(new FixedFilterQuery(tenantId, mappedFieldIds, parameters, aliases), cancellationToken);
        return check.Problem is null
            ? new FixedFilterSet(JsonSerializer.Serialize(parameters), [.. check.FieldIds])
            : Error.Validation(codes.InvalidFilter, check.Problem);
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex ValidAliasPattern();
}
