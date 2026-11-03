using System.Net;
using System.Net.Http.Json;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.Auth;

/// <summary>What a person chose for themselves, kept with their account: how the tool calls of an answer are drawn.</summary>
public sealed class PreferencesTests(SharedQuorumApp host) : IClassFixture<SharedQuorumApp>, IAsyncLifetime
{
    public ValueTask InitializeAsync() => new(host.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static readonly Uri Preferences = new("/api/me/preferences", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string> LedgerStyleAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<PreferencesBody>(Preferences, Ct))!.LedgerStyle;

    [Fact]
    public async Task ThePreferencesStartAtTheClassicLedger() =>
        Assert.Equal("classic", await LedgerStyleAsync(host.Admin));

    [Theory]
    [InlineData("cards")]
    [InlineData("Timeline")]
    [InlineData("bars")]
    public async Task AStyleIsKeptWithTheAccountAndFollowsTheNextSignIn(string style)
    {
        using var saved = await host.Admin.PutAsJsonAsync(Preferences, new { ledgerStyle = style }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        Assert.Equal(style.ToLowerInvariant(), await LedgerStyleAsync(host.Admin));

        // Another browser: the same account signs in again and finds the same choice.
        using var other = host.App.CreateUiClient();
        using var login = await other.PostAsJsonAsync(
            new Uri("/api/auth/login", UriKind.Relative),
            new { email = QuorumApp.AdminEmail, password = QuorumApp.AdminPassword },
            Ct);
        login.EnsureSuccessStatusCode();
        Assert.Equal(style.ToLowerInvariant(), await LedgerStyleAsync(other));
    }

    [Fact]
    public async Task TheClassicStyleGivesTheDefaultBackAndAnOmittedPreferenceIsKept()
    {
        using var cards = await host.Admin.PutAsJsonAsync(Preferences, new { ledgerStyle = "cards" }, Ct);
        using var nothing = await host.Admin.PutAsJsonAsync(Preferences, new { }, Ct);
        var kept = await LedgerStyleAsync(host.Admin);
        using var classic = await host.Admin.PutAsJsonAsync(Preferences, new { ledgerStyle = "classic" }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, nothing.StatusCode);
        Assert.Equal("cards", kept);
        Assert.Equal("classic", await LedgerStyleAsync(host.Admin));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("fancy")]
    public async Task AnUnknownStyleIsRefusedAndTheChoiceStaysAsItWas(string style)
    {
        using var cards = await host.Admin.PutAsJsonAsync(Preferences, new { ledgerStyle = "cards" }, Ct);

        using var response = await host.Admin.PutAsJsonAsync(Preferences, new { ledgerStyle = style }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("cards", await LedgerStyleAsync(host.Admin));
    }

    [Fact]
    public async Task EachPersonHasTheirOwnChoice()
    {
        using var member = await host.App.CreateMemberClientAsync();
        using var admin = await host.Admin.PutAsJsonAsync(Preferences, new { ledgerStyle = "bars" }, Ct);

        Assert.Equal("classic", await LedgerStyleAsync(member));
        Assert.Equal("bars", await LedgerStyleAsync(host.Admin));
    }

    [Fact]
    public async Task WithoutSigningInThePreferencesAreRefused()
    {
        using var anonymous = host.App.CreateUiClient();

        using var read = await anonymous.GetAsync(Preferences, Ct);
        using var write = await anonymous.PutAsJsonAsync(Preferences, new { ledgerStyle = "cards" }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);
    }

    private sealed record PreferencesBody(string LedgerStyle);
}
