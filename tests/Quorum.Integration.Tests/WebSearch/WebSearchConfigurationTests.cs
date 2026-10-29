using System.Net;
using System.Net.Http.Json;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.WebSearch;

public sealed class WebSearchConfigurationTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-9
    public async Task AdminCanGetTheWebSearchConfiguration()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        var config = await WebSearchApi.GetConfigurationAsync(admin);

        Assert.NotNull(config);
        Assert.Null(config.SearchUrl);
        Assert.Equal(90, config.SearchTimeoutSeconds);
    }

    [Fact] // AC-9
    public async Task AdminCanUpdateTheWebSearchConfiguration()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        var request = WebSearchApi.DefaultConfiguration with
        {
            SearchUrl = "https://searxng.example.com",
            SearchTimeoutSeconds = 60,
        };
        using var response = await WebSearchApi.UpdateConfigurationAsync(admin, request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var config = await WebSearchApi.GetConfigurationAsync(admin);
        Assert.Equal("https://searxng.example.com", config.SearchUrl);
        Assert.Equal(60, config.SearchTimeoutSeconds);
    }

    [Fact] // AC-9
    public async Task AdminCannotSetAnInvalidUrl()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        var request = WebSearchApi.DefaultConfiguration with { SearchUrl = "not-a-url" };
        using var response = await WebSearchApi.UpdateConfigurationAsync(admin, request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact] // AC-9
    public async Task AdminCannotSetATimeoutLessThanOneSecond()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        var request = WebSearchApi.DefaultConfiguration with
        {
            SearchUrl = "https://searxng.example.com",
            SearchTimeoutSeconds = 0,
        };
        using var response = await WebSearchApi.UpdateConfigurationAsync(admin, request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact] // AC-9
    public async Task NonAdminGetsForbidden()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        using var member = await app.CreateMemberClientAsync();

        using var response = await member.GetAsync(WebSearchApi.WebSearchSettings, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact] // AC-9
    public async Task AnonymousGetsUnauthorized()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = app.CreateUiClient();

        using var response = await client.GetAsync(WebSearchApi.WebSearchSettings, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
