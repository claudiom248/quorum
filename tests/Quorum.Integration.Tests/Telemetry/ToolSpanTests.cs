using System.Net.Http.Json;
using System.Text.Json;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.KnowledgeBases.Lookups;
using Quorum.Integration.Tests.LookupTools;
using Quorum.Integration.Tests.Tools;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Telemetry;
using static Quorum.Integration.Tests.Fixtures.FakeModelServer;
using static Quorum.Integration.Tests.Telemetry.SpanTestHelpers;

namespace Quorum.Integration.Tests.Telemetry;

/// <summary>
/// The spans of tool calls and the trace id on their audit entries (spec 016 D-15, D-17, D-20, D-14). One app with the export on
/// serves all the tests; the tests that need another start (a clock, a template, content capture) are in <see cref="ToolSpanConfigurationTests"/>.
/// </summary>
[Collection("Telemetry")]
public sealed class ToolSpanTests(SharedTelemetryApp host) : IClassFixture<SharedTelemetryApp>, IAsyncLifetime
{
    private static readonly Uri AuditLog = new("/api/tools/audit", UriKind.Relative);

    public ValueTask InitializeAsync() => new(host.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-3
    public async Task AToolCallSpanCarriesItsOriginAndRoundAndTheAuditEntryHasTheTrace()
    {
        host.App.ModelServer.EnqueueRounds(ChatScript.CallTools(Calculate("2+2")), ChatScript.Reply("Four."));
        var conversation = await ChatApi.CreateConversationAsync(host.Admin);

        await ChatApi.SendAsync(host.Admin, conversation, "Compute");

        var turn = await WaitForTurnAsync(host.App, conversation);
        var tool = Assert.Single(ToolSpansOf(host.App, turn));
        Assert.Equal("builtin", tool.GetTagItem(AgentsTelemetry.ToolOriginTag));
        Assert.Equal<object?>(1, tool.GetTagItem(AgentsTelemetry.ToolRoundTag));
        Assert.Equal("Auto", tool.GetTagItem(AgentsTelemetry.ToolDecisionTag));
        Assert.Equal("Succeeded", tool.GetTagItem(AgentsTelemetry.ToolOutcomeTag));
        Assert.NotNull(tool.GetTagItem(AgentsTelemetry.ToolArgumentsHashTag));

        var entry = Assert.Single(await AuditItemsAsync(host.Admin));
        Assert.Equal(turn.TraceId.ToHexString(), entry.GetProperty("traceId").GetString());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("traceUrl").ValueKind);
    }

    [Fact] // spec 016 D-15: a lookup runs in-process and carries an audit server name, but its span names the kind of tool
    public async Task ALookupToolCallSpanCarriesTheLookupOrigin()
    {
        var lookup = await LookupApi.CreateManualOkAsync(host.Admin, LookupApi.ManualBody());
        await LookupToolApi.CreateOkAsync(host.Admin, LookupToolApi.Body(lookups: [LookupToolApi.Bind(lookup)]));
        await ToolFixtures.ActivateOnlyAsync(host.Admin, "lists");
        host.App.ModelServer.EnqueueRounds(ChatScript.CallTools(new FakeToolCall("lists_entries", "{}", "call_1")), ChatScript.Reply("Done."));
        var conversation = await ChatApi.CreateConversationAsync(host.Admin);

        await ChatApi.SendAsync(host.Admin, conversation, "Which values?");

        var turn = await WaitForTurnAsync(host.App, conversation);
        var tool = Assert.Single(ToolSpansOf(host.App, turn));
        Assert.Equal("lookup", tool.GetTagItem(AgentsTelemetry.ToolOriginTag));
    }

    [Fact] // AC-11
    public async Task TheSameCallRepeatedInTheTurnHasTheSameHashOnDifferentRounds()
    {
        host.App.ModelServer.EnqueueRounds(
            ChatScript.CallTools(Calculate("1+1")),
            ChatScript.CallTools(Calculate("1+1")),
            ChatScript.CallTools(Calculate("1+1")),
            ChatScript.Reply("Done."));
        var conversation = await ChatApi.CreateConversationAsync(host.Admin);

        await ChatApi.SendAsync(host.Admin, conversation, "Loop");

        var turn = await WaitForTurnAsync(host.App, conversation);
        var tools = ToolSpansOf(host.App, turn);
        Assert.Equal(3, tools.Count);
        Assert.Single(tools.Select(t => t.GetTagItem(AgentsTelemetry.ToolArgumentsHashTag)).Distinct());
        Assert.Equal(3, tools.Select(t => t.GetTagItem(AgentsTelemetry.ToolRoundTag)).Distinct().Count());
        Assert.All(tools, t => Assert.DoesNotContain("1+1", t.TagObjects.Select(tag => tag.Value?.ToString() ?? string.Empty), StringComparer.Ordinal));
    }

    [Fact] // AC-14
    public async Task TheWaitForAConfirmationIsAChildSpanOfTheToolCallWithItsDecision()
    {
        await ToolFixtures.AddProbeAsync(host.App, ToolPolicy.Confirm);
        host.App.ModelServer.EnqueueRounds(
            ChatScript.CallTools(new FakeToolCall("probe", "{\"text\":\"danger\"}", "call_1")),
            ChatScript.Reply("Finished."));
        var conversation = await ChatApi.CreateConversationAsync(host.Admin);
        await using var answer = await LiveAnswer.StartAsync(host.Admin, conversation, "Go");
        var callId = Guid.Parse((await answer.UntilAsync("tool_call")).Data.GetProperty("callId").GetString()!);

        using var decision = await ChatApi.DecideAsync(host.Admin, conversation, callId, approved: true);
        await answer.FinishAsync();

        var turn = await WaitForTurnAsync(host.App, conversation);
        var tool = Assert.Single(ToolSpansOf(host.App, turn));
        var confirmation = Assert.Single(TraceOf(host.App, turn), s => s.OperationName == AgentsTelemetry.ConfirmationActivityName);
        Assert.Equal("approved", confirmation.GetTagItem(AgentsTelemetry.ConfirmationDecisionTag));
        Assert.Equal(tool.Id, confirmation.Parent?.Id);
    }

    private static FakeToolCall Calculate(string expression, string id = "call_1") =>
        new("calculator", JsonSerializer.Serialize(new { expression }), id);

    private static async Task<List<JsonElement>> AuditItemsAsync(HttpClient client)
    {
        var page = await client.GetFromJsonAsync<JsonElement>(AuditLog, Ct);
        return page.GetProperty("items").EnumerateArray().ToList();
    }
}
