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
