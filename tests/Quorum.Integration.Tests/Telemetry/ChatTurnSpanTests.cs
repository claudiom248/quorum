using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.Tools;
using Quorum.Modules.Agents.Chat;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Agents.Telemetry;
using Quorum.Shared.Telemetry;
using static Quorum.Integration.Tests.Fixtures.FakeModelServer;
using static Quorum.Integration.Tests.Telemetry.SpanTestHelpers;

namespace Quorum.Integration.Tests.Telemetry;

/// <summary>
/// The root span of a chat turn, the framework spans under it, and how the turn ended (spec 016 D-7 to D-9, D-13, D-14).
/// One app with the export on serves all the tests; each starts from the state after the set-up.
/// </summary>
[Collection("Telemetry")]
public sealed class ChatTurnSpanTests(SharedTelemetryApp host) : IClassFixture<SharedTelemetryApp>, IAsyncLifetime
{
    private const string ModelName = "llama3.2";

    public ValueTask InitializeAsync() => new(host.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-2
    public async Task AnAnswerWithOneModelCallIsOneTraceWithItsModelSpanAndUsage()
    {
        var conversation = await ChatApi.CreateConversationAsync(host.Admin);

        await ChatApi.SendAsync(host.Admin, conversation, "Hello");

        var turn = await WaitForTurnAsync(host.App, conversation);
        Assert.Equal(ActivityKind.Internal, turn.Kind);
        Assert.NotEqual(ActivityStatusCode.Error, turn.Status);
        Assert.Equal(AgentsTelemetry.StopCompleted, turn.GetTagItem(AgentsTelemetry.StopReasonTag));
        Assert.Equal(1, turn.GetTagItem(AgentsTelemetry.RoundsTag));
        Assert.Equal(0, turn.GetTagItem(AgentsTelemetry.ToolCallsTag));
        Assert.Equal(conversation.ToString(), turn.GetTagItem(QuorumActivityNames.ConversationIdTag)?.ToString());

        // The agent's span (invoke_agent) and the model call (chat) are both framework spans; the model call is the one with the usage and the first token.
        var model = Assert.Single(TraceOf(host.App, turn), s => s.GetTagItem("gen_ai.operation.name") as string == "chat");
        Assert.True(IsDescendantOf(model, turn));
        Assert.Equal(ModelName, model.GetTagItem("gen_ai.request.model"));
        Assert.Equal<object?>(FakeUsage.PromptTokens, model.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal<object?>(FakeUsage.CompletionTokens, model.GetTagItem("gen_ai.usage.output_tokens"));
        Assert.Contains(model.Events, e => e.Name == AgentsTelemetry.FirstTokenEvent);
    }

    [Fact] // AC-6
    public async Task WithContentCaptureOffNoSpanHoldsThePromptOrTheAnswer()
    {
        var conversation = await ChatApi.CreateConversationAsync(host.Admin);

        await ChatApi.SendAsync(host.Admin, conversation, "Telemetry probe question");

        var turn = await WaitForTurnAsync(host.App, conversation);
        var spans = TraceOf(host.App, turn);
        Assert.NotEmpty(spans);
        foreach (var span in spans)
        {
            foreach (var tag in span.TagObjects)
            {
                Assert.DoesNotContain("probe question", tag.Value?.ToString() ?? string.Empty, StringComparison.Ordinal);
                Assert.DoesNotContain("from the model", tag.Value?.ToString() ?? string.Empty, StringComparison.Ordinal);
            }

            foreach (var evt in span.Events)
            {
                foreach (var tag in evt.Tags)
                {
                    Assert.DoesNotContain("probe question", tag.Value?.ToString() ?? string.Empty, StringComparison.Ordinal);
                    Assert.DoesNotContain("from the model", tag.Value?.ToString() ?? string.Empty, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact] // Span names of the frameworks under the turn: the first check of PR 2 (plan risk: execute_tool).
    public async Task AToolCallIsAFrameworkSpanUnderTheTurn()
    {
        host.App.ModelServer.EnqueueRounds(
            ChatScript.CallTools(new FakeToolCall("calculator", "{\"expression\":\"2+2\"}", "call_1")),
            ChatScript.Reply("Four."));
        var conversation = await ChatApi.CreateConversationAsync(host.Admin);

        await ChatApi.SendAsync(host.Admin, conversation, "Compute");

        var turn = await WaitForTurnAsync(host.App, conversation);
        var tool = Assert.Single(ToolSpansOf(host.App, turn));
        Assert.True(IsDescendantOf(tool, turn));
        Assert.Equal(1, turn.GetTagItem(AgentsTelemetry.ToolCallsTag));
    }

    [Fact] // AC-9
    public async Task AnAnswerStoppedByTheRoundLimitIsInErrorWithTheLimitAsItsRounds()
    {
        await ToolFixtures.SetLimitsAsync(host.Admin, maxRounds: 2);
        host.App.ModelServer.Chat = ChatScript.CallTools(new FakeToolCall("calculator", "{\"expression\":\"1+1\"}", "call_1"));
        var conversation = await ChatApi.CreateConversationAsync(host.Admin);

        await ChatApi.SendAsync(host.Admin, conversation, "Loop");

        var turn = await WaitForTurnAsync(host.App, conversation);
        Assert.Equal(ActivityStatusCode.Error, turn.Status);
        Assert.Equal(AgentsTelemetry.StopRounds, turn.GetTagItem(AgentsTelemetry.StopReasonTag));
        Assert.Equal(2, turn.GetTagItem(AgentsTelemetry.RoundsTag));
    }

    [Fact] // AC-8
    public async Task AnAnswerCancelledByTheUserHasNoErrorStatus()
    {
        host.App.ModelServer.Chat = ChatScript.Slow(200, TimeSpan.FromMilliseconds(20));

        // The answer is driven directly: the test server does not report a client that goes away, so the user's stop is the cancellation token.
        await using var scope = TenantScope.Open(host.App, await TenantScope.TenantIdAsync(host.App));
        var chat = scope.ServiceProvider.GetRequiredService<IAssistantChat>();
        var assistantId = await scope.ServiceProvider.GetRequiredService<IAssistantCatalog>().GetDefaultAssistantIdAsync(Ct)
            ?? throw new InvalidOperationException("No assistant.");
        var conversation = Guid.NewGuid();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var started = await chat.StartAnswerAsync(assistantId, conversation, [new ChatTurn(ChatTurnRole.User, "Stop me")], [], stop.Token);
        Assert.True(started.IsSuccess);

        var received = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in started.Value.WithCancellation(stop.Token))
            {
                // The user stops the answer once it is under way.
                if (++received == 1)
                {
                    await stop.CancelAsync();
                }
            }
        });

        var turn = await WaitForTurnAsync(host.App, conversation);
        Assert.Equal(AgentsTelemetry.StopCancelled, turn.GetTagItem(AgentsTelemetry.StopReasonTag));
        Assert.NotEqual(ActivityStatusCode.Error, turn.Status);
    }

    [Fact] // spec 016 D-8: a server that does not know stream_options is asked again without it
    public async Task AServerThatRefusesStreamOptionsIsAskedAgainWithoutThem()
    {
        try
        {
            host.App.ModelServer.EnqueueRounds(ChatScript.Reply("refused") with { RejectsStreamOptions = true }, ChatScript.Reply("Fine."));
            var conversation = await ChatApi.CreateConversationAsync(host.Admin);

            var events = await ChatApi.SendAsync(host.Admin, conversation, "Hello");

            Assert.Equal("Completed", events[^1].Data.GetProperty("status").GetString());
            var requests = host.App.ModelServer.ChatRequests;
            Assert.Equal(2, requests.Count);
            Assert.True(requests[0].TryGetProperty("stream_options", out _));
            Assert.False(requests[1].TryGetProperty("stream_options", out _));
        }
        finally
        {
            OpenAiChatClient.ForgetUnsupportedUsage();
        }
    }
}
