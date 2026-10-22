namespace Quorum.Modules.KnowledgeBases.Features.Lookups;

/// <summary>The <c>Quorum:Lookups</c> section (spec 007 D-9, D-10). Read at startup: changing it needs a restart.</summary>
internal sealed class LookupOptions
{
    public const string SectionName = "Quorum:Lookups";

    public const int DefaultComputationTimeoutMinutes = 10;

    public const int DefaultMaxValuesPerLookup = 5000;

    public const int MaxValuesPerLookupLimit = 50_000;

    /// <summary>The time budget of one computation.</summary>
    public int ComputationTimeoutMinutes { get; set; } = DefaultComputationTimeoutMinutes;

    /// <summary>The ceiling of values one slice keeps; a longer list is cut and marked truncated.</summary>
    public int MaxValuesPerLookup { get; set; } = DefaultMaxValuesPerLookup;

    public TimeSpan ComputationTimeout => TimeSpan.FromMinutes(ComputationTimeoutMinutes);

    /// <summary>How long after it began a computation is still taken as running: its budget and a minute for the writes that follow.</summary>
    public TimeSpan Lease => ComputationTimeout + TimeSpan.FromMinutes(1);

    public static string? Validate(LookupOptions options)
    {
        if (options.ComputationTimeoutMinutes is < 1 or > 24 * 60)
        {
            return $"{SectionName}:ComputationTimeoutMinutes must be between 1 and {24 * 60}.";
        }

        return options.MaxValuesPerLookup is < 1 or > MaxValuesPerLookupLimit
            ? $"{SectionName}:MaxValuesPerLookup must be between 1 and {MaxValuesPerLookupLimit}."
            : null;
    }
}
