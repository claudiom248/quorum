using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.Agents.Domain;
using static Quorum.Integration.Tests.Fixtures.FakeModelServer;

namespace Quorum.Integration.Tests.Tools;

/// <summary>Tools that wait for the user: the confirmation of a call and the questions of the model (spec 004, AC-32 to AC-40).</summary>
public sealed class ToolConfirmationTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FakeToolCall Probe(string text, string id = "call_1") =>
        new("native__probe", JsonSerializer.Serialize(new { text }), id);

    private static FakeToolCall Ask(string question, string id = "call_1") =>
        new("native__ask_user", JsonSerializer.Serialize(new { question }), id);

    private static List<JsonElement> Messages(QuorumApp app, int request) =>
        app.ModelServer.ChatRequests[request].GetProperty("messages").EnumerateArray().ToList();

    private static Guid CallId(SseEvent toolCall) => Guid.Parse(toolCall.Data.GetProperty("callId").GetString()!);

    private static async Task<(QuorumApp App, HttpClient Client, Guid ConversationId)> StartConfirmationAsync(
        PostgresFixture postgres,
        FakeTimeProvider? clock = null,
        int confirmationSeconds = 300,
        int totalSeconds = 300)
    {
        var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, clock);
        var client = await app.SetUpAsync();
        await ToolFixtures.SetLimitsAsync(client, confirmationSeconds: confirmationSeconds, totalSeconds: totalSeconds);
        await ToolFixtures.AddProbeAsync(app, ToolPolicy.Confirm);
        app.ModelServer.EnqueueRounds(ChatScript.Reply("I need to run it. ") with { ToolCalls = [Probe("danger")] }, ChatScript.Reply("Finished."));
        return (app, client, await ChatApi.CreateConversationAsync(client));
    }

    [Fact] // AC-32
    public async Task AConfirmCallIsAnnouncedWithItsExpiryAndNothingRunsUntilTheUserDecides()
    {
        var (app, client, id) = await StartConfirmationAsync(postgres);
        await using var _ = app;
        using var __ = client;
        await using var answer = await LiveAnswer.StartAsync(client, id, "Go");

        var call = await answer.UntilAsync("tool_call");

        Assert.True(call.Data.GetProperty("requiresConfirmation").GetBoolean());
        Assert.Equal("native__probe", call.Data.GetProperty("tool").GetString());
        Assert.Equal("danger", call.Data.GetProperty("arguments").GetProperty("text").GetString());
        Assert.True(call.Data.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);
        Assert.Null(await answer.NextAsync(TimeSpan.FromMilliseconds(300)));
        Assert.Empty(app.Probe.Calls);
        Assert.Empty(await ToolFixtures.AuditRowsAsync(app));
    }

    [Fact] // AC-33
    public async Task ApprovingTheCallRunsTheToolAndTheAnswerContinues()
    {
        var (app, client, id) = await StartConfirmationAsync(postgres);
        await using var _ = app;
        using var __ = client;
        await using var answer = await LiveAnswer.StartAsync(client, id, "Go");
        var callId = CallId(await answer.UntilAsync("tool_call"));

        using var decision = await ChatApi.DecideAsync(client, id, callId, approved: true);
        var events = await answer.FinishAsync();

        Assert.Equal(HttpStatusCode.NoContent, decision.StatusCode);
        Assert.Equal(["probe result"], events.Where(e => e.Type == "tool_result").Select(e => e.Data.GetProperty("content").GetString()));
        Assert.Equal("succeeded", Assert.Single(events, e => e.Type == "tool_result").Data.GetProperty("status").GetString());
        Assert.Equal("Completed", events[^1].Data.GetProperty("status").GetString());
        Assert.Equal("I need to run it. Finished.", SseReader.Text(events));
        Assert.Single(app.Probe.Calls);
        var audit = Assert.Single(await ToolFixtures.AuditRowsAsync(app));
        Assert.Equal(("Approved", "Succeeded", callId), (audit.Decision, audit.Outcome, audit.CallId));
    }

    [Fact] // AC-34
    public async Task DenyingTheCallTellsTheModelAndNothingRuns()
    {
        var (app, client, id) = await StartConfirmationAsync(postgres);
        await using var _ = app;
        using var __ = client;
        await using var answer = await LiveAnswer.StartAsync(client, id, "Go");
        var callId = CallId(await answer.UntilAsync("tool_call"));

        using var decision = await ChatApi.DecideAsync(client, id, callId, approved: false);
        var events = await answer.FinishAsync();

        Assert.Equal(HttpStatusCode.NoContent, decision.StatusCode);
        Assert.Equal("denied", Assert.Single(events, e => e.Type == "tool_result").Data.GetProperty("status").GetString());
        Assert.Contains("denied by the user", Messages(app, 1)[^1].GetProperty("content").GetString()!, StringComparison.Ordinal);
        Assert.Empty(app.Probe.Calls);
        Assert.Equal("Completed", events[^1].Data.GetProperty("status").GetString());
        var audit = Assert.Single(await ToolFixtures.AuditRowsAsync(app));
        Assert.Equal(("Denied", "Denied"), (audit.Decision, audit.Outcome));
    }

    [Theory] // D-9, D-17
    [InlineData("UPDATE agents.tools SET \"State\" = 'Disabled' WHERE \"Name\" = 'probe'")]
    [InlineData("DELETE FROM agents.assistant_tools WHERE \"ToolId\" IN (SELECT \"Id\" FROM agents.tools WHERE \"Name\" = 'probe')")]
    public async Task AToolThatStopsBeingAvailableWhileWaitingIsNotRunEvenWhenApproved(string change)
    {
        var (app, client, id) = await StartConfirmationAsync(postgres);
        await using var _ = app;
        using var __ = client;
        await using var answer = await LiveAnswer.StartAsync(client, id, "Go");
        var callId = CallId(await answer.UntilAsync("tool_call"));
        await ToolFixtures.ExecuteAsync(app, change);

        using var decision = await ChatApi.DecideAsync(client, id, callId, approved: true);
        var events = await answer.FinishAsync();

        Assert.Equal(HttpStatusCode.NoContent, decision.StatusCode);
        Assert.Empty(app.Probe.Calls);
        Assert.Equal("failed", Assert.Single(events, e => e.Type == "tool_result").Data.GetProperty("status").GetString());
        Assert.Contains("no longer available", Messages(app, 1)[^1].GetProperty("content").GetString()!, StringComparison.Ordinal);
        var audit = Assert.Single(await ToolFixtures.AuditRowsAsync(app));
        Assert.Equal(("Approved", "Failed", "tool.unavailable"), (audit.Decision, audit.Outcome, audit.ErrorCode));
    }

    [Fact] // AC-35
    public async Task WhenTheConfirmationTimeoutPassesItCountsAsDeniedAndNothingRuns()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (app, client, id) = await StartConfirmationAsync(postgres, clock, confirmationSeconds: 60);
        await using var _ = app;
        using var __ = client;
        await using var answer = await LiveAnswer.StartAsync(client, id, "Go");
        await answer.UntilAsync("tool_call");

        clock.Advance(TimeSpan.FromSeconds(61));
        var events = await answer.FinishAsync();

        Assert.Equal("timed_out", Assert.Single(events, e => e.Type == "tool_result").Data.GetProperty("status").GetString());
        Assert.Contains("denied by the user", Messages(app, 1)[^1].GetProperty("content").GetString()!, StringComparison.Ordinal);
        Assert.Empty(app.Probe.Calls);
        Assert.Equal("Completed", events[^1].Data.GetProperty("status").GetString());
        var audit = Assert.Single(await ToolFixtures.AuditRowsAsync(app));
        Assert.Equal(("TimedOut", "TimedOut"), (audit.Decision, audit.Outcome));
    }

    [Fact] // AC-35, AC-40
    public async Task ClosingTheStreamWhileWaitingCountsAsDeniedTheCallIsCancelledAndTheAnswerIsInterrupted()
    {
        var (app, client, id) = await StartConfirmationAsync(postgres);
        await using var _ = app;
        using var __ = client;
        var answer = await LiveAnswer.StartAsync(client, id, "Go");
        await answer.UntilAsync("tool_call");

        await answer.DisposeAsync();

        var conversation = await ChatApi.WaitForLastMessageAsync(client, id, "Interrupted");
        Assert.Equal("I need to run it. ", conversation.Messages[^1].Content);
        Assert.Empty(app.Probe.Calls);
        var audit = Assert.Single(await ToolFixtures.AuditRowsAsync(app));
        Assert.Equal(("Denied", "Cancelled"), (audit.Decision, audit.Outcome));
        Assert.Empty(app.Logs.ErrorsAndAbove);
    }

    [Fact] // AC-35
    public async Task DeletingTheConversationWhileWaitingCancelsTheCall()
    {
        var (app, client, id) = await StartConfirmationAsync(postgres);
        await using var _ = app;
        using var __ = client;
        await using var answer = await LiveAnswer.StartAsync(client, id, "Go");
        await answer.UntilAsync("tool_call");

        using var deletion = await client.DeleteAsync(ChatApi.Conversation(id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, deletion.StatusCode);
        Assert.Empty(app.Probe.Calls);
        Assert.Equal("Cancelled", Assert.Single(await ToolFixtures.AuditRowsAsync(app)).Outcome);
    }

    [Fact] // AC-36
    public async Task AnotherUserCannotDecideTheCallAndTheOwnersCallStaysPending()
    {
        var (app, client, id) = await StartConfirmationAsync(postgres);
        await using var _ = app;
        using var __ = client;
        using var member = await app.CreateMemberClientAsync();
        await using var answer = await LiveAnswer.StartAsync(client, id, "Go");
        var callId = CallId(await answer.UntilAsync("tool_call"));

        using var intruder = await ChatApi.DecideAsync(member, id, callId, approved: true);

        Assert.Equal(HttpStatusCode.NotFound, intruder.StatusCode);
        Assert.Empty(app.Probe.Calls);
        using var owner = await ChatApi.DecideAsync(client, id, callId, approved: false);
        Assert.Equal(HttpStatusCode.NoContent, owner.StatusCode);
        await answer.FinishAsync();
    }

    [Fact] // AC-36
    public async Task ASecondDecisionOnTheSameCallIsAConflict()
    {
        var (app, client, id) = await StartConfirmationAsync(postgres);
        await using var _ = app;
        using var __ = client;
        await using var answer = await LiveAnswer.StartAsync(client, id, "Go");
        var callId = CallId(await answer.UntilAsync("tool_call"));
        using var first = await ChatApi.DecideAsync(client, id, callId, approved: true);

        using var second = await ChatApi.DecideAsync(client, id, callId, approved: false);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        await answer.FinishAsync();
        Assert.Single(app.Probe.Calls);
        Assert.Equal("Approved", Assert.Single(await ToolFixtures.AuditRowsAsync(app)).Decision);
    }

    [Fact] // AC-36
    public async Task ADecisionOnAnUnknownCallOrOnAQuestionIsNotFound()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Ask("Which city?")), ChatScript.Reply("Thanks."));
        var id = await ChatApi.CreateConversationAsync(client);
        await using var answer = await LiveAnswer.StartAsync(client, id, "Go");
        var questionCallId = CallId(await answer.UntilAsync("tool_call"));

        using var unknown = await ChatApi.DecideAsync(client, id, Guid.NewGuid(), approved: true);
        using var wrongKind = await ChatApi.DecideAsync(client, id, questionCallId, approved: true);
        using var elsewhere = await ChatApi.DecideAsync(client, Guid.NewGuid(), questionCallId, approved: true);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongKind.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, elsewhere.StatusCode);
    }

    [Fact] // AC-36
    public async Task ADecisionAfterTheAnswerHasEndedIsAConflictNotAnUnknownCall()
    {
        var (app, client, id) = await StartConfirmationAsync(postgres);
        await using var _ = app;
        using var __ = client;
        await using var answer = await LiveAnswer.StartAsync(client, id, "Go");
        var callId = CallId(await answer.UntilAsync("tool_call"));
        using var decision = await ChatApi.DecideAsync(client, id, callId, approved: true);
        await answer.FinishAsync();
        await ChatApi.WaitForLastMessageAsync(client, id, "Completed");

        using var late = await ChatApi.DecideAsync(client, id, callId, approved: false);

        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
    }

    [Fact] // AC-35, AC-36
    public async Task ADecisionAfterTheTimeoutIsAConflictAndDoesNotRunTheTool()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (app, client, id) = await StartConfirmationAsync(postgres, clock, confirmationSeconds: 60);
        await using var _ = app;
        using var __ = client;
        await using var answer = await LiveAnswer.StartAsync(client, id, "Go");
        var callId = CallId(await answer.UntilAsync("tool_call"));
        clock.Advance(TimeSpan.FromSeconds(61));
        await answer.UntilAsync("tool_result");

        using var late = await ChatApi.DecideAsync(client, id, callId, approved: true);
        await answer.FinishAsync();

        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Empty(app.Probe.Calls);
    }

    [Fact]
    public async Task ADecisionWithoutAnApprovedValueIsRejected()
    {
        var (app, client, id) = await StartConfirmationAsync(postgres);
        await using var _ = app;
        using var __ = client;

        using var response = await client.PostAsJsonAsync(ChatApi.ToolDecision(id, Guid.NewGuid()), new { }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact] // AC-38
    public async Task ARegenerationAsksForConfirmationAgain()
    {
        var (app, client, id) = await StartConfirmationAsync(postgres);
        await using var _ = app;
        using var __ = client;
        await using (var first = await LiveAnswer.StartAsync(client, id, "Go"))
        {
            using var approval = await ChatApi.DecideAsync(client, id, CallId(await first.UntilAsync("tool_call")), approved: true);
            await first.FinishAsync();
        }

        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Probe("again")), ChatScript.Reply("Done again."));
        await using var second = await LiveAnswer.RegenerateAsync(client, id);
        var call = await second.UntilAsync("tool_call");

        Assert.True(call.Data.GetProperty("requiresConfirmation").GetBoolean());
        Assert.Single(app.Probe.Calls);
        using var denial = await ChatApi.DecideAsync(client, id, CallId(call), approved: false);
        await second.FinishAsync();
        Assert.Single(app.Probe.Calls);
    }

    [Fact] // AC-39
    public async Task AfterARestartNothingRanTheWaitingCallIsGoneAndTheAnswerIsStoredAsNotCompleted()
    {
        var (app, client, id) = await StartConfirmationAsync(postgres);
        using var __ = client;
        var answer = await LiveAnswer.StartAsync(client, id, "Go");
        var callId = CallId(await answer.UntilAsync("tool_call"));

        // The host stops while the answer waits: the connection goes, the answer is stored as interrupted and nothing in memory survives.
        await answer.DisposeAsync();
        var conversation = await ChatApi.WaitForLastMessageAsync(client, id, "Interrupted");
        await app.DisposeAsync();

        await using var restarted = app.Restart();
        using var again = restarted.CreateUiClient();
        using var login = await again.PostAsJsonAsync(new Uri("/api/auth/login", UriKind.Relative), new { email = QuorumApp.AdminEmail, password = QuorumApp.AdminPassword }, Ct);
        login.EnsureSuccessStatusCode();
        using var late = await ChatApi.DecideAsync(again, id, callId, approved: true);

        Assert.Equal("I need to run it. ", conversation.Messages[^1].Content);
        Assert.Equal(HttpStatusCode.NotFound, late.StatusCode);
        Assert.Empty(app.Probe.Calls);
        Assert.Empty(restarted.Probe.Calls);
        var audit = Assert.Single(await ToolFixtures.AuditRowsAsync(restarted));
        Assert.Equal(("Denied", "Cancelled"), (audit.Decision, audit.Outcome));
    }

    [Fact] // D-9
    public async Task TheTimeSpentWaitingForTheUserDoesNotCountAgainstTheTotalTime()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (app, client, id) = await StartConfirmationAsync(postgres, clock, confirmationSeconds: 3600, totalSeconds: 30);
        await using var _ = app;
        using var __ = client;
        await using var answer = await LiveAnswer.StartAsync(client, id, "Go");
        var callId = CallId(await answer.UntilAsync("tool_call"));

        clock.Advance(TimeSpan.FromMinutes(20));
        using var decision = await ChatApi.DecideAsync(client, id, callId, approved: true);
        var events = await answer.FinishAsync();

        Assert.Equal("Completed", events[^1].Data.GetProperty("status").GetString());
        Assert.Single(app.Probe.Calls);
    }

    [Fact]
    public async Task WhileTheAnswerWaitsTheStreamSendsPingsSoAProxyDoesNotCutIt()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (app, client, id) = await StartConfirmationAsync(postgres, clock, confirmationSeconds: 3600);
        await using var _ = app;
        using var __ = client;
        await using var answer = await LiveAnswer.StartAsync(client, id, "Go");
        var callId = CallId(await answer.UntilAsync("tool_call"));

        // The relay arms its timer a moment after the last event, so the clock is moved until the first ping shows.
        SseEvent? ping = null;
        for (var attempt = 0; attempt < 100 && ping is null; attempt++)
        {
            clock.Advance(TimeSpan.FromSeconds(16));
            var next = await answer.NextAsync(TimeSpan.FromMilliseconds(100));
            ping = next is { Type: "ping" } ? next : null;
        }

        Assert.NotNull(ping);
        using var decision = await ChatApi.DecideAsync(client, id, callId, approved: false);
        var events = await answer.FinishAsync();
        Assert.Equal("Completed", events[^1].Data.GetProperty("status").GetString());
    }

    [Fact] // AC-37
    public async Task TheModelCanAskTheUserAQuestionAndGetTheAnswerBack()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.ModelServer.EnqueueRounds(
            ChatScript.Reply("One moment. ") with { ToolCalls = [Ask("Which city?")] },
            ChatScript.Reply("Rome it is."));
        var id = await ChatApi.CreateConversationAsync(client);
        await using var answer = await LiveAnswer.StartAsync(client, id, "What is the weather?");

        var call = await answer.UntilAsync("tool_call");
        var question = await answer.UntilAsync("ask_user");

        Assert.False(call.Data.GetProperty("requiresConfirmation").GetBoolean());
        Assert.Equal(CallId(call), Guid.Parse(question.Data.GetProperty("callId").GetString()!));
        Assert.Equal("Which city?", question.Data.GetProperty("question").GetString());
        Assert.True(question.Data.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);
        Assert.Null(await answer.NextAsync(TimeSpan.FromMilliseconds(300)));

        using var reply = await ChatApi.AnswerAsync(client, id, CallId(call), "Rome");
        var events = await answer.FinishAsync();

        Assert.Equal(HttpStatusCode.NoContent, reply.StatusCode);
        var result = Assert.Single(events, e => e.Type == "tool_result").Data;
        Assert.Equal(("succeeded", "Rome"), (result.GetProperty("status").GetString(), result.GetProperty("content").GetString()));
        Assert.Equal("Rome", Messages(app, 1)[^1].GetProperty("content").GetString());
        Assert.Equal("One moment. Rome it is.", SseReader.Text(events));
        var audit = Assert.Single(await ToolFixtures.AuditRowsAsync(app));
        Assert.Equal(("ask_user", "Auto", "Succeeded"), (audit.ToolName, audit.Decision, audit.Outcome));
    }

    [Fact] // AC-37
    public async Task AnUnansweredQuestionTimesOutAndTheModelIsTold()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, clock);
        using var client = await app.SetUpAsync();
        await ToolFixtures.SetLimitsAsync(client, confirmationSeconds: 60);
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Ask("Which city?")), ChatScript.Reply("I will guess."));
        var id = await ChatApi.CreateConversationAsync(client);
        await using var answer = await LiveAnswer.StartAsync(client, id, "Weather?");
        await answer.UntilAsync("ask_user");

        clock.Advance(TimeSpan.FromSeconds(61));
        var events = await answer.FinishAsync();

        Assert.Equal("timed_out", Assert.Single(events, e => e.Type == "tool_result").Data.GetProperty("status").GetString());
        Assert.Contains("did not answer", Messages(app, 1)[^1].GetProperty("content").GetString()!, StringComparison.Ordinal);
        Assert.Equal("Completed", events[^1].Data.GetProperty("status").GetString());
        Assert.Equal("TimedOut", Assert.Single(await ToolFixtures.AuditRowsAsync(app)).Outcome);
    }

    [Fact] // D-15
    public async Task TheQuestionToolAsksEvenWhenItsPolicyIsConfirm()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await ToolFixtures.ExecuteAsync(app, "UPDATE agents.tools SET \"Policy\" = 'Confirm' WHERE \"Name\" = 'ask_user'");
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Ask("Which city?")), ChatScript.Reply("Thanks."));
        var id = await ChatApi.CreateConversationAsync(client);
        await using var answer = await LiveAnswer.StartAsync(client, id, "Weather?");

        var call = await answer.UntilAsync("tool_call");
        await answer.UntilAsync("ask_user");
        using var reply = await ChatApi.AnswerAsync(client, id, CallId(call), "Rome");
        var events = await answer.FinishAsync();

        Assert.False(call.Data.GetProperty("requiresConfirmation").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, reply.StatusCode);
        Assert.Equal("Completed", events[^1].Data.GetProperty("status").GetString());
    }

    [Fact] // D-10
    public async Task AnAnswerLongerThanTheResultLimitIsNotTruncatedTheModelGetsATooLargeError()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await ToolFixtures.SetLimitsAsync(client, maxResult: 1000);
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Ask("Describe it.")), ChatScript.Reply("Too long, sorry."));
        var id = await ChatApi.CreateConversationAsync(client);
        await using var answer = await LiveAnswer.StartAsync(client, id, "Go");
        var callId = CallId(await answer.UntilAsync("tool_call"));

        using var reply = await ChatApi.AnswerAsync(client, id, callId, new string('x', 1001));
        var events = await answer.FinishAsync();

        Assert.Equal(HttpStatusCode.NoContent, reply.StatusCode);
        Assert.Equal("failed", Assert.Single(events, e => e.Type == "tool_result").Data.GetProperty("status").GetString());
        var sent = Messages(app, 1)[^1].GetProperty("content").GetString()!;
        Assert.Contains("too large", sent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("xxxxx", sent, StringComparison.Ordinal);
        Assert.Equal("Failed", Assert.Single(await ToolFixtures.AuditRowsAsync(app)).Outcome);
    }

    [Fact] // AC-37, AC-40
    public async Task ClosingTheStreamWhileAQuestionIsPendingInterruptsTheAnswer()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.ModelServer.EnqueueRounds(ChatScript.Reply("Asking. ") with { ToolCalls = [Ask("Which city?")] });
        var id = await ChatApi.CreateConversationAsync(client);
        var answer = await LiveAnswer.StartAsync(client, id, "Weather?");
        await answer.UntilAsync("ask_user");

        await answer.DisposeAsync();

        var conversation = await ChatApi.WaitForLastMessageAsync(client, id, "Interrupted");
        Assert.Equal("Asking. ", conversation.Messages[^1].Content);
        Assert.Equal("Cancelled", Assert.Single(await ToolFixtures.AuditRowsAsync(app)).Outcome);
    }

    [Theory] // AC-37
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnEmptyAnswerIsRejectedAndTheQuestionStaysOpen(string text)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Ask("Which city?")), ChatScript.Reply("Thanks."));
        var id = await ChatApi.CreateConversationAsync(client);
        await using var answer = await LiveAnswer.StartAsync(client, id, "Weather?");
        var callId = CallId(await answer.UntilAsync("tool_call"));

        using var rejected = await ChatApi.AnswerAsync(client, id, callId, text);
        using var accepted = await ChatApi.AnswerAsync(client, id, callId, "Rome");
        using var again = await ChatApi.AnswerAsync(client, id, callId, "Milan");

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        await answer.FinishAsync();
    }

    [Theory] // AC-25
    [InlineData("""{"question":""}""")]
    [InlineData("""{"question":"   "}""")]
    [InlineData("""{}""")]
    public async Task AQuestionThatIsEmptyOrMissingIsReturnedToTheModelAsAnErrorWithoutAskingTheUser(string arguments)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.ModelServer.EnqueueRounds(
            ChatScript.CallTools(new FakeToolCall("native__ask_user", arguments)) with { ToolCallShape = ToolCallShape.WholeCallStringArguments },
            ChatScript.Reply("Sorry."));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Weather?");

        Assert.DoesNotContain(events, e => e.Type == "ask_user");
        Assert.Equal("failed", Assert.Single(events, e => e.Type == "tool_result").Data.GetProperty("status").GetString());
        Assert.Equal("Completed", events[^1].Data.GetProperty("status").GetString());
    }

    [Fact]
    public async Task AUserWithoutTheToolsUsePermissionDoesNotGetTheQuestionTool()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        using var member = await app.CreateMemberClientAsync();
        var id = await ChatApi.CreateConversationAsync(member);

        await ChatApi.SendAsync(member, id, "Hi");

        Assert.False(app.ModelServer.ChatRequests[^1].TryGetProperty("tools", out _));
    }
}
