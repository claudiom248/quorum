using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.Tools;
using Quorum.Modules.Agents.Telemetry;
using static Quorum.Integration.Tests.Fixtures.FakeModelServer;
using static Quorum.Integration.Tests.Telemetry.SpanTestHelpers;

namespace Quorum.Integration.Tests.Telemetry;

/// <summary>
/// The turn that ends by the total time (spec 016 D-13, D-14). The test moves the clock, which is fixed when the app starts, so it
/// keeps an app of its own.
/// </summary>
[Collection("Telemetry")]
public sealed class ChatTurnTimeTests(PostgresFixture postgres)
{
    [Fact] // AC-10
    public async Task AnAnswerStoppedByTheTotalTimeIsInError()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, clock, telemetry: true);
        using var client = await app.SetUpAsync();
        await ToolFixtures.SetLimitsAsync(client, totalSeconds: 10, callSeconds: 60);
        await ToolFixtures.AddProbeAsync(app);
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

        var turn = await WaitForTurnAsync(app, conversation);
        Assert.Equal(ActivityStatusCode.Error, turn.Status);
        Assert.Equal(AgentsTelemetry.StopTotalTime, turn.GetTagItem(AgentsTelemetry.StopReasonTag));
    }
}
