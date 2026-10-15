using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Quorum.Modules.Tenancy.Contracts;
using Quorum.Modules.Tenancy.Domain;

namespace Quorum.Modules.Tenancy.Identity;

internal sealed class QuorumUserClaimsFactory(UserManager<QuorumUser> userManager, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<QuorumUser>(userManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(QuorumUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(QuorumClaimTypes.TenantId, user.TenantId.ToString()));
        identity.AddClaim(new Claim(QuorumClaimTypes.DisplayName, user.DisplayName));
        identity.AddClaim(new Claim(ClaimTypes.Role, user.Role));
        return identity;
    }
}
