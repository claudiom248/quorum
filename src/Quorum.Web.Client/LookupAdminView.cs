using Quorum.Web.Client.Api;

namespace Quorum.Web.Client;

/// <summary>What the Lookups page shows about a lookup, kept apart from the markup so it can be tested alone (spec 007 D-10, D-16).</summary>
public static class LookupAdminView
{
    public static string StatusLabel(LookupSliceInfo slice)
    {
        ArgumentNullException.ThrowIfNull(slice);

        if (slice.Running)
        {
            return "Computing";
        }

        return slice.Status switch
        {
            LookupStatuses.Ready when slice.Stale => "Out of date",
            LookupStatuses.Ready => "Ready",
            LookupStatuses.Failed => "Failed",
            _ => "Waiting for the first computation",
        };
    }

    /// <summary>A slice is out of date or failed: the last good list, if any, is still what models read.</summary>
    public static bool NeedsAttention(LookupSliceInfo slice)
    {
        ArgumentNullException.ThrowIfNull(slice);
        return !slice.Running && (slice.Stale || slice.Status == LookupStatuses.Failed);
    }

    public static string UnitLabel(string? unit) => unit switch
    {
        "documents" => "documents",
        "chunks" => "chunks",
        _ => "no counts",
    };

    public static string KindLabel(string kind) => kind switch
    {
        LookupKinds.Computed => "Values of a field",
        LookupKinds.Entity => "Entities",
        LookupKinds.Manual => "Typed by hand",
        _ => kind,
    };

    /// <summary>Which part of the knowledge base a slice covers, in words; the whole of it when the filters are empty.</summary>
    public static string SliceLabel(LookupSliceInfo slice)
    {
        ArgumentNullException.ThrowIfNull(slice);

        if (slice.Filters.ValueKind != System.Text.Json.JsonValueKind.Object || !slice.Filters.EnumerateObject().Any())
        {
            return "Whole knowledge base";
        }

        return "Filtered: " + string.Join(", ", slice.Filters.EnumerateObject().Select(p => $"{p.Name} = {p.Value.ToString()}"));
    }

    public static string When(DateTimeOffset? at) => at is { } value ? value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "never";

    /// <summary>The count of values of a slice, with a mark when it stopped at the ceiling.</summary>
    public static string ValueCountLabel(LookupSliceInfo slice)
    {
        ArgumentNullException.ThrowIfNull(slice);
        return slice.Truncated ? $"{slice.ValueCount} values (cut at the ceiling)" : $"{slice.ValueCount} values";
    }

    /// <summary>The attributes of an entity as <c>name: value</c> pairs, in the order stored.</summary>
    public static string Attributes(LookupValueInfo value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Attributes is not { ValueKind: System.Text.Json.JsonValueKind.Object } attributes)
        {
            return string.Empty;
        }

        return string.Join(", ", attributes.EnumerateObject().Select(p => $"{p.Name}: {p.Value.ToString()}"));
    }

    public static string ScopeLabel(ManualLookupInfo lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);

        if (lookup.Scope == ScopeKinds.Global)
        {
            return "every tenant";
        }

        return lookup.ScopeTenantIds is { Count: > 0 } tenants ? $"this tenant and {tenants.Count} other(s)" : "this tenant";
    }
}
