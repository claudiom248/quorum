using System.Text.Json;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.KnowledgeBases.Contracts;

namespace Quorum.Modules.Agents.Tools.Lookup;

/// <summary>The configuration of a lookup tool as one answer uses it.</summary>
internal sealed record LookupBinding(
    IReadOnlyList<Guid> LookupIds,
    IReadOnlyList<Guid> KnowledgeBaseIds,
    IReadOnlyList<Guid> ExposedFieldIds,
    IReadOnlyList<Guid> FixedFilterFieldIds,
    string FixedFilters)
{
    /// <summary>The alias given to a lookup, by lookup id; a lookup without one is known by its own name.</summary>
    public IReadOnlyDictionary<Guid, string> LookupAliases { get; init; } = new Dictionary<Guid, string>();

    /// <summary>The alias given to an exposed field, by field id; a field without one is known by its name (spec 003 A1-3).</summary>
    public IReadOnlyDictionary<Guid, string> ExposedAliases { get; init; } = new Dictionary<Guid, string>();

    /// <summary>The lookups the tenant can see now, by the name the model was offered, as of the start of the answer; filled by <see cref="LookupToolResolver"/>.</summary>
    public IReadOnlyDictionary<string, Guid> Lookups { get; init; } = new Dictionary<string, Guid>();

    /// <summary>The knowledge bases the live operations may query that the tenant can see now, by the name the model was offered; filled by <see cref="LookupToolResolver"/>.</summary>
    public IReadOnlyDictionary<string, Guid> KnowledgeBases { get; init; } = new Dictionary<string, Guid>();

    /// <summary>The filter parameters the model was offered, by name, with the field each stands for; filled by <see cref="LookupToolResolver"/>.</summary>
    public IReadOnlyDictionary<string, Guid> FilterFields { get; init; } = new Dictionary<string, Guid>();

    /// <summary>What this function of the tool does: <c>values</c>, <c>documents</c> or <c>count</c>; filled by <see cref="LookupToolResolver"/>, which gives each its own function.</summary>
    public string Operation { get; init; } = LookupToolDefinition.ValuesOperation;

    /// <summary>The attributes each entity list can be ordered by, by the name the model was offered the list under; filled by <see cref="LookupToolResolver"/>.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> SortableAttributes { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>The fields each list can be filtered by, by the name the model was offered the list under; filled by <see cref="LookupToolResolver"/>.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ListFilters { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>The list that holds the exact values of a filter parameter, by parameter name: the list of the field it filters; filled by <see cref="LookupToolResolver"/>.</summary>
    public IReadOnlyDictionary<string, string> FilterLists { get; init; } = new Dictionary<string, string>();

    public static LookupBinding From(LookupTool row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new LookupBinding([.. row.LookupIds], [.. row.KnowledgeBaseIds], [.. row.ExposedFieldIds], [.. row.FixedFilterFieldIds], row.FixedFilters)
        {
            LookupAliases = JsonSerializer.Deserialize<Dictionary<Guid, string>>(row.LookupAliases) ?? [],
            ExposedAliases = JsonSerializer.Deserialize<Dictionary<Guid, string>>(row.ExposedAliases) ?? [],
        };
    }

    /// <summary>The fixed filters in the shape the lookups read them: they make the slice the tool uses (spec 007 D-6).</summary>
    public LookupSliceFilters SliceFilters()
    {
        using var filters = JsonDocument.Parse(FixedFilters);
        var parsed = filters.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        return parsed.Count == 0
            ? LookupSliceFilters.None
            : new LookupSliceFilters(parsed, ExposedAliases.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal), FixedFilterFieldIds);
    }
}
