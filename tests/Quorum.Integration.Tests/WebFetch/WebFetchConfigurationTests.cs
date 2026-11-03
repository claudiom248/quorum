using System.Net;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.WebSearch;

namespace Quorum.Integration.Tests.WebFetch;

/// <summary>
/// The fetch defaults and web properties (spec 006, AC-1, AC-13, AC-14). They live in the platform's one web configuration row:
/// reading it before anyone saved one answers with the defaults, and every PUT carries the whole row so the other fields keep their value.
/// </summary>
public sealed class WebFetchConfigurationTests(PostgresFixture postgres, SharedQuorumApp host) : IClassFixture<SharedQuorumApp>, IAsyncLifetime
{
    public ValueTask InitializeAsync() => new(host.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-13
    public async Task AdminCanGetTheWebFetchDefaults()
    {
        var admin = host.Admin;

        var config = await WebFetchApi.GetConfigurationAsync(admin);

        // The defaults the spec states: 30 seconds per URL, 5 URLs per call, 4000 tokens per page, 20000 total.
        Assert.Equal(30, config.FetchTimeoutSeconds);
        Assert.Equal(5, config.FetchMaxUrlsPerInvocation);
        Assert.Equal(4000, config.FetchTokenBudgetPerPage);
        Assert.Equal(20000, config.FetchTotalTokenBudget);
    }

    [Fact] // AC-13
    public async Task AdminCanUpdateTheWebFetchDefaults()
    {
        var admin = host.Admin;

        var request = WebSearchApi.DefaultConfiguration with
        {
            FetchTimeoutSeconds = 45,
            FetchMaxUrlsPerInvocation = 8,
            FetchTokenBudgetPerPage = 6000,
            FetchTotalTokenBudget = 30000,
        };
        using var response = await WebFetchApi.UpdateConfigurationAsync(admin, request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var config = await WebFetchApi.GetConfigurationAsync(admin);
        Assert.Equal(45, config.FetchTimeoutSeconds);
        Assert.Equal(8, config.FetchMaxUrlsPerInvocation);
        Assert.Equal(6000, config.FetchTokenBudgetPerPage);
        Assert.Equal(30000, config.FetchTotalTokenBudget);
    }

    [Fact] // AC-13
    public async Task AdminCannotSetAFetchTimeoutBelowTheMinimum()
    {
        var admin = host.Admin;

        var request = WebSearchApi.DefaultConfiguration with { FetchTimeoutSeconds = 2 };
        using var response = await WebFetchApi.UpdateConfigurationAsync(admin, request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact] // AC-13
    public async Task AdminCannotSetAFetchTimeoutAboveTheMaximum()
    {
        var admin = host.Admin;

        var request = WebSearchApi.DefaultConfiguration with { FetchTimeoutSeconds = 200 };
        using var response = await WebFetchApi.UpdateConfigurationAsync(admin, request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact] // AC-13
    public async Task AdminCannotSetInvalidMaxUrlsPerInvocation()
    {
        var admin = host.Admin;

        var request = WebSearchApi.DefaultConfiguration with { FetchMaxUrlsPerInvocation = 20 };
        using var response = await WebFetchApi.UpdateConfigurationAsync(admin, request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact] // AC-13
    public async Task AdminCannotSetInvalidTokenBudgetPerPage()
    {
        var admin = host.Admin;

        var request = WebSearchApi.DefaultConfiguration with { FetchTokenBudgetPerPage = 50 };
        using var response = await WebFetchApi.UpdateConfigurationAsync(admin, request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact] // AC-13
    public async Task AdminCannotSetInvalidTotalTokenBudget()
    {
        var admin = host.Admin;

        var request = WebSearchApi.DefaultConfiguration with { FetchTotalTokenBudget = 200000 };
        using var response = await WebFetchApi.UpdateConfigurationAsync(admin, request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact] // AC-14
    public async Task AdminCanGetTheWebProperties()
    {
        var admin = host.Admin;

        var config = await WebFetchApi.GetConfigurationAsync(admin);

        Assert.Equal("Quorum/1.0", config.UserAgent);
        Assert.Equal(1000, config.FetchDelayMs);
        Assert.True(config.RespectRobotsTxt);
        Assert.True(config.CacheEnabled);
        Assert.Equal(10, config.CacheTtlMinutes);
    }

    [Fact] // AC-14
    public async Task AdminCanUpdateTheWebProperties()
    {
        var admin = host.Admin;

        var request = WebSearchApi.DefaultConfiguration with
        {
            UserAgent = "AcmeBot/2.1",
            FetchDelayMs = 500,
            RespectRobotsTxt = false,
            CacheEnabled = false,
            CacheTtlMinutes = 30,
        };
        using var response = await WebFetchApi.UpdateConfigurationAsync(admin, request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var config = await WebFetchApi.GetConfigurationAsync(admin);
        Assert.Equal("AcmeBot/2.1", config.UserAgent);
        Assert.Equal(500, config.FetchDelayMs);
        Assert.False(config.RespectRobotsTxt);
        Assert.False(config.CacheEnabled);
        Assert.Equal(30, config.CacheTtlMinutes);
    }

    [Fact] // AC-14
    public async Task AdminCannotSetAnOverlyLongUserAgent()
    {
        var admin = host.Admin;

        var request = WebSearchApi.DefaultConfiguration with { UserAgent = new string('x', WebFetchApi.MaxUserAgentLength + 1) };
        using var response = await WebFetchApi.UpdateConfigurationAsync(admin, request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact] // AC-14
    public async Task AdminCannotSetAnInvalidCacheTtl()
    {
        var admin = host.Admin;

        var request = WebSearchApi.DefaultConfiguration with { CacheTtlMinutes = 120 };
        using var response = await WebFetchApi.UpdateConfigurationAsync(admin, request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact] // AC-14
    public async Task AdminCannotSetANegativeDelay()
    {
        var admin = host.Admin;

        var request = WebSearchApi.DefaultConfiguration with { FetchDelayMs = -1 };
        using var response = await WebFetchApi.UpdateConfigurationAsync(admin, request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact] // AC-1, AC-13, AC-14
    public async Task TheUnifiedRowCarriesSearchAndFetchTogether()
    {
        var admin = host.Admin;

        var request = WebSearchApi.DefaultConfiguration with
        {
            SearchUrl = "https://searxng.example.com",
            FetchTimeoutSeconds = 60,
            UserAgent = "Custom/1.0",
        };
        using var response = await WebFetchApi.UpdateConfigurationAsync(admin, request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var config = await WebFetchApi.GetConfigurationAsync(admin);
        // The same row answers every part, so the search fields and the fetch fields and the properties stay together.
        Assert.Equal("https://searxng.example.com", config.SearchUrl);
        Assert.Equal(60, config.FetchTimeoutSeconds);
        Assert.Equal("Custom/1.0", config.UserAgent);
    }

    [Fact] // AC-13, AC-14
    public async Task NonAdminGetsForbidden()
    {
        var app = host.App;
        using var member = await app.CreateMemberClientAsync();

        using var read = await member.GetAsync(WebSearchApi.WebSettings, Ct);
        using var write = await WebSearchApi.UpdateConfigurationAsync(member, WebSearchApi.DefaultConfiguration);

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact] // AC-13, AC-14
    public async Task AnonymousGetsUnauthorized()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = app.CreateUiClient();

        using var response = await client.GetAsync(WebSearchApi.WebSettings, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
