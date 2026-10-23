using System.Text.Json;

namespace Quorum.Web.Client.Api;

// Client-side copies of the lookup contract (spec 007): lists of values that models read through a lookup tool.

public static class LookupKinds
{
    public const string Computed = "Computed";
    public const string Entity = "Entity";
    public const string Manual = "Manual";
}

public static class LookupStatuses
{
    public const string Pending = "Pending";
    public const string Ready = "Ready";
    public const string Failed = "Failed";
}

/// <summary>Limits of a lookup, as the server enforces them.</summary>
public static class LookupLimits
{
    public const int MaxManualValues = 5_000;
    public const int MaxValueLength = 200;
    public const int MinRefreshHours = 1;
    public const int MaxRefreshHours = 168;
    public const int DefaultRefreshHours = 24;
    public const int MaxEntityAttributes = 10;
    public const int MaxNameLength = 40;
    public const int MaxDescriptionLength = 1_000;
}

/// <param name="Hash">Names the slice in the values call.</param>
/// <param name="Filters">The fixed filters the slice was computed under; <c>{}</c> for the whole knowledge base.</param>
/// <param name="Running">A computation of the slice is in progress.</param>
public sealed record LookupSliceInfo(
    string Hash,
    JsonElement Filters,
    string Status,
    bool Stale,
    int ValueCount,
    bool Truncated,
    DateTimeOffset? ComputedAt,
    DateTimeOffset? NextRefreshAt,
    string? Error,
    bool Running);

/// <param name="Field">The dictionary field a computed lookup lists.</param>
/// <param name="Unit"><c>documents</c> or <c>chunks</c>; <see langword="null"/> for a lookup with no counts.</param>
public sealed record KnowledgeBaseLookupInfo(Guid Id, string Name, string Kind, string? Field, string? Unit, IReadOnlyList<LookupSliceInfo> Slices);

public sealed record LookupValueInfo(string Value, string? Label, IReadOnlyList<string>? Synonyms, int? Count, JsonElement? Attributes);

public sealed record LookupValuesPage(string? Unit, DateTimeOffset? AsOf, bool Stale, bool Truncated, IReadOnlyList<LookupValueInfo> Values, string? Cursor, bool CursorRestarted);

/// <param name="Shared"><see langword="true"/> for a lookup of another tenant: it can be read but not changed.</param>
public sealed record ManualLookupInfo(Guid Id, string Name, string? Description, ToolScope Scope, int ValueCount, bool Shared);

public sealed record ManualValueRequest(string Value, string? Label, IReadOnlyList<string>? Synonyms);

/// <param name="Scope"><c>Global</c> or <c>Tenant</c>.</param>
public sealed record ManualLookupRequest(string Name, string? Description, string Scope, IReadOnlyList<Guid>? ScopeTenantIds, IReadOnlyList<ManualValueRequest> Values);

public sealed record LookupKnowledgeBaseRef(Guid Id, string Name);

/// <summary>A row of the list of every lookup: <c>Kind</c> is <c>Manual</c> or <c>Derived</c> (computed from a knowledge base).</summary>
/// <param name="KnowledgeBase">Only for a derived lookup.</param>
/// <param name="Field">The dictionary field a computed lookup lists.</param>
/// <param name="Shared"><see langword="true"/> for a manual lookup of another tenant: it can be read, not changed.</param>
public sealed record LookupListItem(
    Guid Id,
    string Name,
    string Kind,
    LookupKnowledgeBaseRef? KnowledgeBase,
    string? Field,
    string? Unit,
    ToolScope Scope,
    int ValueCount,
    bool Shared);

public static class LookupListKinds
{
    public const string Manual = "Manual";
    public const string Derived = "Derived";
}
