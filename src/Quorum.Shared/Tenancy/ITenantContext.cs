namespace Quorum.Shared.Tenancy;

/// <summary>
/// The tenant of the current request. Every module's DbContext filters its data by <see cref="TenantId"/>.
/// </summary>
public interface ITenantContext
{
    /// <summary>
    /// The current tenant, or <see langword="null"/> when no tenant is resolved (e.g. anonymous or setup requests).
    /// </summary>
    Guid? TenantId { get; }
}
