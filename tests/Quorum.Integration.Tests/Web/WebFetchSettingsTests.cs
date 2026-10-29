using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Quorum.Web.Client;
using Quorum.Web.Client.Api;

namespace Quorum.Integration.Tests.Web;

/// <summary>
/// The Web fetch and Web properties tabs of the settings (spec 006, AC-13, AC-14): what a web fetch tool falls back on and how the
/// platform behaves towards a site. Both tabs write the one row the Web search tab writes, so neither loses what the other holds.
/// </summary>
public sealed class WebFetchSettingsTests : BunitContext
{
    private readonly FakeApiHandler _api = new();
    private WebConfigurationInfo _row = WebConfigurationLimits.Defaults with { SearchUrl = "https://searxng.example.test" };
    private HttpStatusCode _read = HttpStatusCode.OK;

    public WebFetchSettingsTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        var client = new QuorumApi(_api.CreateClient());
        Services.AddSingleton(client);
        var session = new Session(client);
        Services.AddSingleton(session);
        Services.AddSingleton(new OnboardingProgress(client, session));

        _api.Respond = (method, path) =>
        {
            if (method == HttpMethod.Get && path == "/api/settings/assistant")
            {
                return FakeApiHandler.Json(new
                {
                    baseUrl = "http://ollama:11434/v1",
                    modelName = "llama3.2",
                    hasApiKey = false,
                    contextTokenBudget = 4096,
                    systemPrompt = "Be brief.",
                    supportsTools = true,
                });
            }

            if (method == HttpMethod.Get && path == "/api/assistants")
            {
                return FakeApiHandler.Json(Array.Empty<object>());
            }

            if (path == "/api/settings/web")
            {
                return method == HttpMethod.Get
                    ? _read == HttpStatusCode.OK
                        ? FakeApiHandler.Json(new
                        {
                            searchUrl = _row.SearchUrl,
                            searchTimeoutSeconds = _row.SearchTimeoutSeconds,
                            fetchTimeoutSeconds = _row.FetchTimeoutSeconds,
                            fetchMaxUrlsPerInvocation = _row.FetchMaxUrlsPerInvocation,
                            fetchTokenBudgetPerPage = _row.FetchTokenBudgetPerPage,
                            fetchTotalTokenBudget = _row.FetchTotalTokenBudget,
                            userAgent = _row.UserAgent,
                            fetchDelayMs = _row.FetchDelayMs,
                            respectRobotsTxt = _row.RespectRobotsTxt,
                            cacheEnabled = _row.CacheEnabled,
                            cacheTtlMinutes = _row.CacheTtlMinutes,
                        })
                        : new HttpResponseMessage(_read)
                    : new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
    }

    private IRenderedComponent<Quorum.Web.Client.Pages.Settings> Open(string tab)
    {
        var page = Render<Quorum.Web.Client.Pages.Settings>();
        page.WaitForState(() => !page.Markup.Contains("mud-progress-linear", StringComparison.Ordinal));
        page.FindAll(".mud-tab").First(t => t.TextContent.Contains(tab, StringComparison.Ordinal)).Click();
        return page;
    }

    [Fact] // AC-13
    public void TheFetchTabShowsWhatThePlatformAllowsAWebFetchTool()
    {
        var page = Open("Web fetch");

        Assert.Equal("30", page.Find("input[data-web-fetch-timeout]").GetAttribute("value"));
        Assert.Equal("5", page.Find("input[data-web-fetch-urls]").GetAttribute("value"));
        Assert.Equal("4000", page.Find("input[data-web-fetch-per-page]").GetAttribute("value"));
        Assert.Equal("20000", page.Find("input[data-web-fetch-total]").GetAttribute("value"));
    }

    [Fact] // AC-13
    public void SavingTheFetchTabWritesTheWholeRowAndNotOnlyItsOwnFields()
    {
        var page = Open("Web fetch");

        page.Find("input[data-web-fetch-per-page]").Change("800");
        page.Find("input[data-web-fetch-timeout]").Change("15");
        page.Find("button[data-save-web-fetch]").Click();
        page.WaitForState(() => _api.Count(HttpMethod.Put, "settings/web") > 0);

        var body = _api.LastBody(HttpMethod.Put, "settings/web");

        // One row for the whole platform: the instance and the properties the other tabs hold go back unchanged.
        Assert.Contains("\"fetchTokenBudgetPerPage\":800", body, StringComparison.Ordinal);
        Assert.Contains("\"fetchTimeoutSeconds\":15", body, StringComparison.Ordinal);
        Assert.Contains("https://searxng.example.test", body, StringComparison.Ordinal);
        Assert.Contains("\"userAgent\":\"Quorum/1.0\"", body, StringComparison.Ordinal);
        page.WaitForElement("[role=status]");
        Assert.Contains("Web fetch saved", page.Find("[role=status]").TextContent, StringComparison.Ordinal);
    }

    [Fact] // AC-14
    public void ThePropertiesTabShowsHowThePlatformAnnouncesItselfAndWhatItRemembers()
    {
        var page = Open("Web properties");

        Assert.Equal("Quorum/1.0", page.Find("input[data-user-agent]").GetAttribute("value"));
        Assert.Equal("1000", page.Find("input[data-fetch-delay]").GetAttribute("value"));
        Assert.Equal("10", page.Find("input[data-cache-ttl]").GetAttribute("value"));
        Assert.NotNull(page.Find("input[data-respect-robots]"));
        Assert.NotNull(page.Find("input[data-cache-enabled]"));
    }

    [Fact] // AC-14
    public void SavingThePropertiesTabKeepsTheFetchDefaultsTheOtherTabHolds()
    {
        var page = Open("Web properties");

        page.Find("input[data-user-agent]").Change("AcmeBot/2.0");
        page.Find("input[data-fetch-delay]").Change("250");
        page.Find("input[data-respect-robots]").Change(false);
        page.Find("button[data-save-web-properties]").Click();
        page.WaitForState(() => _api.Count(HttpMethod.Put, "settings/web") > 0);

        var body = _api.LastBody(HttpMethod.Put, "settings/web");
        Assert.Contains("\"userAgent\":\"AcmeBot/2.0\"", body, StringComparison.Ordinal);
        Assert.Contains("\"fetchDelayMs\":250", body, StringComparison.Ordinal);
        Assert.Contains("\"respectRobotsTxt\":false", body, StringComparison.Ordinal);
        Assert.Contains("\"fetchTotalTokenBudget\":20000", body, StringComparison.Ordinal);
        page.WaitForElement("[role=status]");
        Assert.Contains("Web properties saved", page.Find("[role=status]").TextContent, StringComparison.Ordinal);
    }

    [Fact] // AC-13
    public void TheTabsAreAbsentForSomeoneWhoMayNotManageThePlatform()
    {
        _read = HttpStatusCode.Forbidden;

        var page = Render<Quorum.Web.Client.Pages.Settings>();
        page.WaitForState(() => !page.Markup.Contains("mud-progress-linear", StringComparison.Ordinal));

        Assert.DoesNotContain(page.FindAll(".mud-tab"), t => t.TextContent.Contains("Web fetch", StringComparison.Ordinal));
        Assert.DoesNotContain(page.FindAll(".mud-tab"), t => t.TextContent.Contains("Web properties", StringComparison.Ordinal));
    }
}
