using System.Net;
using System.Net.Http.Json;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.Auth;

public sealed class AuthTests(SharedQuorumApp host) : IClassFixture<SharedQuorumApp>, IAsyncLifetime
{
    public ValueTask InitializeAsync() => new(host.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static readonly Uri Login = new("/api/auth/login", UriKind.Relative);
    private static readonly Uri Logout = new("/api/auth/logout", UriKind.Relative);
    private static readonly Uri Me = new("/api/auth/me", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-10
    public async Task AdminSignsInWithAnHttpOnlyCookieAndCanReadItsProfile()
    {
        var app = host.App;
        using var client = app.CreateUiClient();

        using var login = await client.PostAsJsonAsync(Login, new { email = QuorumApp.AdminEmail, password = QuorumApp.AdminPassword }, Ct);
        var me = await client.GetFromJsonAsync<MeBody>(Me, Ct);

        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        Assert.Contains(login.Headers.GetValues("Set-Cookie"), c => c.StartsWith("quorum.auth=", StringComparison.Ordinal) && c.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new MeBody(QuorumApp.AdminEmail, "Acme Admin", "Admin"), me);
    }

    [Fact]
    public async Task WrongPasswordIsRejected()
    {
        var app = host.App;
        using var client = app.CreateUiClient();

        using var login = await client.PostAsJsonAsync(Login, new { email = QuorumApp.AdminEmail, password = "not the right one" }, Ct);
        using var unknownUser = await client.PostAsJsonAsync(Login, new { email = "nobody@acme.test", password = QuorumApp.AdminPassword }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);
    }

    [Fact] // AC-11
    public async Task FiveFailedAttemptsLockTheAccountEvenForTheRightPassword()
    {
        var app = host.App;
        using var client = app.CreateUiClient();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var failed = await client.PostAsJsonAsync(Login, new { email = QuorumApp.AdminEmail, password = "not the right one" }, Ct);
        }

        using var locked = await client.PostAsJsonAsync(Login, new { email = QuorumApp.AdminEmail, password = QuorumApp.AdminPassword }, Ct);

        Assert.Equal((HttpStatusCode)423, locked.StatusCode);
    }

    [Theory] // AC-12
    [InlineData("GET", "/api/auth/me")]
    [InlineData("GET", "/api/conversations")]
    [InlineData("POST", "/api/conversations")]
    [InlineData("GET", "/api/settings/assistant")]
    public async Task UnauthenticatedRequestsGet401(string method, string url)
    {
        var app = host.App;
        using var anonymous = app.CreateUiClient();

        using var response = await anonymous.SendAsync(new HttpRequestMessage(new HttpMethod(method), url), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task LogoutEndsTheSession()
    {
        var client = host.Admin;

        using var logout = await client.PostAsync(Logout, content: null, Ct);
        using var me = await client.GetAsync(Me, Ct);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task StateChangingCallsWithTheCookieButWithoutTheAntiForgeryHeaderAreRefused()
    {
        var client = host.Admin;
        client.DefaultRequestHeaders.Remove("X-Quorum-Request");

        using var create = await client.PostAsync(new Uri("/api/conversations", UriKind.Relative), content: null, Ct);
        using var read = await client.GetAsync(Me, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    private sealed record MeBody(string Email, string DisplayName, string Role);
}
