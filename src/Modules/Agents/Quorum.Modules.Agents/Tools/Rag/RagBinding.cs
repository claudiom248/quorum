using System.Text.Json;
using Quorum.Modules.Agents.Domain;

namespace Quorum.Modules.Agents.Tools.Rag;

/// <summary>The configuration of a rag tool as one answer uses it.</summary>
internal sealed record RagBinding(
    IReadOnlyList<Guid> KnowledgeBaseIds,
    IReadOnlyList<Guid> ExposedFieldIds,
    IReadOnlyList<Guid> FixedFilterFieldIds,
    string FixedFilters,
    int TopK,
    double? ScoreThreshold,
    int MaxResultCharacters)
{
    /// <summary>The alias given to an exposed field, by field id; a field without one is known by its name (spec 003 A1-3).</summary>
    public IReadOnlyDictionary<Guid, string> ExposedAliases { get; init; } = new Dictionary<Guid, string>();

    /// <summary>The knowledge bases the tenant can see now, by name, as of the start of the answer; filled by <see cref="RagToolResolver"/>.</summary>
    public IReadOnlyDictionary<string, Guid> KnowledgeBases { get; init; } = new Dictionary<string, Guid>();

    /// <summary>The filter parameters the model was offered, by name, with the field each stands for; filled by <see cref="RagToolResolver"/>.</summary>
    public IReadOnlyDictionary<string, Guid> FilterFields { get; init; } = new Dictionary<string, Guid>();

    public static RagBinding From(RagTool row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new RagBinding([.. row.KnowledgeBaseIds], [.. row.ExposedFieldIds], [.. row.FixedFilterFieldIds], row.FixedFilters, row.TopK, row.ScoreThreshold, row.MaxResultCharacters)
        {
            ExposedAliases = ReadAliases(row.ExposedAliases),
        };
    }

    private static Dictionary<Guid, string> ReadAliases(string json) =>
        JsonSerializer.Deserialize<Dictionary<Guid, string>>(json) ?? [];
}
