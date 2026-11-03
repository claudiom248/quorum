using System.Net;
using Bunit;
using Quorum.Web.Client;

namespace Quorum.Integration.Tests.Web;

/// <summary>The Display tab of the settings: how the tool calls of a chat are drawn.</summary>
public sealed class SettingsDisplayTests : ListPageTestContext
{
    public SettingsDisplayTests()
    {
        Api.Respond = (method, path) =>
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

            return method == HttpMethod.Get && path == "/api/assistants"
                ? FakeApiHandler.Json(Array.Empty<object>())
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        };
    }

    private IRenderedComponent<Quorum.Web.Client.Pages.Settings> OpenDisplay()
    {
        var page = Visit<Quorum.Web.Client.Pages.Settings>("/settings");
        page.FindAll(".mud-tab").First(tab => tab.TextContent.Contains("Display", StringComparison.Ordinal)).Click();
        return page;
    }

    [Fact]
    public void EveryStyleIsOfferedWithWhatItLooksLike()
    {
        var page = OpenDisplay();

        page.WaitForAssertion(() =>
        {
            Assert.Equal(LedgerStyles.All.Count, page.FindAll(".ledger-choice").Count);
            foreach (var style in LedgerStyles.All)
            {
                Assert.Contains(LedgerStyles.Description(style), page.Markup, StringComparison.Ordinal);
            }
        });
    }

    [Fact]
    public void ChoosingAStyleKeepsItInTheCookieTheChatReads()
    {
        var page = OpenDisplay();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("input[type=radio]")));

        page.FindAll("input[type=radio]")[(int)LedgerStyle.Timeline].Click();

        page.WaitForAssertion(() =>
        {
            var write = Assert.Single(JSInterop.Invocations, call => call.Identifier == "quorumLedger.write");
            Assert.Equal([LedgerStyles.CookieName, "b"], write.Arguments.Select(argument => argument?.ToString()));
        });
    }

    [Fact]
    public void TheStyleSavedInTheCookieIsTheOneSelected()
    {
        JSInterop.Setup<string?>("quorumLedger.read", LedgerStyles.CookieName).SetResult("c");

        var page = OpenDisplay();

        page.WaitForAssertion(() => Assert.NotNull(page.FindAll("input[type=radio]")[(int)LedgerStyle.Bars].GetAttribute("checked")));
    }
}
