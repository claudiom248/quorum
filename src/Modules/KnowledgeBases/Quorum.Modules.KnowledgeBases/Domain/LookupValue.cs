namespace Quorum.Modules.KnowledgeBases.Domain;

/// <summary>
/// One row of a lookup slice in one generation (spec 007 D-9, D-10). A computation writes a whole new generation and then makes it
/// the current one, so a reader that filters on <see cref="LookupSlice.CurrentGeneration"/> never sees a mix.
/// </summary>
internal sealed class LookupValue
{
    public const int MaxValueLength = 200;

    private LookupValue()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid SliceId { get; private set; }

    public int Generation { get; private set; }

    /// <summary>The rank in the generation: by count, then value (for entities, in the order of the sort that was asked).</summary>
    public int Position { get; private set; }

    public string Value { get; private set; } = null!;

    /// <summary>Only manual lookups.</summary>
    public string? Label { get; private set; }

    /// <summary>Only manual lookups.</summary>
    public List<string> Synonyms { get; private set; } = [];

    /// <summary><see langword="null"/> in a manual lookup.</summary>
    public int? DocumentCount { get; private set; }

    /// <summary>Only entity lookups: the chosen attributes as a JSON object.</summary>
    public string? Attributes { get; private set; }

    public static LookupValue Create(
        Guid tenantId,
        Guid sliceId,
        int generation,
        int position,
        string value,
        int? documentCount = null,
        string? label = null,
        IEnumerable<string>? synonyms = null,
        string? attributes = null) => new()
        {
            TenantId = tenantId,
            SliceId = sliceId,
            Generation = generation,
            Position = position,
            Value = value,
            DocumentCount = documentCount,
            Label = label,
            Synonyms = synonyms is null ? [] : [.. synonyms],
            Attributes = attributes,
        };
}
