using System.Diagnostics;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.Agents.Telemetry;
using Quorum.Shared.Telemetry;

namespace Quorum.Integration.Tests.Telemetry;

/// <summary>What the tests of spans share: waiting for spans and telling the ones of a test from those of the tests before it.</summary>
internal static class SpanTestHelpers
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<List<Activity>> WaitForSpansAsync(QuorumApp app, int count, Func<Activity, bool> match)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (true)
        {
            var found = app.Spans.Snapshot().Where(match).ToList();
            if (found.Count >= count)
            {
                return found;
            }

            Assert.True(DateTimeOffset.UtcNow < deadline, $"Fewer than {count} matching spans within 30 seconds.");
            await Task.Delay(20, Ct);
        }
    }

    public static async Task<Activity> WaitForSpanAsync(QuorumApp app, Func<Activity, bool> match) =>
        (await WaitForSpansAsync(app, 1, match)).Last();

    /// <summary>
    /// The root span of the turn of this conversation. An app shared by several tests may still receive the late spans of the test
    /// before, so a test finds its spans by its conversation and then by the trace, never by a name alone.
    /// </summary>
    public static Task<Activity> WaitForTurnAsync(QuorumApp app, Guid conversationId) =>
        WaitForSpanAsync(
            app,
            span => span.OperationName == AgentsTelemetry.TurnActivityName
                && span.GetTagItem(QuorumActivityNames.ConversationIdTag)?.ToString() == conversationId.ToString());

    /// <summary>The spans of the trace of <paramref name="turn"/>.</summary>
    public static List<Activity> TraceOf(QuorumApp app, Activity turn) =>
        [.. app.Spans.Snapshot().Where(span => span.TraceId == turn.TraceId)];

    public static List<Activity> ToolSpansOf(QuorumApp app, Activity turn) =>
        [.. TraceOf(app, turn).Where(span => span.OperationName.StartsWith("execute_tool", StringComparison.Ordinal))];

    /// <summary>Whether <paramref name="span"/> is under <paramref name="ancestor"/> in the tree of spans.</summary>
    public static bool IsDescendantOf(Activity span, Activity ancestor)
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
}
