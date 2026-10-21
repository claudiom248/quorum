using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Quorum.Web.Client.Api;

namespace Quorum.Web.Client;

/// <summary>The dictionary fields a rag tool can expose or filter on, given the knowledge bases it searches.</summary>
/// <param name="Complete">
/// <see langword="false"/> when a selected knowledge base belongs to another tenant: its mapping is not visible here, so every
/// dictionary field is offered and the server refuses the ones no knowledge base maps.
/// </param>
public sealed record FieldChoices(IReadOnlyList<DictionaryField> Fields, bool Complete);

/// <summary>What the rag tool editor of the Tools page shows and reads, kept apart from the markup so it can be tested alone.</summary>
public static class RagToolView
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The fields mapped by the selected knowledge bases (spec 003 D-11: "the fields used by the KBs").</summary>
    public static FieldChoices Candidates(
        IEnumerable<Guid> selectedKnowledgeBaseIds,
        IReadOnlyDictionary<Guid, KnowledgeBaseDetail> details,
        IReadOnlyList<DictionaryField> dictionary)
    {
        ArgumentNullException.ThrowIfNull(selectedKnowledgeBaseIds);
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(dictionary);

        var mapped = new HashSet<Guid>();
        var complete = true;
        foreach (var id in selectedKnowledgeBaseIds)
        {
            if (details.TryGetValue(id, out var detail) && detail.Mapping is { } mapping)
            {
                mapped.UnionWith(mapping.Fields.Select(f => f.DictionaryFieldId));
            }
            else
            {
                complete = false;
            }
        }

        return complete
            ? new FieldChoices([.. dictionary.Where(f => mapped.Contains(f.Id)).OrderBy(f => f.Name, StringComparer.Ordinal)], Complete: true)
            : new FieldChoices([.. dictionary.OrderBy(f => f.Name, StringComparer.Ordinal)], Complete: false);
    }

    /// <summary>The parameters the model will see for these exposed fields, as the server generates them (D-11): a local preview, the saved definition is the real one.</summary>
    public static IReadOnlyList<string> ParameterNames(IEnumerable<DictionaryField> exposed, int knowledgeBaseCount)
    {
        ArgumentNullException.ThrowIfNull(exposed);

        List<string> names = ["query"];
        if (knowledgeBaseCount > 1)
        {
            names.Add("knowledge_base");
        }

        names.AddRange(exposed.OrderBy(f => f.Name, StringComparer.Ordinal).SelectMany(f => FilterInputs.ParametersOf(f.Name, f.Type).Select(p => p.Name)));
        return names;
    }

    /// <summary>The size of a definition in words.</summary>
    public static string SizeText(int estimatedTokens) =>
        estimatedTokens >= 1_000
            ? $"about {(estimatedTokens / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture)}k tokens of the context"
            : $"about {estimatedTokens} tokens of the context";

    /// <summary>The input schema as indented JSON, for the preview.</summary>
    public static string FormatSchema(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Undefined ? "{}" : JsonSerializer.Serialize(schema, Indented);

    public static string PolicyLabel(string policy) => policy == ToolPolicies.Auto ? "runs without asking" : "asks before running";
}

/// <summary>The rag tool being created or edited: what the admin typed, and its conversion into the request.</summary>
public sealed partial class RagToolForm
{
    private readonly HashSet<Guid> _knownCandidates = [];

    /// <summary>The tool being edited; <see langword="null"/> for a new one.</summary>
    public Guid? Id { get; init; }

    public string OriginalName { get; init; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Policy { get; set; } = ToolPolicies.Auto;

    public List<Guid> KnowledgeBaseIds { get; } = [];

    public HashSet<Guid> ExposedFieldIds { get; } = [];

    /// <summary>Fields with a fixed filter, in the order they were added.</summary>
    public List<Guid> FixedFilterFieldIds { get; } = [];

    /// <summary>What was typed for each filter parameter (<c>year_min</c>...).</summary>
    public Dictionary<string, string> FilterValues { get; } = [];

    /// <summary>Stored filter parameters this editor cannot show (their field is gone): saving drops them.</summary>
    public List<string> UnshownFilters { get; } = [];

    public int TopK { get; set; } = RagToolLimits.DefaultTopK;

    public string ScoreThreshold { get; set; } = string.Empty;

    public int MaxResultCharacters { get; set; } = RagToolLimits.DefaultMaxResultCharacters;

    public ScopeForm Scope { get; set; } = new();

    public bool IsNew => Id is null;

    public static RagToolForm From(RagToolDetail detail, IReadOnlyList<DictionaryField> dictionary)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(dictionary);

        var form = new RagToolForm
        {
            Id = detail.Id,
            OriginalName = detail.Name,
            Name = detail.Name,
            Description = detail.Description,
            Policy = detail.Policy,
            TopK = detail.TopK,
            ScoreThreshold = detail.ScoreThreshold?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            MaxResultCharacters = detail.MaxResultCharacters,
            Scope = ScopeForm.From(detail.Scope),
        };
        form.KnowledgeBaseIds.AddRange(detail.KnowledgeBaseIds);
        form.ExposedFieldIds.UnionWith(detail.ExposedFieldIds);

        var stored = FilterInputs.Show(detail.FixedFilters);
        foreach (var field in dictionary)
        {
            var parameters = FilterInputs.ParametersOf(field.Name, field.Type);
            if (parameters.Any(p => stored.ContainsKey(p.Name)))
            {
                form.FixedFilterFieldIds.Add(field.Id);
                foreach (var parameter in parameters.Where(p => stored.ContainsKey(p.Name)))
                {
                    form.FilterValues[parameter.Name] = stored[parameter.Name];
                }
            }
        }

        var shown = form.FixedFilterFieldIds
            .SelectMany(id => FilterInputs.ParametersOf(dictionary.First(f => f.Id == id).Name, dictionary.First(f => f.Id == id).Type))
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        form.UnshownFilters.AddRange(stored.Keys.Where(k => !shown.Contains(k)));
        return form;
    }

