using System.Net;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.Web;

/// <summary>
/// The stylesheet and scripts of the UI have no fingerprint in their name: a browser that keeps an old copy shows the new
/// pages without their styles. They must be revalidated on every load (a cheap 304 when nothing changed).
/// </summary>
public sealed class StaticAssetCachingTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/css/app.css")]
    [InlineData("/chat.js")]
    [InlineData("/index.html")]
    [InlineData("/_framework/blazor.webassembly.js")]
    public async Task UnfingerprintedAssetsAreRevalidatedOnEveryLoad(string path)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = app.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoCache, $"{path} should be served with Cache-Control: no-cache.");
    }

    [Fact]
    public async Task TheSinglePageFallbackIsRevalidatedToo()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = app.CreateClient();

        using var response = await client.GetAsync(new Uri("/c/01a14117-8443-7208-890d-b78925ebb291", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoCache);
    }
}
