using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.Tools;
using Quorum.Modules.Agents.Telemetry;
using Quorum.Shared.Telemetry;
using static Quorum.Integration.Tests.Fixtures.FakeModelServer;

namespace Quorum.Integration.Tests.Telemetry;

/// <summary>The spans of tool calls and the trace id on their audit entries (spec 016 D-15, D-17, D-20, D-21, D-14).</summary>
public sealed class ToolSpanTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Uri AuditLog = new("/api/tools/audit", UriKind.Relative);

    [Fact] // AC-1
    public async Task WithoutExportAnAuditEntryHasNoTraceId()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Calculate("1+1")), ChatScript.Reply("Done."));
        var conversation = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, conversation, "Compute");

        var entry = Assert.Single(await AuditItemsAsync(client));
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("traceId").ValueKind);
    }

    [Fact] // AC-3
    public async Task AToolCallSpanCarriesItsOriginAndRoundAndTheAuditEntryHasTheTrace()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, telemetry: true);
        using var client = await app.SetUpAsync();
        app.Spans.Clear();
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Calculate("2+2")), ChatScript.Reply("Four."));
        var conversation = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, conversation, "Compute");

        var turn = await WaitForSpanAsync(app, s => s.OperationName == AgentsTelemetry.TurnActivityName);
        var tool = await WaitForSpanAsync(app, s => s.OperationName.StartsWith("execute_tool", StringComparison.Ordinal));
        Assert.Equal("builtin", tool.GetTagItem(AgentsTelemetry.ToolOriginTag));
        Assert.Equal<object?>(1, tool.GetTagItem(AgentsTelemetry.ToolRoundTag));
        Assert.Equal("Auto", tool.GetTagItem(AgentsTelemetry.ToolDecisionTag));
        Assert.Equal("Succeeded", tool.GetTagItem(AgentsTelemetry.ToolOutcomeTag));
        Assert.NotNull(tool.GetTagItem(AgentsTelemetry.ToolArgumentsHashTag));
        Assert.Equal(turn.TraceId, tool.TraceId);

        var entry = Assert.Single(await AuditItemsAsync(client));
        Assert.Equal(turn.TraceId.ToHexString(), entry.GetProperty("traceId").GetString());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("traceUrl").ValueKind);
    }

    [Fact] // AC-11
    public async Task TheSameCallRepeatedInTheTurnHasTheSameHashOnDifferentRounds()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, telemetry: true);
        using var client = await app.SetUpAsync();
        app.Spans.Clear();
        app.ModelServer.EnqueueRounds(
            ChatScript.CallTools(Calculate("1+1")),
            ChatScript.CallTools(Calculate("1+1")),
            ChatScript.CallTools(Calculate("1+1")),
            ChatScript.Reply("Done."));
        var conversation = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, conversation, "Loop");

        await WaitForSpanAsync(app, s => s.OperationName == AgentsTelemetry.TurnActivityName);
        var tools = await WaitForSpansAsync(app, 3, s => s.OperationName.StartsWith("execute_tool", StringComparison.Ordinal));
        Assert.Equal(3, tools.Count);
        Assert.Single(tools.Select(t => t.GetTagItem(AgentsTelemetry.ToolArgumentsHashTag)).Distinct());
        Assert.Equal(3, tools.Select(t => t.GetTagItem(AgentsTelemetry.ToolRoundTag)).Distinct().Count());
        Assert.All(tools, t => Assert.DoesNotContain("1+1", t.TagObjects.Select(tag => tag.Value?.ToString() ?? string.Empty), StringComparer.Ordinal));
    }

    [Fact] // AC-12
    public async Task AToolThatTimesOutHasAnErrorSpanWithTheQuorumCodeAndTheTurnCompletes()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, clock, telemetry: true);
        using var client = await app.SetUpAsync();
        await SetLimitsAsync(client, callSeconds: 5);
        await ToolFixtures.AddProbeAsync(app);
        app.Spans.Clear();
        app.Probe.Handler = async (_, token) =>
        {
            clock.Advance(TimeSpan.FromSeconds(6));
            await Task.Delay(Timeout.InfiniteTimeSpan, clock, token);
            return "never";
        };
        app.ModelServer.EnqueueRounds(
            ChatScript.CallTools(new FakeToolCall("probe", "{\"text\":\"slow\"}", "call_1")),
            ChatScript.Reply("Moving on."));
        var conversation = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, conversation, "Go");

        var tool = await WaitForSpanAsync(app, s => s.OperationName.StartsWith("execute_tool", StringComparison.Ordinal) && s.GetTagItem(AgentsTelemetry.ToolOutcomeTag) as string == "TimedOut");
        Assert.Equal(ActivityStatusCode.Error, tool.Status);
        Assert.Equal("tool.timed_out", tool.GetTagItem(AgentsTelemetry.ErrorCodeTag));

        var turn = await WaitForSpanAsync(app, s => s.OperationName == AgentsTelemetry.TurnActivityName);
        Assert.NotEqual(ActivityStatusCode.Error, turn.Status);
        Assert.Equal(AgentsTelemetry.StopCompleted, turn.GetTagItem(AgentsTelemetry.StopReasonTag));
    }

    [Fact] // AC-20
    public async Task WithATemplateTheAuditEntryHasItsTraceLink()
    {
        const string template = "https://grafana.example.test/explore?traceId={traceId}";
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, telemetry: true, traceUrlTemplate: template);
        using var client = await app.SetUpAsync();
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Calculate("3+3")), ChatScript.Reply("Six."));
        var conversation = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, conversation, "Compute");

        var entry = Assert.Single(await AuditItemsAsync(client));
        var traceId = entry.GetProperty("traceId").GetString();
        Assert.NotNull(traceId);
        Assert.Equal($"https://grafana.example.test/explore?traceId={traceId}", entry.GetProperty("traceUrl").GetString());
    }

    [Fact] // AC-23
    public async Task ABrokenTemplateIsIgnoredAndNamedOnceWithoutItsText()
    {
        const string template = "ftp://secret-token@tracing.example.test/{traceId}";
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, telemetry: true, traceUrlTemplate: template);
        _ = app.Server;

        var warnings = app.Logs.Messages.Where(m => m.Contains("TraceUrlTemplate", StringComparison.Ordinal)).ToList();
        Assert.Single(warnings);
        Assert.Contains("does not use the http or https scheme", warnings[0], StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", warnings[0], StringComparison.Ordinal);
        Assert.DoesNotContain("tracing.example.test", warnings[0], StringComparison.Ordinal);
    }

    [Fact] // AC-24
    public async Task WithExportOffABrokenTemplateLogsNothing()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, traceUrlTemplate: "not a url");
        _ = app.Server;

        Assert.DoesNotContain(app.Logs.Messages, m => m.Contains("TraceUrlTemplate", StringComparison.Ordinal));
    }

    private static FakeToolCall Calculate(string expression, string id = "call_1") =>
        new("calculator", JsonSerializer.Serialize(new { expression }), id);

    private static async Task<List<JsonElement>> AuditItemsAsync(HttpClient client)
    {
        var page = await client.GetFromJsonAsync<JsonElement>(AuditLog, Ct);
        return page.GetProperty("items").EnumerateArray().ToList();
    }

    private static async Task SetLimitsAsync(HttpClient client, int callSeconds)
    {
        using var response = await client.PutAsJsonAsync(
            ToolsApi.ToolSettings,
            new
            {
                maxRounds = 8,
                totalTimeoutSeconds = 300,
                callTimeoutSeconds = callSeconds,
                confirmationTimeoutSeconds = 300,
                maxResultCharacters = 20_000,
                auditRetentionDays = 90,
            },
            Ct);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<Activity> WaitForSpanAsync(QuorumApp app, Func<Activity, bool> match) =>
        (await WaitForSpansAsync(app, 1, match)).Last();

    private static async Task<List<Activity>> WaitForSpansAsync(QuorumApp app, int count, Func<Activity, bool> match)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (true)
        {
            var found = app.Spans.Snapshot().Where(match).ToList();
            if (found.Count >= count)
            {
                return found;
            }

            Assert.True(DateTimeOffset.UtcNow < deadline, $"Fewer than {count} matching spans within 10 seconds.");
            await Task.Delay(20, Ct);
        }
    }
}
