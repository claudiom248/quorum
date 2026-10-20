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
    /// <summary>The knowledge bases the tenant can see now, by name, as of the start of the answer; filled by <see cref="RagToolResolver"/>.</summary>
    public IReadOnlyDictionary<string, Guid> KnowledgeBases { get; init; } = new Dictionary<string, Guid>();

    public static RagBinding From(RagTool row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new RagBinding([.. row.KnowledgeBaseIds], [.. row.ExposedFieldIds], [.. row.FixedFilterFieldIds], row.FixedFilters, row.TopK, row.ScoreThreshold, row.MaxResultCharacters);
    }
}
