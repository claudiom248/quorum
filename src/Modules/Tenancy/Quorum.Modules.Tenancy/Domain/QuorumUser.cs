using Microsoft.AspNetCore.Identity;

namespace Quorum.Modules.Tenancy.Domain;

internal sealed class QuorumUser : IdentityUser<Guid>
{
    public Guid TenantId { get; set; }

    public string DisplayName { get; set; } = null!;

    public string Role { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }
}

internal static class Roles
{
    public const string Admin = "Admin";
}
