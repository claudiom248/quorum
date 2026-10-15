namespace Quorum.Modules.Tenancy.Contracts;

/// <summary>Claims Quorum adds to the authenticated user.</summary>
public static class QuorumClaimTypes
{
    public const string TenantId = "quorum:tenant_id";

    public const string DisplayName = "quorum:display_name";
}
