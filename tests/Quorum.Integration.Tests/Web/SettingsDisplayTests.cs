using System.Net;
using System.Text.Json;
using Bunit;
using Quorum.Web.Client;

namespace Quorum.Integration.Tests.Web;

/// <summary>The Display tab of the settings: how the tool calls of a chat are drawn, kept with the account of the person.</summary>
public sealed class SettingsDisplayTests : ListPageTestContext
{
    private string _stored = "classic";
    private HttpStatusCode _write = HttpStatusCode.NoContent;

    public SettingsDisplayTests()
    {
        Api.Respond = (method, path) =>
        {
            if (path == "/api/me/preferences")
            {
                return method == HttpMethod.Get
                    ? FakeApiHandler.Json(new { ledgerStyle = _stored })
                    : new HttpResponseMessage(_write);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
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
    public void ChoosingAStyleSavesItInTheAccountNotInTheBrowser()
    {
        var page = OpenDisplay();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("input[type=radio]")));

        page.FindAll("input[type=radio]")[(int)LedgerStyle.Timeline].Click();

        page.WaitForAssertion(() => Assert.Equal(1, Api.Count(HttpMethod.Put, "/api/me/preferences")));
        Assert.Equal("timeline", Body(Api, HttpMethod.Put, "/api/me/preferences").GetProperty("ledgerStyle").GetString());
        Assert.DoesNotContain(JSInterop.Invocations, call => call.Identifier.Contains("quorumLedger", StringComparison.Ordinal));
    }

    [Fact]
    public void TheStyleOfTheAccountIsTheOneSelected()
    {
        _stored = "bars";

        var page = OpenDisplay();

        page.WaitForAssertion(() => Assert.NotNull(page.FindAll("input[type=radio]")[(int)LedgerStyle.Bars].GetAttribute("checked")));
    }

    [Fact]
    public void AStyleThatCannotBeSavedSaysSoAndTheSelectionStaysWhereItWas()
    {
        _stored = "cards";
        _write = HttpStatusCode.BadRequest;
        var page = OpenDisplay();
        page.WaitForAssertion(() => Assert.NotNull(page.FindAll("input[type=radio]")[(int)LedgerStyle.Cards].GetAttribute("checked")));

        page.FindAll("input[type=radio]")[(int)LedgerStyle.Bars].Click();

        page.WaitForAssertion(() => Assert.Equal(1, Api.Count(HttpMethod.Put, "/api/me/preferences")));
        page.WaitForAssertion(() => Assert.Null(page.FindAll("input[type=radio]")[(int)LedgerStyle.Bars].GetAttribute("checked")));
    }

    [Fact]
    public void WithoutPreferencesTheLedgerIsTheClassicOne()
    {
        Api.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var page = OpenDisplay();

        page.WaitForAssertion(() => Assert.NotNull(page.FindAll("input[type=radio]")[(int)LedgerStyle.Classic].GetAttribute("checked")));
    }
}
