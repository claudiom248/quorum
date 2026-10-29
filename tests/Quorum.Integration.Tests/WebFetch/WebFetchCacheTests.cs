using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.Tools;
using Quorum.Integration.Tests.WebSearch;
using Quorum.Modules.Agents.Tools;
using Quorum.Modules.Agents.Tools.WebFetch;
using static Quorum.Integration.Tests.Fixtures.FakeModelServer;

namespace Quorum.Integration.Tests.WebFetch;

/// <summary>
/// The pages the platform remembers (spec 006, AC-8): a URL read a minute ago is not asked of the site again, the toggle in the
/// settings takes that memory away, and a remembered page is forgotten when its lifetime is over or the policy changes.
/// </summary>
public sealed class WebFetchCacheTests(PostgresFixture postgres)
{
    private const string ArticleUrl = "https://site.test/article";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FakeToolCall Call(params string[] urls) =>
        new(ToolNaming.WebFetch("read"), JsonSerializer.Serialize(new { urls }), "call_1");

    private static int PageRequestsOf(QuorumApp app, string url) =>
        app.WebFetch.Urls.Count(u => u.Equals(url, StringComparison.OrdinalIgnoreCase));

    private static string Page() =>
        $"<html><head><title>Report</title></head><body><article><p>{new string('s', 900)}</p></article></body></html>";

    /// <summary>An application with one web fetch tool activated on the default assistant, and a conversation to ask in.</summary>
    private async Task<(QuorumApp App, HttpClient Admin, Guid Chat)> StartAsync(
        WebSearchApi.WebConfigurationBody? web = null,
        Action<FakeWebFetchServer>? script = null)
    {
        var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        var admin = await app.SetUpAsync();
        using (var configured = await WebFetchApi.UpdateConfigurationAsync(
                   admin, web ?? WebSearchApi.DefaultConfiguration with { FetchDelayMs = 0 }))
        {
            configured.EnsureSuccessStatusCode();
        }

        await WebFetchApi.CreateOkAsync(admin, WebFetchApi.Body(name: "read"));
        await ToolFixtures.ActivateOnlyAsync(admin, "web_fetch__read");
        script?.Invoke(app.WebFetch);
        return (app, admin, await ChatApi.CreateConversationAsync(admin));
    }

    [Fact] // AC-8
    public async Task APageReadAMinuteAgoIsNotAskedOfTheSiteAgain()
    {
        var (app, admin, chat) = await StartAsync(script: s => s.ServeHtml(ArticleUrl, Page()));
        app.ModelServer.EnqueueRounds(
            ChatScript.CallTools(Call(ArticleUrl)),
            ChatScript.CallTools(new FakeToolCall(ToolNaming.WebFetch("read"), Call(ArticleUrl).Arguments, "call_2")),
            ChatScript.Reply("Done."));

        await ChatApi.SendAsync(admin, chat, "Read it, then read it again");

        // The site is asked once for the page; the second read comes from the platform's memory of it.
        Assert.Equal(1, PageRequestsOf(app, ArticleUrl));
    }

    [Fact] // AC-8
    public async Task WhenTheCacheIsTurnedOffEveryReadGoesToTheSite()
    {
        var (app, admin, chat) = await StartAsync(
            WebSearchApi.DefaultConfiguration with { FetchDelayMs = 0, CacheEnabled = false },
            script: s => s.ServeHtml(ArticleUrl, Page()));
        app.ModelServer.EnqueueRounds(
            ChatScript.CallTools(Call(ArticleUrl)),
            ChatScript.CallTools(new FakeToolCall(ToolNaming.WebFetch("read"), Call(ArticleUrl).Arguments, "call_2")),
            ChatScript.Reply("Done."));

        await ChatApi.SendAsync(admin, chat, "Read it, then read it again");

        Assert.Equal(2, PageRequestsOf(app, ArticleUrl));
    }

    [Fact] // AC-8
    public async Task ARememberedPageIsAskedAgainOnceItsLifetimeIsOver()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        await app.SetUpAsync();
        await using var scope = app.Services.CreateAsyncScope();
        var cache = scope.ServiceProvider.GetRequiredService<WebFetchCache>();
        var site = new CountedSite();

        await cache.GetOrCreateAsync(ArticleUrl, TimeSpan.FromMilliseconds(200), site.LoadAsync, Ct);
        await cache.GetOrCreateAsync(ArticleUrl, TimeSpan.FromMilliseconds(200), site.LoadAsync, Ct);
        Assert.Equal(1, site.Loads);

        await Task.Delay(400, Ct);
        await cache.GetOrCreateAsync(ArticleUrl, TimeSpan.FromMilliseconds(200), site.LoadAsync, Ct);
        Assert.Equal(2, site.Loads);
    }

    [Fact] // AC-14
    public async Task ChangingTheCachePolicyForgetsEveryPageThePlatformRemembered()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        await app.SetUpAsync();
        await using var scope = app.Services.CreateAsyncScope();
        var cache = scope.ServiceProvider.GetRequiredService<WebFetchCache>();
        var site = new CountedSite();

        await cache.GetOrCreateAsync(ArticleUrl, TimeSpan.FromMinutes(10), site.LoadAsync, Ct);
        await cache.ClearAsync(Ct);
        await cache.GetOrCreateAsync(ArticleUrl, TimeSpan.FromMinutes(10), site.LoadAsync, Ct);

        // The settings' toggle drops the memory through this tag, so a page is never read back from a policy the admin has undone.
        Assert.Equal(2, site.Loads);
    }

    [Fact] // spec 006, cache key
    public void TheKeyOfAUrlIsItsOwnAndNotAnotherUrlThatLooksLikeIt()
    {
        Assert.NotEqual(WebFetchCache.KeyFor(ArticleUrl), WebFetchCache.KeyFor($"{ArticleUrl}?page=2"));
        Assert.Equal(WebFetchCache.KeyFor(ArticleUrl), WebFetchCache.KeyFor(ArticleUrl));
        Assert.StartsWith("web-fetch:", WebFetchCache.KeyFor(ArticleUrl), StringComparison.Ordinal);
    }

    /// <summary>The site a test pretends to be: it counts how often the platform came to fetch the page.</summary>
    private sealed class CountedSite
    {
        public int Loads { get; private set; }

        public Task<WebFetchResponse> LoadAsync(CancellationToken cancellationToken)
        {
            Loads++;
            return Task.FromResult(WebFetchResponse.Ok("text/html", "Report", "the article", DateTimeOffset.UtcNow));
        }
    }
}
