using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Quorum.Modules.Tenancy.Contracts;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.Tenancy.Identity;

internal sealed class HttpTenantContext(IHttpContextAccessor httpContextAccessor) : ITenantContext
{
    public Guid? TenantId =>
        Guid.TryParse(httpContextAccessor.HttpContext?.User.FindFirstValue(QuorumClaimTypes.TenantId), out var tenantId)
            ? tenantId
            : null;
}
