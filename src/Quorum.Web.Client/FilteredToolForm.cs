using System.Text.RegularExpressions;
using Quorum.Web.Client.Api;

namespace Quorum.Web.Client;

/// <summary>
/// What a rag tool and a lookup tool have in common in their editors: a name, a description, knowledge bases, the dictionary fields
/// the model may filter on (with aliases), the filters it can never change, a policy and a scope. Kept apart from the markup so it
/// can be tested alone; the server checks the same limits again.
/// </summary>
public abstract partial class FilteredToolForm
{
    private static readonly string[] ReservedAliasSuffixes = ["_min", "_max", "_from", "_to"];

    private readonly HashSet<Guid> _knownCandidates = [];

    /// <summary>The tool being edited; <see langword="null"/> for a new one.</summary>
    public Guid? Id { get; init; }

    public string OriginalName { get; init; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Policy { get; set; } = ToolPolicies.Auto;

    public List<Guid> KnowledgeBaseIds { get; } = [];

    public HashSet<Guid> ExposedFieldIds { get; } = [];

    /// <summary>The alias given to an exposed field, by field id: the parameter name the model sees. A field without one is known by its name.</summary>
    public Dictionary<Guid, string> Aliases { get; } = [];

    /// <summary>Fields with a fixed filter, in the order they were added.</summary>
    public List<Guid> FixedFilterFieldIds { get; } = [];

    /// <summary>What was typed for each filter parameter (<c>year_min</c>...).</summary>
    public Dictionary<string, string> FilterValues { get; } = [];

    /// <summary>Stored filter parameters this editor cannot show (their field is gone): saving drops them.</summary>
    public List<string> UnshownFilters { get; } = [];

    public ScopeForm Scope { get; set; } = new();

    public bool IsNew => Id is null;

    /// <summary>The prefix of the name the model sees: <c>rag</c> for <c>rag__name</c>.</summary>
    protected abstract string Prefix { get; }

    protected abstract int MaxExposedFields { get; }

    /// <summary>The tool's own parameters: no alias can be one of them, and no filter parameter can be called like one.</summary>
    protected abstract IReadOnlyList<string> ReservedNames { get; }

    /// <summary>
    /// Brings exposed and fixed-filter fields in line with the fields the selected knowledge bases map: the ones no longer offered
    /// go, and the ones newly offered are exposed (the default of D-11) while there is room.
    /// </summary>
    public void ApplyCandidates(IReadOnlyList<DictionaryField> candidates, bool exposeNew)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var offered = candidates.Select(f => f.Id).ToHashSet();
        ExposedFieldIds.RemoveWhere(id => !offered.Contains(id));
        foreach (var gone in Aliases.Keys.Where(id => !offered.Contains(id)).ToList())
        {
            Aliases.Remove(gone);
        }

        foreach (var removed in FixedFilterFieldIds.Where(id => !offered.Contains(id)).ToList())
        {
            RemoveFixedFilter(removed, candidates);
        }

        if (exposeNew)
        {
            foreach (var field in candidates.Where(f => !_knownCandidates.Contains(f.Id)))
            {
                if (ExposedFieldIds.Count < MaxExposedFields)
                {
                    ExposedFieldIds.Add(field.Id);
                }
            }
        }

        _knownCandidates.UnionWith(offered);
    }

    /// <summary>Marks the fields as known without exposing them (a tool loaded for editing: its exposure is what it was).</summary>
    public void KnowCandidates(IReadOnlyList<DictionaryField> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        _knownCandidates.UnionWith(candidates.Select(f => f.Id));
    }

    /// <summary>The name a filter on the field uses: its alias when the field is exposed, otherwise its own name.</summary>
    public string FilterNameOf(DictionaryField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return ExposedFieldIds.Contains(field.Id) ? Aliases.GetValueOrDefault(field.Id) ?? field.Name : field.Name;
    }

    /// <summary>The parameters of a filter on the field, under the name the model (or the fixed filter) uses for it.</summary>
    public IReadOnlyList<FilterParameter> ParametersOf(DictionaryField field) => FilterInputs.ParametersOf(FilterNameOf(field), field.Type);

    /// <summary>Gives an exposed field an alias, or takes it back with an empty one. What was typed for a fixed filter on the field follows its new name.</summary>
    public void SetAlias(DictionaryField field, string? alias)
    {
        ArgumentNullException.ThrowIfNull(field);

        var before = ParametersOf(field);
        var clean = alias?.Trim();
        if (string.IsNullOrEmpty(clean) || clean == field.Name)
        {
            Aliases.Remove(field.Id);
        }
        else
        {
            Aliases[field.Id] = clean;
        }

        var after = ParametersOf(field);
        for (var i = 0; i < before.Count; i++)
        {
            if (FilterValues.Remove(before[i].Name, out var value))
            {
                FilterValues[after[i].Name] = value;
            }
        }
    }

    public void AddFixedFilter(Guid fieldId)
    {
        if (!FixedFilterFieldIds.Contains(fieldId))
        {
            FixedFilterFieldIds.Add(fieldId);
        }
    }

    public void RemoveFixedFilter(Guid fieldId, IReadOnlyList<DictionaryField> dictionary)
    {
        FixedFilterFieldIds.Remove(fieldId);
        if (dictionary.FirstOrDefault(f => f.Id == fieldId) is { } field)
        {
            foreach (var parameter in ParametersOf(field))
            {
                FilterValues.Remove(parameter.Name);
            }
        }
    }

