using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.ServiceTokens;

/// <summary>The service token middleware in the Host (spec 011).</summary>
public sealed class ServiceTokenMiddlewareTests(SharedQuorumApp host) : IClassFixture<SharedQuorumApp>, IAsyncLifetime
{
    public ValueTask InitializeAsync() => new(host.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-11
    public async Task WithoutBearerHeaderCookieAuthPassesThrough()
    {
        var admin = host.Admin;

        using var response = await admin.GetAsync("/api/auth/me", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact] // AC-12
    public async Task InvalidBearerTokenReturns401WithoutFallback()
    {
        var app = host.App;
        using var anonymous = app.CreateClient();
        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "svc_invalidtokenthatdoesnotexist");

        using var response = await anonymous.GetAsync("/api/auth/me", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact] // AC-12
    public async Task InvalidBearerOnAnonymousRequestReturns401()
    {
        var app = host.App;
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "svc_totallyfake");

        using var response = await client.GetAsync("/api/knowledge-bases", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
