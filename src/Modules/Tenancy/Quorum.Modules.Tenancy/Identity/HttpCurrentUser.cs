using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.Tenancy.Identity;

internal sealed class HttpCurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    public Guid? UserId =>
        Guid.TryParse(httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            ? userId
            : null;
}
