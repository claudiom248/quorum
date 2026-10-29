using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Quorum.Web.Client;
using Quorum.Web.Client.Api;

namespace Quorum.Integration.Tests.Web;

/// <summary>
/// The Web tab of the settings (spec 006, AC-13, AC-14): the platform's one web row as one form — what a web fetch tool falls back
/// on, how the platform announces itself, and what it remembers. The instance a web search asks is the same row, and its own tab is
/// <see cref="WebSearchSettingsTests"/>.
/// </summary>
public sealed class WebFetchSettingsTests : BunitContext
{
    private readonly FakeApiHandler _api = new();
    private WebConfigurationInfo _row = WebConfigurationLimits.Defaults with { SearchUrl = "https://searxng.example.test" };
    private HttpStatusCode _read = HttpStatusCode.OK;
    private HttpStatusCode _write = HttpStatusCode.NoContent;
    private bool _writeThrows;
    private string _refusal = "The value must be between 5 and 120.";

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
                if (method == HttpMethod.Get)
                {
                    return _read == HttpStatusCode.OK
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
                        : new HttpResponseMessage(_read);
                }

                if (_writeThrows)
                {
                    // The client's own wait ran out: nothing answered, and the handler throws before a status exists.
                    throw new TaskCanceledException("The request timed out.");
                }

                return _write == HttpStatusCode.NoContent
                    ? new HttpResponseMessage(HttpStatusCode.NoContent)
                    : FakeApiHandler.Json(
                        new
                        {
                            type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
                            title = "One or more validation errors occurred.",
                            status = 400,
                            errors = new { fetchTimeoutSeconds = new[] { _refusal } },
                        },
                        HttpStatusCode.BadRequest);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
    }

    private IRenderedComponent<Quorum.Web.Client.Pages.Settings> Open()
    {
        var page = Render<Quorum.Web.Client.Pages.Settings>();
        page.WaitForState(() => !page.Markup.Contains("mud-progress-linear", StringComparison.Ordinal));
        page.FindAll(".mud-tab").First(t => t.TextContent.Contains("Web", StringComparison.Ordinal)).Click();
        return page;
    }

    [Fact] // AC-13, AC-14
    public void OneTabShowsTheWholeWebRowOnOneScreen()
    {
        var page = Open();

        Assert.Equal("https://searxng.example.test", page.Find("input[data-web-search-url]").GetAttribute("value"));
        Assert.Equal("30", page.Find("input[data-web-fetch-timeout]").GetAttribute("value"));
        Assert.Equal("5", page.Find("input[data-web-fetch-urls]").GetAttribute("value"));
        Assert.Equal("4000", page.Find("input[data-web-fetch-per-page]").GetAttribute("value"));
        Assert.Equal("20000", page.Find("input[data-web-fetch-total]").GetAttribute("value"));
        Assert.Equal("Quorum/1.0", page.Find("input[data-user-agent]").GetAttribute("value"));
        Assert.Equal("1000", page.Find("input[data-fetch-delay]").GetAttribute("value"));
        Assert.Equal("10", page.Find("input[data-cache-ttl]").GetAttribute("value"));
        Assert.NotNull(page.Find("input[data-respect-robots]"));
        Assert.NotNull(page.Find("input[data-cache-enabled]"));
    }

    [Fact] // AC-13, AC-14
    public void SavingWritesTheWholeRowAtOnce()
    {
        var page = Open();

        page.Find("input[data-web-fetch-per-page]").Change("800");
        page.Find("input[data-web-fetch-timeout]").Change("15");
        page.Find("input[data-user-agent]").Change("AcmeBot/2.0");
        page.Find("input[data-respect-robots]").Change(false);
        page.Find("button[data-save-web]").Click();
        page.WaitForState(() => _api.Count(HttpMethod.Put, "settings/web") > 0);

        var body = _api.LastBody(HttpMethod.Put, "settings/web");

        // One row, one write: what the form holds together goes together, and what it did not touch comes back as it was read.
        Assert.Contains("\"fetchTokenBudgetPerPage\":800", body, StringComparison.Ordinal);
        Assert.Contains("\"fetchTimeoutSeconds\":15", body, StringComparison.Ordinal);
        Assert.Contains("\"userAgent\":\"AcmeBot/2.0\"", body, StringComparison.Ordinal);
        Assert.Contains("\"respectRobotsTxt\":false", body, StringComparison.Ordinal);
        Assert.Contains("https://searxng.example.test", body, StringComparison.Ordinal);
        Assert.Contains("\"fetchTotalTokenBudget\":20000", body, StringComparison.Ordinal);
        page.WaitForElement("[role=status]");
        Assert.Contains("Web settings saved", page.Find("[role=status]").TextContent, StringComparison.Ordinal);
    }

    [Fact] // spec 006: a refused write says so and leaves the form usable
    public void ARefusedSaveSaysSoAndDoesNotLockTheForm()
    {
        _write = HttpStatusCode.BadRequest;
        var page = Open();

        page.Find("input[data-web-fetch-timeout]").Change("2");
        page.Find("button[data-save-web]").Click();
        page.WaitForElement(".problem-list li");

        Assert.Equal(_refusal, page.Find(".problem-list li").TextContent);
        Assert.False(page.Find("button[data-save-web]").HasAttribute("disabled"));
        Assert.Equal(1, _api.Count(HttpMethod.Put, "settings/web"));

        // The administrator fixes the number and saves: the same form answers again.
        _write = HttpStatusCode.NoContent;
        page.Find("input[data-web-fetch-timeout]").Change("20");
        page.Find("button[data-save-web]").Click();
        page.WaitForElement("[role=status]");

        Assert.Contains("Web settings saved", page.Find("[role=status]").TextContent, StringComparison.Ordinal);
        Assert.Contains("\"fetchTimeoutSeconds\":20", _api.LastBody(HttpMethod.Put, "settings/web"), StringComparison.Ordinal);
    }

    [Fact] // spec 006: a save the client waits out is not the admin giving up
    public void ATimedOutSaveSaysSoAndDoesNotLockTheForm()
    {
        _writeThrows = true;
        var page = Open();

        page.Find("input[data-web-fetch-timeout]").Change("20");
        page.Find("button[data-save-web]").Click();
        page.WaitForState(() => page.Markup.Contains("not reachable", StringComparison.Ordinal));

        // Without the client's own catch the exception escapes the handler, the code after the call never runs, and the button
        // stays disabled for the rest of the page's life.
        Assert.False(page.Find("button[data-save-web]").HasAttribute("disabled"));

        _writeThrows = false;
        page.Find("button[data-save-web]").Click();
        page.WaitForElement("[role=status]");
        Assert.Contains("Web settings saved", page.Find("[role=status]").TextContent, StringComparison.Ordinal);
    }

    [Fact] // AC-13, AC-14
    public void TheTabIsAbsentForSomeoneWhoMayNotManageThePlatform()
    {
        _read = HttpStatusCode.Forbidden;

        var page = Render<Quorum.Web.Client.Pages.Settings>();
        page.WaitForState(() => !page.Markup.Contains("mud-progress-linear", StringComparison.Ordinal));

        Assert.DoesNotContain(page.FindAll(".mud-tab"), t => t.TextContent.Contains("Web", StringComparison.Ordinal));
    }
}