    /// <summary>
    /// Brings exposed and fixed-filter fields in line with the fields the selected knowledge bases map: the ones no longer offered
    /// go, and the ones newly offered are exposed (the default of D-11) while there is room.
    /// </summary>
    public void ApplyCandidates(IReadOnlyList<DictionaryField> candidates, bool exposeNew)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var offered = candidates.Select(f => f.Id).ToHashSet();
        ExposedFieldIds.RemoveWhere(id => !offered.Contains(id));
        foreach (var removed in FixedFilterFieldIds.Where(id => !offered.Contains(id)).ToList())
        {
            RemoveFixedFilter(removed, candidates);
        }

        if (exposeNew)
        {
            foreach (var field in candidates.Where(f => !_knownCandidates.Contains(f.Id)))
            {
                if (ExposedFieldIds.Count < RagToolLimits.MaxExposedFields)
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
            foreach (var parameter in FilterInputs.ParametersOf(field.Name, field.Type))
            {
                FilterValues.Remove(parameter.Name);
            }
        }
    }

    /// <summary>Everything that blocks saving, with the same limits the server applies.</summary>
    public IReadOnlyList<string> Problems(IReadOnlyList<DictionaryField> dictionary)
    {
        var problems = new List<string>();
        if (!ValidName().IsMatch(Name.Trim()))
        {
            problems.Add($"The name is 1 to {RagToolLimits.MaxNameLength} characters: lower-case letters, digits, \"-\" and \"_\", starting with a letter or digit. The model sees it as rag__name.");
        }

        if (Description.Trim().Length == 0 || Description.Length > RagToolLimits.MaxDescriptionLength)
        {
            problems.Add($"The description is required, up to {RagToolLimits.MaxDescriptionLength} characters: the model reads it to decide when to search.");
        }

        if (KnowledgeBaseIds.Count == 0)
        {
            problems.Add("Choose at least one knowledge base.");
        }
        else if (KnowledgeBaseIds.Count > RagToolLimits.MaxKnowledgeBases)
        {
            problems.Add($"A rag tool can search up to {RagToolLimits.MaxKnowledgeBases} knowledge bases.");
        }

        if (ExposedFieldIds.Count > RagToolLimits.MaxExposedFields)
        {
            problems.Add($"A rag tool can expose up to {RagToolLimits.MaxExposedFields} fields to the model.");
        }

        if (TopK is < 1 or > RagToolLimits.MaxTopK)
        {
            problems.Add($"The number of results is between 1 and {RagToolLimits.MaxTopK}.");
        }

        if (MaxResultCharacters is < RagToolLimits.MinMaxResultCharacters or > RagToolLimits.MaxMaxResultCharacters)
        {
            problems.Add($"The result size is between {RagToolLimits.MinMaxResultCharacters} and {RagToolLimits.MaxMaxResultCharacters} characters.");
        }

        if (ScoreThreshold.Trim().Length > 0 && (ParseThreshold() is not { } threshold || !double.IsFinite(threshold)))
        {
            problems.Add("The score threshold is a number, or empty for none.");
        }

        if (Scope.Build().Error is { } scope)
        {
            problems.Add(scope);
        }

        var (_, filterError) = ReadFixedFilters(dictionary);
        if (filterError is not null)
        {
            problems.Add(filterError);
        }

        return problems;
    }

    /// <summary>The request to send. Call it when <see cref="Problems"/> is empty.</summary>
    public RagToolRequest ToRequest(IReadOnlyList<DictionaryField> dictionary)
    {
        var (filters, _) = ReadFixedFilters(dictionary);
        return new RagToolRequest(
            Name.Trim(),
            Description.Trim(),
            [.. KnowledgeBaseIds],
            [.. ExposedFieldIds],
            filters is { Count: > 0 } ? filters : null,
            TopK,
            ParseThreshold(),
            MaxResultCharacters,
            Scope.Build().Scope!,
            Policy);
    }

    private double? ParseThreshold() =>
        double.TryParse(ScoreThreshold, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    // A fixed filter with no value would restrict nothing, and the server refuses it: it must be filled or removed.
    private (Dictionary<string, object>? Filters, string? Error) ReadFixedFilters(IReadOnlyList<DictionaryField> dictionary)
    {
        var all = new Dictionary<string, object>();
        foreach (var id in FixedFilterFieldIds)
        {
            if (dictionary.FirstOrDefault(f => f.Id == id) is not { } field)
            {
                continue;
            }

            var (filters, error) = FilterInputs.Read(FilterValues, FilterInputs.ParametersOf(field.Name, field.Type));
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

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]{0,39}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ValidName();
}
