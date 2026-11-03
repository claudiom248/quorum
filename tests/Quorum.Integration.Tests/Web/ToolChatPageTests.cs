using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Quorum.Web.Client;
using Quorum.Web.Client.Api;

namespace Quorum.Integration.Tests.Web;

/// <summary>What the chat page shows and sends while an answer uses tools (spec 004, AC-49).</summary>
public sealed class ToolChatPageTests : BunitContext
{
    private static readonly Guid Conversation = Guid.Parse("01a14117-8443-7208-890d-b78925ebb291");
    private static readonly Guid Call = Guid.Parse("01a14118-0000-7208-890d-b78925ebb291");
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly FakeApiHandler _api = new();
    private readonly Pipe _stream = new();
    private bool _answered;

    public ToolChatPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(TimeProvider.System);
        var client = new QuorumApi(_api.CreateClient());
        Services.AddSingleton(client);
        var session = new Session(client);
        Services.AddSingleton(session);
        Services.AddSingleton(new OnboardingProgress(client, session));

        _api.Respond = (method, path) =>
        {
            if (method == HttpMethod.Post && path == "/api/conversations")
            {
                return FakeApiHandler.Json(new { id = Conversation }, HttpStatusCode.Created);
            }

            if (method == HttpMethod.Post && path.EndsWith("/messages", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(_stream.Reader.AsStream()) { Headers = { ContentType = new("text/event-stream") } } };
            }

            if (method == HttpMethod.Post && (path.EndsWith("/decision", StringComparison.Ordinal) || path.EndsWith("/answer", StringComparison.Ordinal)))
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (method == HttpMethod.Get && path == $"/api/conversations/{Conversation}")
            {
                return FakeApiHandler.Json(new
                {
                    id = Conversation,
                    title = "Hi",
                    messages = _answered
                        ? new[]
                        {
                            new { id = Guid.NewGuid(), role = "User", content = "Hi", status = "Completed", createdAt = DateTimeOffset.UnixEpoch },
                            new { id = Guid.NewGuid(), role = "Assistant", content = "Stored: It is 4.", status = "Completed", createdAt = DateTimeOffset.UnixEpoch },
                        }
                        : [],
                    attachments = Array.Empty<object>(),
                });
            }

            if (method == HttpMethod.Get && (path == "/api/conversations" || path.EndsWith("/attachments", StringComparison.Ordinal)))
            {
                return FakeApiHandler.Json(Array.Empty<object>());
            }

            if (path.EndsWith("/context", StringComparison.Ordinal))
            {
                return FakeApiHandler.Json(new ConversationContext(4096, 3276, 48, 0, 20, 3200, ToolsTokens: 8));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
    }

    private async Task PushAsync(string eventName, object data)
    {
        var text = $"event: {eventName}\ndata: {JsonSerializer.Serialize(data, Web)}\n\n";
        await _stream.Writer.WriteAsync(Encoding.UTF8.GetBytes(text), Xunit.TestContext.Current.CancellationToken);
    }

    private Task PushToolCallAsync(string arguments, bool requiresConfirmation) =>
        PushAsync("tool_call", new
        {
            callId = Call,
            tool = "calculator",
            arguments = JsonDocument.Parse(arguments).RootElement,
            requiresConfirmation,
            expiresAt = requiresConfirmation ? DateTimeOffset.UtcNow.AddMinutes(5) : (DateTimeOffset?)null,
        });

    private Task FinishAnswerAsync()
    {
        _answered = true;
        return PushAsync("done", new { messageId = Guid.NewGuid(), status = "Completed", error = (string?)null });
    }

    private async Task<(IRenderedComponent<Quorum.Web.Client.Pages.Chat> Page, Task Sending)> StartAnswerAsync()
    {
        var page = Render<Quorum.Web.Client.Pages.Chat>();
        page.Find("textarea").Input("What is 2+2?");
        var sending = page.InvokeAsync(() => page.Instance.SendFromKeyboard());
        await Task.Yield();
        return (page, sending);
    }

    private static void Click(IRenderedComponent<Quorum.Web.Client.Pages.Chat> page, string label) =>
        page.FindAll("button").First(button => button.TextContent.Trim() == label).Click();

    [Fact]
    public async Task ACallThatNeedsConfirmationShowsADialogWithTheFullArgumentsAndNothingIsSentYet()
    {
        var (page, sending) = await StartAnswerAsync();

        await PushToolCallAsync("""{"expression":"2+2","note":"exactly as sent"}""", requiresConfirmation: true);

        page.WaitForAssertion(() =>
        {
            var dialog = page.Find("[role=dialog]");
            Assert.Contains("Allow this tool to run?", dialog.TextContent, StringComparison.Ordinal);
            Assert.Contains("calculator", dialog.TextContent, StringComparison.Ordinal);
            Assert.Contains("\"expression\": \"2+2\"", dialog.TextContent, StringComparison.Ordinal);
            Assert.Contains("\"note\": \"exactly as sent\"", dialog.TextContent, StringComparison.Ordinal);
        });
        Assert.Equal(0, _api.Count(HttpMethod.Post, "/decision"));

        await FinishAnswerAsync();
        _stream.Writer.Complete();
        await sending;
    }

    [Theory]
    [InlineData("Approve", true)]
    [InlineData("Deny", false)]
    public async Task TheDecisionIsSentForThatCallAndTheDialogGoesAway(string button, bool approved)
    {
        var (page, sending) = await StartAnswerAsync();
        await PushToolCallAsync("""{"expression":"2+2"}""", requiresConfirmation: true);
        page.WaitForAssertion(() => page.Find("[role=dialog]"));

        Click(page, button);

        page.WaitForAssertion(() =>
        {
            Assert.Empty(page.FindAll("[role=dialog]"));
            Assert.Equal(1, _api.Count(HttpMethod.Post, $"/tool-calls/{Call}/decision"));
        });
        using var body = JsonDocument.Parse(_api.LastBody(HttpMethod.Post, "/decision")!);
        Assert.Equal(approved, body.RootElement.GetProperty("approved").GetBoolean());

        await FinishAnswerAsync();
        _stream.Writer.Complete();
        await sending;
    }

    [Fact]
    public async Task TheCardFollowsTheCallFromRunningToItsResultAndStaysAfterTheAnswerEnds()
    {
        var (page, sending) = await StartAnswerAsync();

        await PushToolCallAsync("""{"expression":"2+2"}""", requiresConfirmation: false);
        page.WaitForAssertion(() =>
        {
            Assert.Contains("Running", page.Find(".tool-ledger-row").TextContent, StringComparison.Ordinal);
            Assert.Empty(page.FindAll("[role=dialog]"));
        });

        await PushAsync("tool_result", new { callId = Call, status = "succeeded", content = "4", sizeCharacters = 1 });
        page.WaitForAssertion(() =>
        {
            var row = page.Find(".tool-ledger-row");
            Assert.Contains("Done", row.TextContent, StringComparison.Ordinal);
            Assert.Contains(row.QuerySelectorAll("pre"), pre => pre.TextContent == "4");
        });

        await FinishAnswerAsync();
        _stream.Writer.Complete();
        await sending;
        page.WaitForAssertion(() =>
        {
            Assert.Contains("Stored: It is 4.", page.Markup, StringComparison.Ordinal);
            Assert.Contains("Done", page.Find(".tool-ledger-row").TextContent, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("denied", "Denied")]
    [InlineData("timed_out", "Timed out")]
    [InlineData("failed", "Failed")]
    [InlineData("cancelled", "Stopped")]
    public async Task EveryEndOfACallIsNamedOnItsCard(string status, string label)
    {
        var (page, sending) = await StartAnswerAsync();
        await PushToolCallAsync("""{"expression":"2+2"}""", requiresConfirmation: true);
        page.WaitForAssertion(() => page.Find("[role=dialog]"));

        await PushAsync("tool_result", new { callId = Call, status, content = "not run", sizeCharacters = 7 });

        page.WaitForAssertion(() =>
        {
            Assert.Contains(label, page.Find(".tool-ledger-row").TextContent, StringComparison.Ordinal);
            Assert.Empty(page.FindAll("[role=dialog]"));
        });

        await FinishAnswerAsync();
        _stream.Writer.Complete();
        await sending;
    }

    [Fact]
    public async Task AStreamThatEndsWhileAConfirmationIsOpenClosesTheDialogAndMarksTheCallStopped()
    {
        var (page, sending) = await StartAnswerAsync();
        await PushToolCallAsync("""{"expression":"2+2"}""", requiresConfirmation: true);
        page.WaitForAssertion(() => page.Find("[role=dialog]"));

        // The connection goes away: no result and no done event ever arrive.
        _stream.Writer.Complete();
        await sending;

        page.WaitForAssertion(() =>
        {
            Assert.Empty(page.FindAll("[role=dialog]"));
            Assert.Contains("Stopped", page.Find(".tool-ledger-row").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task AQuestionStillOpenWhenTheStreamEndsIsClosedToo()
    {
        var (page, sending) = await StartAnswerAsync();
        await PushAsync("tool_call", new { callId = Call, tool = "ask_user", arguments = new { question = "Which?" }, requiresConfirmation = false, expiresAt = (DateTimeOffset?)null });
        await PushAsync("ask_user", new { callId = Call, question = "Which?", expiresAt = DateTimeOffset.UtcNow.AddMinutes(5) });
        page.WaitForAssertion(() => page.Find(".tool-question"));

        _stream.Writer.Complete();
        await sending;

        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".tool-question")));
    }

    [Fact]
    public async Task TheCardsStayAboveTheFinalAnswerWhenItEnds()
    {
        var (page, sending) = await StartAnswerAsync();
        await PushToolCallAsync("""{"expression":"2+2"}""", requiresConfirmation: false);
        await PushAsync("tool_result", new { callId = Call, status = "succeeded", content = "4", sizeCharacters = 1 });
        await FinishAnswerAsync();
        _stream.Writer.Complete();
        await sending;

        page.WaitForAssertion(() =>
        {
            Assert.Contains("Stored: It is 4.", page.Markup, StringComparison.Ordinal);
            Assert.True(
                page.Markup.IndexOf("tool-group", StringComparison.Ordinal) < page.Markup.IndexOf("Stored: It is 4.", StringComparison.Ordinal),
                "The tool calls must come before the answer, as they do while streaming.");
        });
    }

    [Fact]
    public async Task TheDialogTakesTheFocusWhenItOpens()
    {
        var (page, sending) = await StartAnswerAsync();

        await PushToolCallAsync("""{"expression":"2+2"}""", requiresConfirmation: true);

        page.WaitForAssertion(() => page.Find("[role=dialog]"));

        // The script is called after the render that shows the dialog, so no further render announces it: poll.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        string[] focusCalls;
        do
        {
            focusCalls = await RecordedFocusCallsAsync();
            if (focusCalls.Length == 1)
            {
                break;
            }

            await Task.Delay(25, Xunit.TestContext.Current.CancellationToken);
        } while (DateTime.UtcNow < deadline);

        Assert.Single(focusCalls);
        await FinishAnswerAsync();
        _stream.Writer.Complete();
        await sending;

        // bUnit keeps the recorded calls in a dictionary the renderer writes to while the test reads it, so the copy is retried:
        // reading again is what a changed collection means here, not that the call arrived.
        async Task<string[]> RecordedFocusCallsAsync()
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    return [.. JSInterop.Invocations.Select(i => i.Identifier).Where(id => id == "quorumChat.focusDialog")];
                }
                catch (InvalidOperationException)
                {
                    await Task.Delay(2, Xunit.TestContext.Current.CancellationToken);
                }
            }

            return [.. JSInterop.Invocations.Select(i => i.Identifier).Where(id => id == "quorumChat.focusDialog")];
        }
    }

    [Fact]
    public async Task EscapeDeniesTheCall()
    {
        var (page, sending) = await StartAnswerAsync();
        await PushToolCallAsync("""{"expression":"2+2"}""", requiresConfirmation: true);
        page.WaitForAssertion(() => page.Find("[role=dialog]"));

        page.Find("[role=dialog]").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });

        page.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Post, $"/tool-calls/{Call}/decision")));
        using var body = JsonDocument.Parse(_api.LastBody(HttpMethod.Post, "/decision")!);
        Assert.False(body.RootElement.GetProperty("approved").GetBoolean());
        await FinishAnswerAsync();
        _stream.Writer.Complete();
        await sending;
    }

    [Fact]
    public async Task AQuestionOfTheModelIsShownAndTheAnswerIsSentForThatCall()
    {
        var (page, sending) = await StartAnswerAsync();
        await PushAsync("tool_call", new { callId = Call, tool = "ask_user", arguments = new { question = "Which city?" }, requiresConfirmation = false, expiresAt = (DateTimeOffset?)null });
        await PushAsync("ask_user", new { callId = Call, question = "Which city?", expiresAt = DateTimeOffset.UtcNow.AddMinutes(5) });

        page.WaitForAssertion(() => Assert.Contains("Which city?", page.Find(".tool-question").TextContent, StringComparison.Ordinal));
        Assert.True(page.Find(".tool-question button").HasAttribute("disabled"));

        page.Find(".tool-question input").Input("Rome");
        page.Find(".tool-question").Submit();

        page.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Post, $"/tool-calls/{Call}/answer")));
        using var body = JsonDocument.Parse(_api.LastBody(HttpMethod.Post, "/answer")!);
        Assert.Equal("Rome", body.RootElement.GetProperty("answer").GetString());
        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".tool-question")));

        await PushAsync("tool_result", new { callId = Call, status = "succeeded", content = "Rome", sizeCharacters = 4 });
        await FinishAnswerAsync();
        _stream.Writer.Complete();
        await sending;
    }

    [Fact]
    public async Task APingFromTheServerIsIgnored()
    {
        var (page, sending) = await StartAnswerAsync();

        await PushAsync("ping", new { });
        await PushToolCallAsync("""{"expression":"2+2"}""", requiresConfirmation: false);

        page.WaitForAssertion(() => Assert.Single(page.FindAll(".tool-ledger-row")));
        await FinishAnswerAsync();
        _stream.Writer.Complete();
        await sending;
    }

    private static readonly Guid Delegation = Guid.Parse("01a14119-0000-7208-890d-b78925ebb291");
    private static readonly Guid Nested = Guid.Parse("01a1411a-0000-7208-890d-b78925ebb291");

    private Task PushDelegationAsync() =>
        PushAsync("tool_call", new
        {
            callId = Delegation,
            tool = "web-researcher",
            isDelegation = true,
            arguments = JsonDocument.Parse("""{"task":"find the price"}""").RootElement,
            requiresConfirmation = false,
        });

    private Task PushNestedCallAsync(bool requiresConfirmation) =>
        PushAsync("tool_call", new
        {
            callId = Nested,
            tool = "calculator",
            arguments = JsonDocument.Parse("""{"expression":"2+2"}""").RootElement,
            requiresConfirmation,
            expiresAt = requiresConfirmation ? DateTimeOffset.UtcNow.AddMinutes(5) : (DateTimeOffset?)null,
            parentCallId = Delegation,
            agentSlug = "web-researcher",
        });

    [Fact]
    public async Task ADelegationIsOneEntryWithItsStateAndTheCallsOfTheSubAgentNestedWithoutItsText()
    {
        var (page, sending) = await StartAnswerAsync();

        await PushDelegationAsync();
        await PushNestedCallAsync(requiresConfirmation: false);
        await PushAsync("tool_result", new { callId = Nested, status = "succeeded", content = "4", sizeCharacters = 1 });
        await PushAsync("tool_result", new { callId = Delegation, status = "succeeded", content = "The sub-agent text", sizeCharacters = 18 });

        page.WaitForAssertion(() =>
        {
            var top = Assert.Single(page.FindAll(".tool-ledger:not(.tool-ledger-nested) > .tool-ledger-row"));
            Assert.Contains("web-researcher", top.TextContent, StringComparison.Ordinal);
            Assert.Contains("Done", top.TextContent, StringComparison.Ordinal);

            var nested = Assert.Single(top.QuerySelectorAll(".tool-ledger-nested > .tool-ledger-row"));
            Assert.Contains("calculator", nested.TextContent, StringComparison.Ordinal);
            Assert.Contains("web-researcher", nested.QuerySelector(".tool-ledger-agent")!.TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("The sub-agent text", page.Markup, StringComparison.Ordinal);
        });

        await FinishAnswerAsync();
        _stream.Writer.Complete();
        await sending;
    }

    [Fact]
    public async Task AConfirmationInsideADelegationNamesTheSubAgentAndTheDecisionIsSentForThatCall()
    {
        var (page, sending) = await StartAnswerAsync();

        await PushDelegationAsync();
        await PushNestedCallAsync(requiresConfirmation: true);

        page.WaitForAssertion(() =>
        {
            var dialog = page.Find("[role=dialog]");
            Assert.Contains("web-researcher", dialog.TextContent, StringComparison.Ordinal);
            Assert.Contains("\"expression\": \"2+2\"", dialog.TextContent, StringComparison.Ordinal);
        });

        Click(page, "Approve");

        page.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Post, $"/tool-calls/{Nested}/decision")));

        await FinishAnswerAsync();
        _stream.Writer.Complete();
        await sending;
    }

    [Theory]
    [InlineData("succeeded", false)]
    [InlineData("failed", true)]
    public async Task ADelegationWithoutToolCallsHidesItsTextUnlessItFailed(string status, bool shown)
    {
        var (page, sending) = await StartAnswerAsync();

        await PushDelegationAsync();
        await PushAsync("tool_result", new { callId = Delegation, status, content = "The sub-agent text", sizeCharacters = 18 });

        page.WaitForAssertion(() =>
        {
            Assert.Contains(status == "failed" ? "Failed" : "Done", page.Find(".tool-ledger-row").TextContent, StringComparison.Ordinal);
            Assert.Equal(shown, page.Markup.Contains("The sub-agent text", StringComparison.Ordinal));
        });

        await FinishAnswerAsync();
        _stream.Writer.Complete();
        await sending;
    }
}
