using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Quorum.Web.Client;
using Quorum.Web.Client.Api;
using Quorum.Web.Client.Components;

namespace Quorum.Integration.Tests.Web;

/// <summary>The getting-started checklist of the home page (spec 010, D-13; AC-13).</summary>
public sealed class GettingStartedTests : BunitContext
{
    private static readonly string[] Keys = ["model", "prompt", "tools", "knowledge"];

    private readonly FakeApiHandler _api = new();
    private string _role = "Admin";
    private bool[] _done = [true, false, false, false];
    private bool _dismissed;

    public GettingStartedTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        var client = new QuorumApi(_api.CreateClient());
        Services.AddSingleton(client);
        var session = new Session(client);
        Services.AddSingleton(session);
        Services.AddSingleton(new OnboardingProgress(client, session));

        _api.Respond = (method, path) => (method.Method, path) switch
        {
            ("GET", "/api/setup/status") => FakeApiHandler.Json(new { isSetupMode = false }),
            ("GET", "/api/auth/me") => FakeApiHandler.Json(new { email = "ada@example.com", displayName = "Ada", role = _role }),
            ("GET", "/api/onboarding/checklist") => FakeApiHandler.Json(new
            {
                items = Keys.Select((key, i) => new { key, done = _done[i] }),
                dismissed = _dismissed,
            }),
            ("PUT", "/api/onboarding/checklist/dismissed") => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private IRenderedComponent<GettingStarted> RenderChecklist()
    {
        Services.GetRequiredService<Session>().LoadAsync().GetAwaiter().GetResult();
        return Render<GettingStarted>();
    }

    [Fact]
    public void AnAdminSeesTheFourStepsWithWhatIsDone()
    {
        var page = RenderChecklist();

        page.WaitForAssertion(() => Assert.Equal(4, page.FindAll("[data-step]").Count));
        Assert.Equal("true", page.Find("[data-step=model]").GetAttribute("data-done"));
        Assert.Equal("false", page.Find("[data-step=knowledge]").GetAttribute("data-done"));
    }

    [Theory]
    [InlineData("User", false, false)]
    [InlineData("Admin", true, false)]
    [InlineData("Admin", false, true)]
    public void ItIsHiddenForOtherRolesWhenDismissedAndWhenComplete(string role, bool dismissed, bool complete)
    {
        _role = role;
        _dismissed = dismissed;
        _done = complete ? [true, true, true, true] : _done;

        var page = RenderChecklist();

        Assert.Empty(page.FindAll("[data-getting-started]"));
        Assert.Equal(role == "Admin" ? 1 : 0, _api.Count(HttpMethod.Get, "/api/onboarding/checklist"));
    }

    [Fact]
    public void DismissingItHidesItAndTellsTheServer()
    {
        var page = RenderChecklist();
        page.WaitForAssertion(() => page.Find("[data-getting-started]"));

        page.FindAll("button").First(b => b.TextContent.Contains("Dismiss", StringComparison.Ordinal)).Click();

        page.WaitForAssertion(() => Assert.Empty(page.FindAll("[data-getting-started]")));
        Assert.Equal(1, _api.Count(HttpMethod.Put, "/api/onboarding/checklist/dismissed"));
    }
}
