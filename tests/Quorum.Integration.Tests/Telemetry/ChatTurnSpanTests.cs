using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.Tools;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Agents.Telemetry;
using Quorum.Shared.Telemetry;
using static Quorum.Integration.Tests.Fixtures.FakeModelServer;

namespace Quorum.Integration.Tests.Telemetry;

/// <summary>The root span of a chat turn, the framework spans under it, and how the turn ended (spec 016 D-7 to D-9, D-13, D-14).</summary>
[Collection("Telemetry")]
public sealed class ChatTurnSpanTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string ModelName = "llama3.2";

    [Fact] // AC-2
    public async Task AnAnswerWithOneModelCallIsOneTraceWithItsModelSpanAndUsage()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, telemetry: true);
        using var client = await app.SetUpAsync(toolsActive: false);
        app.Spans.Clear();
        var conversation = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, conversation, "Hello");

        var turn = await WaitForSpanAsync(app, IsTurn);
        Assert.Equal(ActivityKind.Internal, turn.Kind);
        Assert.NotEqual(ActivityStatusCode.Error, turn.Status);
        Assert.Equal(AgentsTelemetry.StopCompleted, turn.GetTagItem(AgentsTelemetry.StopReasonTag));
        Assert.Equal(1, turn.GetTagItem(AgentsTelemetry.RoundsTag));
        Assert.Equal(0, turn.GetTagItem(AgentsTelemetry.ToolCallsTag));
        Assert.Equal(conversation.ToString(), turn.GetTagItem(QuorumActivityNames.ConversationIdTag)?.ToString());

        // The agent's span (invoke_agent) and the model call (chat) are both framework spans; the model call is the one with the usage and the first token.
        var model = await WaitForSpanAsync(app, s => s.GetTagItem("gen_ai.operation.name") as string == "chat");
        Assert.Equal(turn.TraceId, model.TraceId);
        Assert.True(IsDescendantOf(model, turn));
        Assert.Equal(ModelName, model.GetTagItem("gen_ai.request.model"));
        Assert.Equal<object?>(FakeUsage.PromptTokens, model.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal<object?>(FakeUsage.CompletionTokens, model.GetTagItem("gen_ai.usage.output_tokens"));
        Assert.Contains(model.Events, e => e.Name == AgentsTelemetry.FirstTokenEvent);
    }

    [Fact] // AC-6
    public async Task WithContentCaptureOffNoSpanHoldsThePromptOrTheAnswer()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, telemetry: true);
        using var client = await app.SetUpAsync(toolsActive: false);
        app.Spans.Clear();
        var conversation = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, conversation, "Telemetry probe question");
        await WaitForSpanAsync(app, IsTurn);

        var spans = app.Spans.Snapshot();
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
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, telemetry: true);
        using var client = await app.SetUpAsync();
        app.Spans.Clear();
        app.ModelServer.EnqueueRounds(
            ChatScript.CallTools(new FakeToolCall("calculator", "{\"expression\":\"2+2\"}", "call_1")),
            ChatScript.Reply("Four."));
        var conversation = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, conversation, "Compute");

        var turn = await WaitForSpanAsync(app, IsTurn);
        var tool = await WaitForSpanAsync(app, s => s.OperationName.StartsWith("execute_tool", StringComparison.Ordinal));
        Assert.True(IsDescendantOf(tool, turn));
        Assert.Equal(1, turn.GetTagItem(AgentsTelemetry.ToolCallsTag));
    }

    [Fact] // AC-9
    public async Task AnAnswerStoppedByTheRoundLimitIsInErrorWithTheLimitAsItsRounds()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, telemetry: true);
        using var client = await app.SetUpAsync();
        await SetLimitsAsync(client, maxRounds: 2, totalSeconds: 300);
        app.Spans.Clear();
        app.ModelServer.Chat = ChatScript.CallTools(new FakeToolCall("calculator", "{\"expression\":\"1+1\"}", "call_1"));
        var conversation = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, conversation, "Loop");

        var turn = await WaitForSpanAsync(app, IsTurn);
        Assert.Equal(ActivityStatusCode.Error, turn.Status);
        Assert.Equal(AgentsTelemetry.StopRounds, turn.GetTagItem(AgentsTelemetry.StopReasonTag));
        Assert.Equal(2, turn.GetTagItem(AgentsTelemetry.RoundsTag));
    }

    [Fact] // AC-10
    public async Task AnAnswerStoppedByTheTotalTimeIsInError()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, clock, telemetry: true);
        using var client = await app.SetUpAsync();
        await SetLimitsAsync(client, maxRounds: 8, totalSeconds: 10, callSeconds: 60);
        await ToolFixtures.AddProbeAsync(app);
        app.Spans.Clear();
        app.Probe.Handler = async (_, token) =>
        {
            clock.Advance(TimeSpan.FromSeconds(11));
            await Task.Delay(Timeout.InfiniteTimeSpan, clock, token);
            return "never";
        };
        app.ModelServer.EnqueueRounds(
            ChatScript.Reply("Working. ") with { ToolCalls = [new FakeToolCall("probe", "{\"text\":\"slow\"}", "call_1")] },
            ChatScript.Reply("Should not be asked."));
        var conversation = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, conversation, "Go");

        var turn = await WaitForSpanAsync(app, IsTurn);
        Assert.Equal(ActivityStatusCode.Error, turn.Status);
        Assert.Equal(AgentsTelemetry.StopTotalTime, turn.GetTagItem(AgentsTelemetry.StopReasonTag));
    }

    [Fact] // AC-8
    public async Task AnAnswerCancelledByTheUserHasNoErrorStatus()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, telemetry: true);
        using var client = await app.SetUpAsync(toolsActive: false);
        app.ModelServer.Chat = ChatScript.Slow(200, TimeSpan.FromMilliseconds(20));
        app.Spans.Clear();

        // The answer is driven directly: the test server does not report a client that goes away, so the user's stop is the cancellation token.
        await using var scope = TenantScope.Open(app, await TenantScope.TenantIdAsync(app));
        var chat = scope.ServiceProvider.GetRequiredService<IAssistantChat>();
        var assistantId = await scope.ServiceProvider.GetRequiredService<IAssistantCatalog>().GetDefaultAssistantIdAsync(Ct)
            ?? throw new InvalidOperationException("No assistant.");

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var started = await chat.StartAnswerAsync(assistantId, Guid.NewGuid(), [new ChatTurn(ChatTurnRole.User, "Stop me")], [], stop.Token);
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

        var turn = await WaitForSpanAsync(app, s => IsTurn(s) && s.GetTagItem(AgentsTelemetry.StopReasonTag) is not null);
        Assert.Equal(AgentsTelemetry.StopCancelled, turn.GetTagItem(AgentsTelemetry.StopReasonTag));
        Assert.NotEqual(ActivityStatusCode.Error, turn.Status);
    }

    private static bool IsTurn(Activity span) => span.OperationName == AgentsTelemetry.TurnActivityName;

    /// <summary>Whether <paramref name="span"/> is under <paramref name="ancestor"/> in the tree of spans.</summary>
    private static bool IsDescendantOf(Activity span, Activity ancestor)
    {
        for (var parent = span.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent.Id == ancestor.Id)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task SetLimitsAsync(HttpClient client, int maxRounds, int totalSeconds, int callSeconds = 30)
    {
        using var response = await client.PutAsJsonAsync(
            ToolsApi.ToolSettings,
            new
            {
                maxRounds,
                totalTimeoutSeconds = totalSeconds,
                callTimeoutSeconds = callSeconds,
                confirmationTimeoutSeconds = 300,
                maxResultCharacters = 20_000,
                auditRetentionDays = 90,
            },
            Ct);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<Activity> WaitForSpanAsync(QuorumApp app, Func<Activity, bool> match)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (true)
        {
            var found = app.Spans.Snapshot().LastOrDefault(match);
            if (found is not null)
            {
                return found;
            }

            Assert.True(DateTimeOffset.UtcNow < deadline, "No matching span within 30 seconds.");
            await Task.Delay(20, Ct);
        }
    }
}
