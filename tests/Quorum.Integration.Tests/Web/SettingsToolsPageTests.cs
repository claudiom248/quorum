using System.Net;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Quorum.Web.Client;
using Quorum.Web.Client.Api;

namespace Quorum.Integration.Tests.Web;

/// <summary>The "supports tools" flag and the activation of tools in the assistant settings (spec 004, AC-30, AC-17).</summary>
public sealed class SettingsToolsPageTests : BunitContext
{
    private static readonly Guid AssistantId = Guid.Parse("01a146c8-cab3-74b1-9132-40f5e9a95eef");
    private static readonly Guid Calculator = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b11bd");
    private static readonly Guid Clock = Guid.Parse("01a14797-67f1-7bc5-baba-742fda518aa9");
    private static readonly string[] Models = ["llama3.2", "tiny"];

    private readonly FakeApiHandler _api = new();
    private bool? _reported = true;
    private bool _supportsToolsStored = true;
    private object[] _warnings = [];

    public SettingsToolsPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        var client = new QuorumApi(_api.CreateClient());
        Services.AddSingleton(client);

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
                    supportsTools = _supportsToolsStored,
                });
            }

            if (method == HttpMethod.Post && path == "/api/settings/assistant/test")
            {
                return FakeApiHandler.Json(new { reachable = true, models = Models, supportsTools = _reported });
            }

            if (method == HttpMethod.Put && path == "/api/settings/assistant")
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (method == HttpMethod.Get && path == "/api/assistants")
            {
                return FakeApiHandler.Json(new[] { new { id = AssistantId, name = "Assistant" } });
            }

            if (method == HttpMethod.Get && path == $"/api/assistants/{AssistantId}/tools")
            {
                return FakeApiHandler.Json(new
                {
                    pool = new object[]
                    {
                        new { toolId = Calculator, name = "native__calculator", active = true },
                        new { toolId = Clock, name = "native__current_datetime", active = false },
                    },
                    warnings = _warnings,
                });
            }

            if (method == HttpMethod.Put && path == $"/api/assistants/{AssistantId}/tools")
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
    }

    private IRenderedComponent<Quorum.Web.Client.Pages.Settings> RenderPage()
    {
        var page = Render<Quorum.Web.Client.Pages.Settings>();
        page.WaitForAssertion(() => page.Find("form"));
        return page;
    }

    private static bool SwitchIsOn(IRenderedComponent<Quorum.Web.Client.Pages.Settings> page) =>
        page.Find("input[type=checkbox][role=switch], .mud-switch input").HasAttribute("checked")
        || page.Find(".mud-switch input").GetAttribute("aria-checked") == "true";

    [Fact]
    public void TheStoredFlagIsShown()
    {
        _supportsToolsStored = false;

        var page = RenderPage();

        Assert.Contains("The model supports tools", page.Markup, StringComparison.Ordinal);
        Assert.False(SwitchIsOn(page));
    }

    [Theory] // AC-30
    [InlineData(true, true, "supports tools")]
    [InlineData(false, false, "does not support tools")]
    public void TestingTheConnectionPreFillsTheFlagFromWhatTheServerReports(bool reported, bool expected, string hint)
    {
        _supportsToolsStored = !reported;
        _reported = reported;
        var page = RenderPage();

        page.FindAll("button").First(b => b.TextContent.Contains("Test connection", StringComparison.Ordinal)).Click();

        page.WaitForAssertion(() =>
        {
            Assert.Contains(hint, page.Find("#tools-support-hint").TextContent, StringComparison.Ordinal);
            Assert.Equal(expected, SwitchIsOn(page));
        });
        using var body = JsonDocument.Parse(_api.LastBody(HttpMethod.Post, "/assistant/test")!);
        Assert.Equal("llama3.2", body.RootElement.GetProperty("modelName").GetString());
    }

    [Fact] // AC-30
    public void WhenTheServerDoesNotSayTheAdminsValueStaysAndTheHintSaysSo()
    {
        _supportsToolsStored = false;
        _reported = null;
        var page = RenderPage();

        page.FindAll("button").First(b => b.TextContent.Contains("Test connection", StringComparison.Ordinal)).Click();

        page.WaitForAssertion(() => Assert.Contains("does not say", page.Find("#tools-support-hint").TextContent, StringComparison.Ordinal));
        Assert.False(SwitchIsOn(page));
    }

    [Fact]
    public void SavingSendsTheFlag()
    {
        _supportsToolsStored = false;
        var page = RenderPage();

        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Put, "/api/settings/assistant")));
        using var body = JsonDocument.Parse(_api.LastBody(HttpMethod.Put, "/api/settings/assistant")!);
        Assert.False(body.RootElement.GetProperty("supportsTools").GetBoolean());
    }

    [Fact]
    public void ThePoolIsListedWithFriendlyNamesAndItsActiveState()
    {
        var page = RenderPage();

        page.WaitForAssertion(() =>
        {
            var labels = page.FindAll(".mud-checkbox").Select(c => c.TextContent.Trim()).ToArray();
            Assert.Equal(["calculator", "current_datetime"], labels);
        });
    }

    [Fact] // AC-17
    public void SavingTheToolsSendsExactlyTheActiveOnes()
    {
        var page = RenderPage();
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".mud-checkbox input").Count));

        // Calculator is active, the clock is not: swap them.
        page.FindAll(".mud-checkbox input")[0].Change(false);
        page.FindAll(".mud-checkbox input")[1].Change(true);
        page.FindAll("form")[1].Submit();

        page.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Put, $"/api/assistants/{AssistantId}/tools")));
        using var body = JsonDocument.Parse(_api.LastBody(HttpMethod.Put, $"/api/assistants/{AssistantId}/tools")!);
        Assert.Equal([Clock.ToString()], body.RootElement.GetProperty("toolIds").EnumerateArray().Select(e => e.GetString()));
        page.WaitForAssertion(() => Assert.Contains("Tools saved.", page.Markup, StringComparison.Ordinal));
    }

    [Fact] // D-26
    public void TheWarningsOfTheServerAreShownAboutTheActiveSet()
    {
        _warnings = [new { code = "tool.too_many", message = "More than 15 tools are active: small models choose worse among many." }];

        var page = RenderPage();

        page.WaitForAssertion(() => Assert.Contains("More than 15 tools are active", page.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public void AModelMarkedAsWithoutToolsSaysThatNoneAreOffered()
    {
        _supportsToolsStored = false;

        var page = RenderPage();

        page.WaitForAssertion(() => Assert.Contains("none of these are offered", page.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutPermissionToManageToolsTheSectionIsAbsentAndTheRestWorks()
    {
        var denied = _api.Respond;
        _api.Respond = (method, path) =>
            path.StartsWith("/api/assistants", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                : denied(method, path);

        var page = RenderPage();

        Assert.DoesNotContain("Save tools", page.Markup, StringComparison.Ordinal);
        Assert.Contains("The model supports tools", page.Markup, StringComparison.Ordinal);
    }
}
