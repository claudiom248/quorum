using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Integration.Tests.Tools;
using Quorum.Modules.Tenancy.Contracts;

namespace Quorum.Integration.Tests.Fixtures;

/// <summary>A service scope that runs as the app's tenant, for tests that call a service directly instead of through a request.</summary>
internal static class TenantScope
{
    /// <summary>The tenant the setup created. Call after <see cref="QuorumApp.SetUpAsync"/>.</summary>
    public static Task<Guid> TenantIdAsync(QuorumApp app) =>
        ToolFixtures.ScalarAsync<Guid>(app, "SELECT \"TenantId\" FROM agents.assistants LIMIT 1");

    /// <summary>
    /// Synchronous on purpose: the accessor keeps the request in an async-local value, which an async method's assignment would
    /// not carry back to the caller.
    /// </summary>
    public static AsyncServiceScope Open(QuorumApp app, Guid tenantId)
    {
        ArgumentNullException.ThrowIfNull(app);

        var scope = app.Services.CreateAsyncScope();
        var identity = new ClaimsIdentity([new Claim(QuorumClaimTypes.TenantId, tenantId.ToString())], "test");
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        return scope;
    }
}