    /// <summary>Takes what a saved tool has: the exposed fields with their aliases and the fixed filters it can show.</summary>
    /// <param name="mapped">The fields the tool's knowledge bases map, which its fixed filters may name; every dictionary field when not given.</param>
    protected void LoadFilters(IEnumerable<ExposedFieldInfo> exposed, System.Text.Json.JsonElement fixedFilters, IReadOnlyList<DictionaryField> dictionary, IReadOnlyList<DictionaryField>? mapped)
    {
        ArgumentNullException.ThrowIfNull(exposed);
        ArgumentNullException.ThrowIfNull(dictionary);

        foreach (var field in exposed)
        {
            ExposedFieldIds.Add(field.FieldId);

            // The detail gives the name the model sees; an alias is kept only when it is not the field's own name.
            if (field.Alias is { Length: > 0 } alias && dictionary.FirstOrDefault(f => f.Id == field.FieldId) is { } known && alias != known.Name)
            {
                Aliases[field.FieldId] = alias;
            }
        }

        var stored = FilterInputs.Show(fixedFilters);
        foreach (var field in mapped ?? dictionary)
        {
            var parameters = ParametersOf(field);
            if (parameters.Any(p => stored.ContainsKey(p.Name)))
            {
                FixedFilterFieldIds.Add(field.Id);
                foreach (var parameter in parameters.Where(p => stored.ContainsKey(p.Name)))
                {
                    FilterValues[parameter.Name] = stored[parameter.Name];
                }
            }
        }

        var shown = FixedFilterFieldIds
            .SelectMany(id => ParametersOf(dictionary.First(f => f.Id == id)))
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        UnshownFilters.AddRange(stored.Keys.Where(k => !shown.Contains(k)));
    }

    protected string? AliasToSend(Guid id, IReadOnlyList<DictionaryField> dictionary) =>
        Aliases.TryGetValue(id, out var alias) && alias.Trim() is { Length: > 0 } clean && dictionary.FirstOrDefault(f => f.Id == id)?.Name != clean ? clean : null;

    protected string? NameProblem(int maxLength) =>
        ValidName().IsMatch(Name.Trim())
            ? null
            : $"The name is 1 to {maxLength} characters: lower-case letters, digits, \"-\" and \"_\", starting with a letter or digit. The model sees it as {Prefix}__name.";

    protected string? DescriptionProblem(int maxLength, string purpose) =>
        Description.Trim().Length == 0 || Description.Length > maxLength
            ? $"The description is required, up to {maxLength} characters: the model reads it to decide {purpose}."
            : null;

    protected string? ExposedCountProblem() =>
        ExposedFieldIds.Count > MaxExposedFields ? $"A {Prefix} tool can expose up to {MaxExposedFields} fields to the model." : null;

    protected string? ScopeProblem() => Scope.Build().Error;

    // The parameters of a tool must all be distinct (spec 003 A1-3): the server checks the same, this says it before saving.
    protected List<string> AliasProblems(IReadOnlyList<DictionaryField> dictionary)
    {
        var problems = new List<string>();
        var parameters = new HashSet<string>(ReservedNames, StringComparer.Ordinal);

        var aliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in dictionary.Where(f => ExposedFieldIds.Contains(f.Id)).OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            var alias = FilterNameOf(field);
            if (Aliases.ContainsKey(field.Id) && !AliasPattern().IsMatch(alias))
            {
                problems.Add($"The alias \"{alias}\" is not allowed: 1 to 40 lower-case letters, digits and \"_\", starting with a letter.");
                continue;
            }

            if (Aliases.ContainsKey(field.Id) && (ReservedNames.Contains(alias, StringComparer.Ordinal) || ReservedAliasSuffixes.Any(s => alias.EndsWith(s, StringComparison.Ordinal))))
            {
                problems.Add($"The alias \"{alias}\" is reserved: tools use {string.Join(", ", ReservedNames.Select(n => $"\"{n}\""))} and the endings \"_min\", \"_max\", \"_from\", \"_to\".");
                continue;
            }

            var generated = FilterInputs.ParametersOf(alias, field.Type).Select(p => p.Name).ToList();
            if (!aliases.Add(alias) || generated.Any(name => !parameters.Add(name)))
            {
                problems.Add($"Two exposed fields would both be called \"{alias}\" for the model. Give one of them an alias.");
            }
        }

        return problems;
    }

    // A fixed filter with no value would restrict nothing, and the server refuses it: it must be filled or removed.
    protected (Dictionary<string, object>? Filters, string? Error) ReadFixedFilters(IReadOnlyList<DictionaryField> dictionary)
    {
        var all = new Dictionary<string, object>();
        foreach (var id in FixedFilterFieldIds)
        {
            if (dictionary.FirstOrDefault(f => f.Id == id) is not { } field)
            {
                continue;
            }

            var (filters, error) = FilterInputs.Read(FilterValues, ParametersOf(field));
            if (error is not null)
            {
                return (null, error);
            }

            if (filters!.Count == 0)
            {
                return (null, $"The fixed filter on {field.Name} has no value: fill it or remove it.");
            }

            foreach (var (key, value) in filters)
            {
                all[key] = value;
            }
        }

        return (all, null);
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,39}\z", RegexOptions.CultureInvariant)]
    private static partial Regex AliasPattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]{0,39}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ValidName();
}
