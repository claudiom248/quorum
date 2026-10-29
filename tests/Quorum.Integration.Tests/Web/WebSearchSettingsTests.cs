using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Quorum.Web.Client;
using Quorum.Web.Client.Api;

namespace Quorum.Integration.Tests.Web;

/// <summary>The Web search tab of the settings: the SearXNG instance of the platform (spec 005, AC-9).</summary>
public sealed class WebSearchSettingsTests : BunitContext
{
    private readonly FakeApiHandler _api = new();
    private string? _url = "https://searxng.example.test";
    private int _timeout = 45;
    private HttpStatusCode _read = HttpStatusCode.OK;

    public WebSearchSettingsTests()
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

            if (path == "/api/settings/web-search")
            {
                return method == HttpMethod.Get
                    ? _read == HttpStatusCode.OK
                        ? FakeApiHandler.Json(new { url = _url, defaultTimeout = _timeout })
                        : new HttpResponseMessage(_read)
                    : new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
    }

    private IRenderedComponent<Quorum.Web.Client.Pages.Settings> Open() => Render<Quorum.Web.Client.Pages.Settings>();

    [Fact] // AC-9
    public void TheTabShowsTheInstanceTheAdministratorSet()
    {
        var page = Open();
        page.WaitForState(() => !page.Markup.Contains("mud-progress-linear", StringComparison.Ordinal));

        page.FindAll(".mud-tab").First(t => t.TextContent.Contains("Web search", StringComparison.Ordinal)).Click();

        Assert.NotNull(page.Find("input[data-web-search-url]"));
        Assert.Equal("https://searxng.example.test", page.Find("input[data-web-search-url]").GetAttribute("value"));
        Assert.Equal("45", page.Find("input[data-web-search-timeout]").GetAttribute("value"));
    }

    [Fact] // AC-9
    public void SavingWritesTheInstanceAndSaysItWasSaved()
    {
        var page = Open();
        page.WaitForState(() => !page.Markup.Contains("mud-progress-linear", StringComparison.Ordinal));
        page.FindAll(".mud-tab").First(t => t.TextContent.Contains("Web search", StringComparison.Ordinal)).Click();

        page.Find("input[data-web-search-url]").Change("http://search.internal:8080");
        page.Find("input[data-web-search-timeout]").Change("120");
        page.Find("button[data-save-web-search]").Click();
        page.WaitForState(() => _api.Count(HttpMethod.Put, "settings/web-search") > 0);

        Assert.Contains("http://search.internal:8080", _api.LastBody(HttpMethod.Put, "settings/web-search"), StringComparison.Ordinal);
        Assert.Contains("120", _api.LastBody(HttpMethod.Put, "settings/web-search"), StringComparison.Ordinal);
        page.WaitForElement("[role=status]");
        Assert.Contains("Web search saved", page.Find("[role=status]").TextContent, StringComparison.Ordinal);
    }

    [Fact] // AC-9
    public void TheTabIsAbsentForSomeoneWhoMayNotManageTools()
    {
        _read = HttpStatusCode.Forbidden;

        var page = Open();
        page.WaitForState(() => !page.Markup.Contains("mud-progress-linear", StringComparison.Ordinal));

        Assert.DoesNotContain(page.FindAll(".mud-tab"), t => t.TextContent.Contains("Web search", StringComparison.Ordinal));
    }

    [Fact] // AC-9
    public void AnInstanceThatWasNeverSetIsOfferedWithTheDefaultWait()
    {
        _url = null;
        _timeout = 90;

        var page = Open();
        page.WaitForState(() => !page.Markup.Contains("mud-progress-linear", StringComparison.Ordinal));
        page.FindAll(".mud-tab").First(t => t.TextContent.Contains("Web search", StringComparison.Ordinal)).Click();

        Assert.Equal("90", page.Find("input[data-web-search-timeout]").GetAttribute("value"));
        Assert.Contains("No instance is set", page.Markup, StringComparison.Ordinal);
    }
}
