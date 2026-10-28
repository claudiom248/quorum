using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Quorum.Web.Client;
using Quorum.Web.Client.Api;

namespace Quorum.Integration.Tests.Web;

/// <summary>The Integrations tab of the settings: service tokens for the knowledge base refresh (spec 011 AC-14).</summary>
public sealed class ServiceTokenSettingsTests : BunitContext
{
    private static readonly Guid First = Guid.Parse("01a146c8-cab3-74b1-9132-40f5e9a95001");
    private static readonly Guid Second = Guid.Parse("01a146c8-cab3-74b1-9132-40f5e9a95002");
    private const string Plaintext = "svc_AbCdEfGhIjKlMnOpQrStUvWxYz0123456789abcdefghijk";

    private readonly FakeApiHandler _api = new();
    private List<object> _tokens = [];

    public ServiceTokenSettingsTests()
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

            if (path == "/api/tenants/me/service-tokens")
            {
                if (method == HttpMethod.Get)
                {
                    return FakeApiHandler.Json(_tokens);
                }

                if (method == HttpMethod.Post)
                {
                    return FakeApiHandler.Json(new { id = First, token = Plaintext, createdAt = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero) });
                }
            }

            if (method == HttpMethod.Delete && path.StartsWith("/api/tenants/me/service-tokens/", StringComparison.Ordinal))
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

    private static void OpenIntegrations(IRenderedComponent<Quorum.Web.Client.Pages.Settings> page) =>
        page.FindAll(".mud-tab").First(t => t.TextContent.Contains("Integrations", StringComparison.Ordinal)).Click();

    [Fact] // AC-14
    public void TheTabListsTheTokensByCreationDate()
    {
        _tokens =
        [
            new { id = First, createdAt = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero) },
            new { id = Second, createdAt = new DateTimeOffset(2026, 10, 26, 15, 30, 0, TimeSpan.Zero) },
        ];

        var page = RenderPage();
        OpenIntegrations(page);

        page.WaitForAssertion(() =>
        {
            Assert.Equal(1, _api.Count(HttpMethod.Get, "/api/tenants/me/service-tokens"));
            Assert.Contains("2026-10-20", page.Markup, StringComparison.Ordinal);
            Assert.Contains("2026-10-26", page.Markup, StringComparison.Ordinal);
        });
    }

    [Fact] // AC-14
    public void TheTabHasAGenerateButton()
    {
        var page = RenderPage();
        OpenIntegrations(page);

        page.WaitForAssertion(() => Assert.Contains("Generate", page.Markup, StringComparison.Ordinal));
    }

    [Fact] // AC-15
    public void GeneratingShowsThePlaintextOnceWithTheWarning()
    {
        var page = RenderPage();
        OpenIntegrations(page);
        page.WaitForAssertion(() => page.FindAll("button").First(b => b.TextContent.Contains("Generate", StringComparison.Ordinal)).Click());

        page.WaitForAssertion(() =>
        {
            Assert.Equal(1, _api.Count(HttpMethod.Post, "/api/tenants/me/service-tokens"));
            Assert.Contains(Plaintext, page.Find("[data-token-plaintext]").OuterHtml, StringComparison.Ordinal);
            Assert.Contains("will not be shown again", page.Markup, StringComparison.Ordinal);
        });
    }

    [Fact] // AC-14
    public void EachTokenHasARevokeButtonThatDeletesIt()
    {
        _tokens = [new { id = First, createdAt = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero) }];

        var page = RenderPage();
        OpenIntegrations(page);
        page.WaitForAssertion(() => page.Find("[data-revoke-token]").Click());

        page.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Delete, $"/api/tenants/me/service-tokens/{First}")));
    }
}
