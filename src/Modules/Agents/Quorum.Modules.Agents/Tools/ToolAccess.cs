using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.Agents.Tools;

/// <summary>Who is chatting, when they may use tools.</summary>
internal sealed record ToolCaller(Guid TenantId, Guid UserId);

/// <summary>
/// Decides whether the current request may be offered tools. Tools are for an identified user who holds <c>tools.use</c>
/// (D-7); anyone else chats with the same assistant as if it had no tools.
/// </summary>
internal sealed class ToolAccess(
    ITenantContext tenant,
    ICurrentUser user,
    IHttpContextAccessor httpContextAccessor,
    IAuthorizationService authorization)
{
    /// <returns>The caller, or <see langword="null"/> when no tools may be used in this request.</returns>
    public async Task<ToolCaller?> ResolveAsync()
    {
        if (tenant.TenantId is not { } tenantId
            || user.UserId is not { } userId
            || httpContextAccessor.HttpContext?.User is not { } principal)
        {
            return null;
        }

        var result = await authorization.AuthorizeAsync(principal, QuorumPolicies.ToolsUse);
        return result.Succeeded ? new ToolCaller(tenantId, userId) : null;
    }
}
