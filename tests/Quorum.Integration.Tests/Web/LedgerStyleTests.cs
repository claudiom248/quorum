using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Quorum.Web.Client;
using Quorum.Web.Client.Api;
using Quorum.Web.Client.Components;

namespace Quorum.Integration.Tests.Web;

/// <summary>The three ledger styles that a cookie picks, drawn from the same calls.</summary>
public sealed class LedgerStyleTests : BunitContext
{
    private static readonly DateTimeOffset Start = new(2026, 11, 3, 18, 0, 0, TimeSpan.Zero);

    public LedgerStyleTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
    }

    private static ToolCallView Call(string tool, Guid? id = null, Guid? parent = null, string? agent = null, string arguments = "{}") =>
        ToolCallView.From(
            new ToolCallEvent(id ?? Guid.NewGuid(), tool, JsonDocument.Parse(arguments).RootElement.Clone(), false, null, parent, agent),
            Start);

    private static void Succeed(ToolCallView call, int seconds) =>
        call.Apply(new ToolResultEvent(call.CallId, "succeeded", "ok", 2), Start.AddSeconds(seconds));

    private IRenderedComponent<ToolCallGroup> Draw(LedgerStyle style, params ToolCallView[] calls) =>
        Render<ToolCallGroup>(parameters => parameters
            .Add(group => group.Calls, calls)
            .Add(group => group.Style, style)
            .Add(group => group.OnDecide, (_, _) => Task.CompletedTask)
            .Add(group => group.OnAnswer, (_, _) => Task.CompletedTask));

    [Theory]
    [InlineData("a", LedgerStyle.Cards)]
    [InlineData("B", LedgerStyle.Timeline)]
    [InlineData("bars", LedgerStyle.Bars)]
    [InlineData("classic", LedgerStyle.Classic)]
    [InlineData("nonsense", LedgerStyle.Classic)]
    [InlineData(null, LedgerStyle.Classic)]
    public void AValueNamesAStyleAndAnythingElseIsTheClassicLedger(string? value, LedgerStyle expected)
    {
        Assert.Equal(expected, LedgerStyles.Parse(value));
    }

    [Fact]
    public void AnAgentKeepsItsTintAndItsSlugReadsAsAName()
    {
        Assert.Equal(LedgerStyles.TintOf("contract-analyst"), LedgerStyles.TintOf("contract-analyst"));
        Assert.InRange(LedgerStyles.TintOf("deadline-planner"), 0, 7);
        Assert.Equal("Contract analyst", LedgerStyles.NameOf("contract-analyst"));
    }

    [Fact]
    public void TheClassicLedgerIsUnchangedWhenNoStyleIsPicked()
    {
        var page = Draw(LedgerStyle.Classic, Call("builtin__calculator"));

        Assert.NotEmpty(page.FindAll(".tool-ledger-row"));
        Assert.Empty(page.FindAll(".lg"));
    }

    [Fact]
    public void TheClassicLedgerNumbersOnlyTheTopLevel()
    {
        var delegation = Call("contract-analyst");
        var page = Draw(LedgerStyle.Classic, delegation, Call("rag__search_contracts", parent: delegation.CallId, agent: "contract-analyst"), Call("builtin__calculator"));

        Assert.Equal(["01", "02"], page.FindAll(".tool-ledger-index").Select(index => index.TextContent));
        Assert.Empty(page.FindAll(".tool-ledger-nested .tool-ledger-index"));
    }

    [Fact]
    public void CardsPutThePlainCallsInOneToolsCardAndEachDelegationInItsOwn()
    {
        var delegation = Call("deadline-planner", arguments: """{"task":"Calcola la data limite"}""");
        var nested = Call("sample-reference-data__notice_deadline", parent: delegation.CallId, agent: "deadline-planner");
        var page = Draw(LedgerStyle.Cards, Call("builtin__current_datetime"), Call("builtin__calculator"), delegation, nested);

        var tools = Assert.Single(page.FindAll(".lg-tools"));
        Assert.Equal(2, tools.QuerySelectorAll(".lg-plain").Length);
        var agent = Assert.Single(page.FindAll(".lg-agent"));
        Assert.Contains("Deadline planner", agent.TextContent, StringComparison.Ordinal);
        Assert.Contains("Calcola la data limite", agent.TextContent, StringComparison.Ordinal);
        Assert.Single(agent.QuerySelectorAll(".lg-plain"));
    }

    [Theory]
    [InlineData(LedgerStyle.Timeline, "lg-timeline")]
    [InlineData(LedgerStyle.Bars, "lg-bars")]
    public void EveryStyleDrawsTheSameCallsUnderItsOwnClass(LedgerStyle style, string cssClass)
    {
        var delegation = Call("contract-analyst");
        var page = Draw(style, Call("builtin__calculator"), delegation, Call("rag__search_contracts", parent: delegation.CallId, agent: "contract-analyst"));

        Assert.Contains(cssClass, page.Find(".lg").ClassList);
        Assert.Equal(2, page.FindAll(".lg-plain").Count);
        Assert.Single(page.FindAll(".lg-agent"));
    }

    [Fact]
    public void ADelegationSaysWhenItRunsAtTheSameTimeAsAnotherAndHowLongItTook()
    {
        var first = Call("contract-analyst");
        var second = Call("finance-analyst");
        var finished = Call("deadline-planner");
        Succeed(finished, 12);

        var parallel = Draw(LedgerStyle.Cards, first, second);
        Assert.Equal(2, parallel.FindAll(".lg-chip").Count);

        var alone = Draw(LedgerStyle.Cards, finished);
        Assert.Empty(alone.FindAll(".lg-chip"));
        Assert.Contains("12 s", alone.Find(".lg-meta").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTextOfASucceededDelegationStaysHiddenInEveryStyleAndAFailureKeepsItsMessage()
    {
        foreach (var style in new[] { LedgerStyle.Cards, LedgerStyle.Timeline, LedgerStyle.Bars })
        {
            var ok = Call("contract-analyst");
            ok.Apply(new ToolResultEvent(ok.CallId, "succeeded", "The sub-agent text", 18));
            var failed = Call("finance-analyst");
            failed.Apply(new ToolResultEvent(failed.CallId, "failed", "The sub-agent failed: context", 30));

            var page = Draw(style, ok, failed);

            Assert.DoesNotContain("The sub-agent text", page.Markup, StringComparison.Ordinal);
            Assert.Contains("The sub-agent failed: context", page.Markup, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AConfirmationStillAsksInsideEveryStyle()
    {
        foreach (var style in new[] { LedgerStyle.Cards, LedgerStyle.Timeline, LedgerStyle.Bars })
        {
            var waiting = ToolCallView.From(
                new ToolCallEvent(Guid.NewGuid(), "builtin__calculator", JsonDocument.Parse("{}").RootElement.Clone(), true, Start.AddMinutes(5), null, null),
                Start);

            var page = Draw(style, waiting);

            Assert.Contains("Allow this tool to run?", page.Find("[role=dialog]").TextContent, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TwoAgentsOfOneAnswerNeverShareATint()
    {
        Assert.Equal(LedgerStyles.TintOf("deadline-planner"), LedgerStyles.TintOf("finance-analyst"));

        var page = Draw(LedgerStyle.Cards, Call("deadline-planner"), Call("finance-analyst"));

        var tints = page.FindAll(".lg-agent").Select(agent => agent.ClassList.Single(c => c.StartsWith("lg-tint-", StringComparison.Ordinal))).ToList();
        Assert.Equal(2, tints.Distinct().Count());
    }
}
