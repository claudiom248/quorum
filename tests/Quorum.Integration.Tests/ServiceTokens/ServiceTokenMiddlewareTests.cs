using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.ServiceTokens;

/// <summary>The service token middleware in the Host (spec 011).</summary>
public sealed class ServiceTokenMiddlewareTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-11
    public async Task WithoutBearerHeaderCookieAuthPassesThrough()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        using var response = await admin.GetAsync("/api/auth/me", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact] // AC-12
    public async Task InvalidBearerTokenReturns401WithoutFallback()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var _ = await app.SetUpAsync();
        using var anonymous = app.CreateClient();
        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "svc_invalidtokenthatdoesnotexist");

        using var response = await anonymous.GetAsync("/api/auth/me", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact] // AC-12
    public async Task InvalidBearerOnAnonymousRequestReturns401()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var _ = await app.SetUpAsync();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "svc_totallyfake");

        using var response = await client.GetAsync("/api/knowledge-bases", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
