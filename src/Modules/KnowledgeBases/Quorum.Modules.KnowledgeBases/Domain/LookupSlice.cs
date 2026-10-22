namespace Quorum.Modules.KnowledgeBases.Domain;

internal enum LookupStatus
{
    /// <summary>No generation has been computed yet.</summary>
    Pending,

    Ready,

    /// <summary>The last computation failed and there is no earlier generation to serve.</summary>
    Failed,
}

/// <summary>
/// The list of one lookup for one set of fixed filters (spec 007 D-6). A tool without fixed filters uses the slice of the whole
/// knowledge base; tools with the same filters share one slice, found by <see cref="FilterHash"/>.
/// </summary>
internal sealed class LookupSlice
{
    public const int FilterHashLength = 64;

    /// <summary>What a computation says about a failure also reaches the model, so the stored problem stays short.</summary>
    public const int MaxErrorLength = 1000;

    private LookupSlice()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid LookupId { get; private set; }

    /// <summary>The hash of <see cref="Filters"/>: see <c>LookupSliceKey</c>.</summary>
    public string FilterHash { get; private set; } = null!;

    /// <summary>The canonical filters of the slice by dictionary field id, as JSON.</summary>
    public string Filters { get; private set; } = null!;

    public LookupStatus Status { get; private set; }

    /// <summary>The last computation failed, so the generation served is older than it should be.</summary>
    public bool Stale { get; private set; }

    /// <summary>The generation readers see; 0 until the first computation ends.</summary>
    public int CurrentGeneration { get; private set; }

    public int ValueCount { get; private set; }

    /// <summary>Values longer than the limit, left out of the current generation.</summary>
    public int SkippedValueCount { get; private set; }

    /// <summary>The list was cut at the ceiling of values, or the scan stopped at its cap.</summary>
    public bool Truncated { get; private set; }

    public DateTimeOffset? ComputedAt { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset? NextRefreshAt { get; private set; }

    /// <summary>The tools that use the slice, by id: they live in another schema, so there is no foreign key. A slice no tool uses is removed.</summary>
    public List<Guid> ToolIds { get; private set; } = [];

    /// <summary>Makes a generation the one readers see: the status is Ready, the last error is cleared and the list is no longer stale.</summary>
    /// <param name="nextRefreshAt"><see langword="null"/> when nothing recomputes the slice, as with a manual lookup.</param>
    public void Publish(int generation, int valueCount, DateTimeOffset now, DateTimeOffset? nextRefreshAt, int skippedValueCount = 0, bool truncated = false)
    {
        CurrentGeneration = generation;
        ValueCount = valueCount;
        SkippedValueCount = skippedValueCount;
        Truncated = truncated;
        Status = LookupStatus.Ready;
        Stale = false;
        LastError = null;
        ComputedAt = now;
        NextRefreshAt = nextRefreshAt;
    }

    public static LookupSlice Create(Guid tenantId, Guid lookupId, string filterHash, string filters, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        TenantId = tenantId,
        LookupId = lookupId,
        FilterHash = filterHash,
        Filters = filters,
        Status = LookupStatus.Pending,
        NextRefreshAt = now,
    };
}
