namespace Quorum.Shared.Tenancy;

/// <summary>No tenant: used by design-time tools and background work that is not tied to a request.</summary>
public sealed class NoTenantContext : ITenantContext
{
    public static NoTenantContext Instance { get; } = new();

    public Guid? TenantId => null;
}
