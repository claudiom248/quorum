using System.Net;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Quorum.Web.Client;
using Quorum.Web.Client.Api;

namespace Quorum.Integration.Tests.Web;

/// <summary>The "supports tools" flag in the setup wizard (spec 004, AC-30).</summary>
public sealed class SetupWizardToolsTests : BunitContext
{
    private static readonly string[] Models = ["llama3.2", "tiny"];

    private readonly FakeApiHandler _api = new();
    private bool? _reported = true;

    public SetupWizardToolsTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        var client = new QuorumApi(_api.CreateClient());
        Services.AddSingleton(client);
        Services.AddSingleton(new Session(client));

        _api.Respond = (method, path) => (method.Method, path) switch
        {
            ("GET", "/api/setup/status") => FakeApiHandler.Json(new { isSetupMode = true, defaultSystemPrompt = "Be brief.", defaultContextTokenBudget = 4096 }),
            ("POST", "/api/setup/verify-token") => new HttpResponseMessage(HttpStatusCode.NoContent),
            ("POST", "/api/setup/model/test") => FakeApiHandler.Json(new { reachable = true, models = Models, supportsTools = _reported }),
            ("POST", "/api/setup/complete") => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private IRenderedComponent<Quorum.Web.Client.Pages.Setup> RenderModelStep()
    {
        var page = Render<Quorum.Web.Client.Pages.Setup>();
        page.WaitForAssertion(() => page.Find("form"));
        page.FindAll("input").First(i => i.GetAttribute("autocomplete") == "off").Change("token");
        page.FindAll("input").First(i => i.GetAttribute("autocomplete") == "name").Change("Ada");
        page.FindAll("input").First(i => i.GetAttribute("autocomplete") == "username").Change("ada@acme.test");
        page.FindAll("input").First(i => i.GetAttribute("autocomplete") == "new-password").Change("a-long-enough-password");
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Contains("Test connection", page.Markup, StringComparison.Ordinal));
        return page;
    }

    private static void TestConnection(IRenderedComponent<Quorum.Web.Client.Pages.Setup> page) =>
        page.FindAll("button").First(b => b.TextContent.Contains("Test connection", StringComparison.Ordinal)).Click();

    private static bool SwitchIsOn(IRenderedComponent<Quorum.Web.Client.Pages.Setup> page) =>
        page.Find(".mud-switch input").GetAttribute("aria-checked") == "true" || page.Find(".mud-switch input").HasAttribute("checked");

    [Theory]
    [InlineData(true, true, "supports tools")]
    [InlineData(false, false, "does not support tools")]
    public void TestingTheConnectionPreFillsTheFlagFromWhatTheServerReports(bool reported, bool expected, string hint)
    {
        _reported = reported;
        var page = RenderModelStep();

        TestConnection(page);

        page.WaitForAssertion(() =>
        {
            Assert.Contains(hint, page.Find("#tools-support-hint").TextContent, StringComparison.Ordinal);
            Assert.Equal(expected, SwitchIsOn(page));
        });
        using var body = JsonDocument.Parse(_api.LastBody(HttpMethod.Post, "/model/test")!);
        Assert.Equal("llama3.2", body.RootElement.GetProperty("modelName").GetString());
    }

    [Fact]
    public void WhenTheServerDoesNotSayTheAdminsChoiceStaysOff()
    {
        _reported = false;
        var page = RenderModelStep();
        TestConnection(page);
        page.WaitForAssertion(() => Assert.False(SwitchIsOn(page)));

        // The admin knows better than a server that stays silent: nothing the test says must switch it back on.
        _reported = null;
        TestConnection(page);

        page.WaitForAssertion(() => Assert.Contains("does not say", page.Find("#tools-support-hint").TextContent, StringComparison.Ordinal));
        Assert.False(SwitchIsOn(page));
    }

    [Fact]
    public void TheFlagTravelsWithTheCompletedSetup()
    {
        _reported = false;
        var page = RenderModelStep();
        TestConnection(page);
        page.WaitForAssertion(() => Assert.False(SwitchIsOn(page)));
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Post, "/api/setup/complete")));
        using var body = JsonDocument.Parse(_api.LastBody(HttpMethod.Post, "/api/setup/complete")!);
        Assert.False(body.RootElement.GetProperty("model").GetProperty("supportsTools").GetBoolean());
    }

    [Fact]
    public void TheSetupHasTwoStepsAndSendsNoSystemPrompt()
    {
        var page = RenderModelStep();
        Assert.Contains("Step 2 of 2", page.Markup, StringComparison.Ordinal);
        TestConnection(page);
        page.WaitForAssertion(() => Assert.Contains("Connected", page.Markup, StringComparison.Ordinal));
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Post, "/api/setup/complete")));
        using var body = JsonDocument.Parse(_api.LastBody(HttpMethod.Post, "/api/setup/complete")!);
        Assert.Equal("ada@acme.test", body.RootElement.GetProperty("admin").GetProperty("email").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("systemPrompt").ValueKind);
    }

    [Fact]
    public void AShortPasswordIsToldOnTheFirstStepBeforeTheTokenIsChecked()
    {
        var page = Render<Quorum.Web.Client.Pages.Setup>();
        page.WaitForAssertion(() => page.Find("form"));
        page.FindAll("input").First(i => i.GetAttribute("autocomplete") == "off").Change("token");
        page.FindAll("input").First(i => i.GetAttribute("autocomplete") == "name").Change("Ada");
        page.FindAll("input").First(i => i.GetAttribute("autocomplete") == "username").Change("ada@acme.test");
        page.FindAll("input").First(i => i.GetAttribute("autocomplete") == "new-password").Change("short");

        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Contains("at least 12 characters", page.Find(".problem-list").TextContent, StringComparison.Ordinal));
        Assert.Contains("Step 1 of 2", page.Markup, StringComparison.Ordinal);
        Assert.Equal(0, _api.Count(HttpMethod.Post, "/api/setup/verify-token"));
    }
}
