using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.Tools;
using Quorum.Modules.Agents.Telemetry;
using static Quorum.Integration.Tests.Fixtures.FakeModelServer;
using static Quorum.Integration.Tests.Telemetry.SpanTestHelpers;

namespace Quorum.Integration.Tests.Telemetry;

/// <summary>
/// The tool spans and the audit link that depend on how the app starts: no export, a trace link template, content capture, and a
/// clock the test moves (spec 016 D-2, D-14, D-20, D-21). Each test keeps an app of its own.
/// </summary>
[Collection("Telemetry")]
public sealed class ToolSpanConfigurationTests(PostgresFixture postgres)
{
    private static readonly Uri AuditLog = new("/api/tools/audit", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    [Fact] // AC-12
    public async Task AToolThatTimesOutHasAnErrorSpanWithTheQuorumCodeAndTheTurnCompletes()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, clock, telemetry: true);
        using var client = await app.SetUpAsync();
        await ToolFixtures.SetLimitsAsync(client, callSeconds: 5);
        await ToolFixtures.AddProbeAsync(app);
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

        var turn = await WaitForTurnAsync(app, conversation);
        var tool = Assert.Single(ToolSpansOf(app, turn));
        Assert.Equal("TimedOut", tool.GetTagItem(AgentsTelemetry.ToolOutcomeTag));
        Assert.Equal(ActivityStatusCode.Error, tool.Status);
        Assert.Equal("tool.timed_out", tool.GetTagItem(AgentsTelemetry.ErrorCodeTag));
        Assert.NotEqual(ActivityStatusCode.Error, turn.Status);
        Assert.Equal(AgentsTelemetry.StopCompleted, turn.GetTagItem(AgentsTelemetry.StopReasonTag));
    }

    [Fact] // AC-20
    public async Task WithATemplateTheAuditEntryHasItsTraceLink()
    {
        const string template = "https://grafana.example.test/explore?traceId={traceId}&from={from}&to={to}";
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, telemetry: true, traceUrlTemplate: template);
        using var client = await app.SetUpAsync();
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Calculate("3+3")), ChatScript.Reply("Six."));
        var conversation = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, conversation, "Compute");

        var entry = Assert.Single(await AuditItemsAsync(client));
        var traceId = entry.GetProperty("traceId").GetString();
        Assert.NotNull(traceId);
        Assert.Matches($@"^https://grafana\.example\.test/explore\?traceId={traceId}&from=\d{{16}}&to=\d{{16}}$", entry.GetProperty("traceUrl").GetString());
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

    [Fact] // AC-7
    public async Task WithContentCaptureOnToolArgumentsAndResultsAreRedactedOnTheSpans()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, telemetry: true, captureContent: true);
        using var client = await app.SetUpAsync();
        await ToolFixtures.AddProbeAsync(app);
        app.Probe.Handler = (_, _) => Task.FromResult("{\"api_key\":\"resultsecret999\",\"note\":\"fine\"}");
        app.ModelServer.EnqueueRounds(
            ChatScript.CallTools(new FakeToolCall("probe", "{\"text\":\"hello\",\"token\":\"argsecret123\"}", "call_1")),
            ChatScript.Reply("Done."));
        var conversation = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, conversation, "Go");

        var turn = await WaitForTurnAsync(app, conversation);
        var tool = Assert.Single(ToolSpansOf(app, turn));
        Assert.Equal("Succeeded", tool.GetTagItem(AgentsTelemetry.ToolOutcomeTag));
        Assert.Contains("[redacted]", tool.GetTagItem("gen_ai.tool.call.arguments")?.ToString(), StringComparison.Ordinal);

        // Nowhere on any span of the trace, the arguments and the result of the tool, nor the history that carries them again.
        foreach (var span in TraceOf(app, turn))
        {
            foreach (var tag in span.TagObjects)
            {
                var value = tag.Value?.ToString() ?? string.Empty;
                Assert.DoesNotContain("argsecret123", value, StringComparison.Ordinal);
                Assert.DoesNotContain("resultsecret999", value, StringComparison.Ordinal);
            }
        }
    }

    private static FakeToolCall Calculate(string expression, string id = "call_1") =>
        new("calculator", JsonSerializer.Serialize(new { expression }), id);

    private static async Task<List<JsonElement>> AuditItemsAsync(HttpClient client)
    {
        var page = await client.GetFromJsonAsync<JsonElement>(AuditLog, Ct);
        return page.GetProperty("items").EnumerateArray().ToList();
    }
}
