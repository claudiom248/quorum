using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.Agents.Domain;
using static Quorum.Integration.Tests.Fixtures.FakeModelServer;

namespace Quorum.Integration.Tests.Tools;

/// <summary>The tool-calling loop of an answer (spec 004, AC-15, AC-19, AC-20, AC-22 to AC-29, AC-31).</summary>
public sealed class ToolLoopTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Uri AssistantSettings = new("/api/settings/assistant", UriKind.Relative);

    private static FakeToolCall Calculate(string expression, string id = "call_1") =>
        new("builtin__calculator", JsonSerializer.Serialize(new { expression }), id);

    private static FakeToolCall Probe(string text, string id = "call_1") =>
        new("builtin__probe", JsonSerializer.Serialize(new { text }), id);

    private static List<JsonElement> Messages(QuorumApp app, int request) =>
        app.ModelServer.ChatRequests[request].GetProperty("messages").EnumerateArray().ToList();

    private static async Task SetLimitsAsync(HttpClient client, int maxRounds = 8, int totalSeconds = 300, int callSeconds = 30, int maxResult = 20_000)
    {
        using var response = await client.PutAsJsonAsync(
            ToolsApi.ToolSettings,
            new
            {
                maxRounds,
                totalTimeoutSeconds = totalSeconds,
                callTimeoutSeconds = callSeconds,
                confirmationTimeoutSeconds = 300,
                maxResultCharacters = maxResult,
                auditRetentionDays = 90,
            },
            Ct);
        response.EnsureSuccessStatusCode();
    }

    [Fact] // AC-15
    public async Task WithoutActiveToolsTheRequestCarriesNoToolsAndTheSystemPromptIsUntouched()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync(systemPrompt: "You are Acme's assistant.");
        await ToolFixtures.ActivateOnlyAsync(client);
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Hi");

        Assert.Equal("Completed", events[^1].Data.GetProperty("status").GetString());
        var request = app.ModelServer.ChatRequests[^1];
        Assert.False(request.TryGetProperty("tools", out _));
        Assert.Equal("You are Acme's assistant.", Messages(app, 0)[0].GetProperty("content").GetString());
    }

    [Fact] // AC-15
    public async Task AModelEndpointThatDoesNotSupportToolsGetsNoToolsEvenWhenSomeAreActive()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        using var update = await client.PutAsJsonAsync(
            AssistantSettings,
            new { baseUrl = QuorumApp.ModelBaseUrl, modelName = "llama3.2", contextTokenBudget = 4096, systemPrompt = "x", supportsTools = false },
            Ct);
        update.EnsureSuccessStatusCode();
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Hi");

        Assert.Equal("Completed", events[^1].Data.GetProperty("status").GetString());
        Assert.False(app.ModelServer.ChatRequests[^1].TryGetProperty("tools", out _));
        Assert.Equal("x", Messages(app, 0)[0].GetProperty("content").GetString());
    }

    [Fact] // D-7
    public async Task AUserWithoutTheToolsUsePermissionChatsWithTheSameAssistantButWithoutTools()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync(systemPrompt: "You are Acme's assistant.");
        using var member = await app.CreateMemberClientAsync();
        var id = await ChatApi.CreateConversationAsync(member);

        var events = await ChatApi.SendAsync(member, id, "Hi");

        Assert.Equal("Completed", events[^1].Data.GetProperty("status").GetString());
        Assert.False(app.ModelServer.ChatRequests[^1].TryGetProperty("tools", out _));
        Assert.Equal("You are Acme's assistant.", Messages(app, 0)[0].GetProperty("content").GetString());
    }

    [Fact] // AC-20
    public async Task TheRequestCarriesTheActiveToolsWithPrefixedNames()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var id = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, id, "Hi");

        var tools = app.ModelServer.ChatRequests[^1].GetProperty("tools").EnumerateArray().ToList();
        Assert.Equal(
            ["builtin__ask_user", "builtin__calculator", "builtin__current_datetime"],
            tools.Select(t => t.GetProperty("function").GetProperty("name").GetString()).ToArray());
        Assert.All(tools, tool => Assert.Equal("function", tool.GetProperty("type").GetString()));
    }

    [Fact] // AC-31
    public async Task WithToolsTheSystemPromptSaysThatResultsAndDocumentsAreDataNotInstructions()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync(systemPrompt: "You are Acme's assistant.");
        var id = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, id, "Hi");

        var system = Messages(app, 0)[0].GetProperty("content").GetString()!;
        Assert.StartsWith("You are Acme's assistant.", system, StringComparison.Ordinal);
        Assert.Contains("tool results", system, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never instructions", system, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // AC-19
    public async Task AToolCallIsRunTheResultGoesBackToTheModelAndTheFinalTextIsStreamed()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.ModelServer.EnqueueRounds(
            ChatScript.Reply("Let me compute. ") with { ToolCalls = [Calculate("2+2*3")] },
            ChatScript.Reply("The result", " is 8."));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "What is 2+2*3?");

        Assert.Equal(["delta", "tool_call", "tool_result", "delta", "delta", "done"], events.Select(e => e.Type));
        var call = events[1].Data;
        Assert.True(Guid.TryParse(call.GetProperty("callId").GetString(), out var callId));
        Assert.Equal("builtin__calculator", call.GetProperty("tool").GetString());
        Assert.Equal("2+2*3", call.GetProperty("arguments").GetProperty("expression").GetString());
        Assert.False(call.GetProperty("requiresConfirmation").GetBoolean());
        var result = events[2].Data;
        Assert.Equal(callId.ToString(), result.GetProperty("callId").GetString());
        Assert.Equal("succeeded", result.GetProperty("status").GetString());
        Assert.Equal("8", result.GetProperty("content").GetString());
        Assert.Equal(1, result.GetProperty("sizeCharacters").GetInt32());
        Assert.Equal("Let me compute. The result is 8.", SseReader.Text(events));

        var second = Messages(app, 1);
        var assistant = second[^2];
        Assert.Equal("assistant", assistant.GetProperty("role").GetString());
        Assert.Equal("Let me compute. ", assistant.GetProperty("content").GetString());
        var sentCall = Assert.Single(assistant.GetProperty("tool_calls").EnumerateArray());
        Assert.Equal("call_1", sentCall.GetProperty("id").GetString());
        Assert.Equal("function", sentCall.GetProperty("type").GetString());
        Assert.Equal("builtin__calculator", sentCall.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("2+2*3", JsonDocument.Parse(sentCall.GetProperty("function").GetProperty("arguments").GetString()!).RootElement.GetProperty("expression").GetString());
        var tool = second[^1];
        Assert.Equal("tool", tool.GetProperty("role").GetString());
        Assert.Equal("call_1", tool.GetProperty("tool_call_id").GetString());
        Assert.Equal("8", tool.GetProperty("content").GetString());
        Assert.True(app.ModelServer.ChatRequests[1].TryGetProperty("tools", out _));
    }

    [Theory]
    [InlineData(ToolCallShape.OpenAiFragments)]
    [InlineData(ToolCallShape.WholeCallStringArguments)]
    [InlineData(ToolCallShape.WholeCallObjectArguments)]
    public async Task ToolCallsAreAssembledWhateverTheShapeTheServerStreamsThemIn(ToolCallShape shape)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.ModelServer.EnqueueRounds(
            ChatScript.CallTools(Calculate("(10 + 5) * 2")) with { ToolCallShape = shape },
            ChatScript.Reply("Done."));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Compute");

        Assert.Equal("Completed", events[^1].Data.GetProperty("status").GetString());
        Assert.Equal("30", Messages(app, 1)[^1].GetProperty("content").GetString());
        Assert.False(string.IsNullOrEmpty(Messages(app, 1)[^1].GetProperty("tool_call_id").GetString()));
    }

    [Fact]
    public async Task ACallWithoutAnIdGetsOneSoTheResultCanBeMatchedToIt()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.ModelServer.EnqueueRounds(
            ChatScript.CallTools(new FakeToolCall("builtin__calculator", """{"expression":"3*3"}""", Id: null)) with { ToolCallShape = ToolCallShape.WholeCallObjectArguments },
            ChatScript.Reply("Nine."));
        var id = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, id, "3*3?");

        var second = Messages(app, 1);
        var sentId = second[^2].GetProperty("tool_calls")[0].GetProperty("id").GetString();
        Assert.False(string.IsNullOrEmpty(sentId));
        Assert.Equal(sentId, second[^1].GetProperty("tool_call_id").GetString());
    }

    [Fact]
    public async Task SeveralCallsInOneRoundAreAssembledByIndex()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.ModelServer.EnqueueRounds(
            ChatScript.CallTools(Calculate("1+1", "call_a"), Calculate("2+2", "call_b")),
            ChatScript.Reply("Done."));
        var id = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, id, "Compute");

        var second = Messages(app, 1);
        Assert.Equal(["call_a", "call_b"], second[^3].GetProperty("tool_calls").EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToArray());
        Assert.Equal([("call_a", "2"), ("call_b", "4")], second.TakeLast(2).Select(m => (m.GetProperty("tool_call_id").GetString(), m.GetProperty("content").GetString())));
    }

    [Fact] // AC-28
    public async Task OnlyTheFinalTextIsStoredAndLaterTurnsDoNotSeeTheToolMessages()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.ModelServer.EnqueueRounds(
            ChatScript.Reply("Checking. ") with { ToolCalls = [Calculate("6*7")] },
            ChatScript.Reply("It is 42."));
        var id = await ChatApi.CreateConversationAsync(client);
        await ChatApi.SendAsync(client, id, "6*7?");

        await ChatApi.SendAsync(client, id, "Thanks");

        var conversation = await ChatApi.GetAsync(client, id);
        Assert.Equal(
            [("User", "6*7?"), ("Assistant", "Checking. It is 42."), ("User", "Thanks"), ("Assistant", "Hello from the model.")],
            conversation.Messages.Select(m => (m.Role, m.Content)));
        var later = Messages(app, 2);
        Assert.DoesNotContain(later, m => m.GetProperty("role").GetString() == "tool");
        Assert.DoesNotContain(later, m => m.TryGetProperty("tool_calls", out _));
    }

    [Fact] // AC-22
    public async Task AModelThatKeepsAskingForToolsEndsTheAnswerAsFailedAfterTheRoundLimitKeepingTheText()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.ModelServer.Chat = ChatScript.Reply("Still working. ") with { ToolCalls = [Calculate("1+1")] };
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Loop");

        var done = events[^1].Data;
        Assert.Equal("Failed", done.GetProperty("status").GetString());
        Assert.Contains("8 rounds", done.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(8, app.ModelServer.ChatRequests.Count);
        var stored = (await ChatApi.GetAsync(client, id)).Messages[^1];
        Assert.Equal("Failed", stored.Status);
        Assert.Equal(string.Concat(Enumerable.Repeat("Still working. ", 8)), stored.Content);
    }

    [Fact] // AC-22
    public async Task TheRoundLimitIsTheOneTheAdminConfigured()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await SetLimitsAsync(client, maxRounds: 2);
        app.ModelServer.Chat = ChatScript.CallTools(Calculate("1+1"));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Loop");

        Assert.Equal("Failed", events[^1].Data.GetProperty("status").GetString());
        Assert.Contains("2 rounds", events[^1].Data.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(2, app.ModelServer.ChatRequests.Count);
    }

    [Fact] // AC-22
    public async Task WithASingleRoundACallIsNeverRunAndTheAnswerFails()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await SetLimitsAsync(client, maxRounds: 1);
        await ToolFixtures.AddProbeAsync(app);
        app.ModelServer.Chat = ChatScript.Reply("Trying. ") with { ToolCalls = [Probe("x")] };
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Go");

        Assert.Equal("Failed", events[^1].Data.GetProperty("status").GetString());
        Assert.Contains("1 rounds", events[^1].Data.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Single(app.ModelServer.ChatRequests);
        Assert.Empty(app.Probe.Calls);
        Assert.Empty(await ToolFixtures.AuditRowsAsync(app));
    }

    [Fact] // AC-25
    public async Task AnUnknownToolIsReturnedToTheModelAsAnErrorAndTheRoundCounts()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.ModelServer.EnqueueRounds(
            ChatScript.CallTools(new FakeToolCall("builtin__teleport", "{}")),
            ChatScript.Reply("I cannot do that."));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Teleport me");

        var result = Assert.Single(events, e => e.Type == "tool_result").Data;
        Assert.Equal("failed", result.GetProperty("status").GetString());
        Assert.Contains("builtin__teleport", Messages(app, 1)[^1].GetProperty("content").GetString()!, StringComparison.Ordinal);
        Assert.Equal(2, app.ModelServer.ChatRequests.Count);
        Assert.Equal("Completed", events[^1].Data.GetProperty("status").GetString());
    }

    [Theory] // AC-25
    [InlineData("""{"expression":42}""")]
    [InlineData("""{}""")]
    [InlineData("""{"expression":"1+1","extra":true}""")]
    [InlineData("""{"expression" """)]
    [InlineData("""[1,2]""")]
    public async Task ArgumentsThatDoNotMatchTheSchemaAreReturnedToTheModelAsAnError(string arguments)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.ModelServer.EnqueueRounds(
            ChatScript.CallTools(new FakeToolCall("builtin__calculator", arguments)) with { ToolCallShape = ToolCallShape.WholeCallStringArguments },
            ChatScript.Reply("Sorry."));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Compute");

        Assert.Equal("failed", Assert.Single(events, e => e.Type == "tool_result").Data.GetProperty("status").GetString());
        Assert.Equal("Completed", events[^1].Data.GetProperty("status").GetString());
        Assert.Equal(2, app.ModelServer.ChatRequests.Count);
    }

    [Fact]
    public async Task AFailureOfTheToolItselfIsReturnedToTheModelAsAnError()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Calculate("1/0")), ChatScript.Reply("Cannot divide by zero."));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "1/0?");

        var result = Assert.Single(events, e => e.Type == "tool_result").Data;
        Assert.Equal("failed", result.GetProperty("status").GetString());
        Assert.Contains("zero", result.GetProperty("content").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // AC-25
    public async Task AToolThatIsNoLongerActiveWhenTheCallArrivesReturnsAnError()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await ToolFixtures.AddProbeAsync(app);
        app.Probe.Handler = async (_, _) =>
        {
            await ToolFixtures.ExecuteAsync(app, "UPDATE agents.tools SET \"State\" = 'Disabled' WHERE \"Name\" = 'probe'");
            return "first";
        };
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Probe("a", "call_a"), Probe("b", "call_b")), ChatScript.Reply("Done."));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Go");

        var results = events.Where(e => e.Type == "tool_result").ToList();
        Assert.Equal(["succeeded", "failed"], results.Select(r => r.Data.GetProperty("status").GetString()));
        Assert.Single(app.Probe.Calls);
        Assert.Contains("no longer available", Messages(app, 1)[^1].GetProperty("content").GetString()!, StringComparison.Ordinal);
    }

    [Fact] // AC-26
    public async Task AResultLargerThanTheLimitIsNotTruncatedTheModelGetsATooLargeError()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await SetLimitsAsync(client, maxResult: 1000);
        await ToolFixtures.AddProbeAsync(app);
        app.Probe.Handler = (_, _) => Task.FromResult(new string('x', 1001));
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Probe("big")), ChatScript.Reply("It was too large."));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Go");

        var result = Assert.Single(events, e => e.Type == "tool_result").Data;
        Assert.Equal("failed", result.GetProperty("status").GetString());
        var sent = Messages(app, 1)[^1].GetProperty("content").GetString()!;
        Assert.Contains("too large", sent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1001", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("xxxxx", sent, StringComparison.Ordinal);
        Assert.Equal(sent.Length, result.GetProperty("sizeCharacters").GetInt32());
    }

    [Fact] // AC-26
    public async Task AResultExactlyAtTheLimitIsDelivered()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync(contextTokenBudget: 4096);
        await SetLimitsAsync(client, maxResult: 1000);
        await ToolFixtures.AddProbeAsync(app);
        app.Probe.Handler = (_, _) => Task.FromResult(new string('x', 1000));
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Probe("big")), ChatScript.Reply("Fine."));
        var id = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, id, "Go");

        Assert.Equal(new string('x', 1000), Messages(app, 1)[^1].GetProperty("content").GetString());
    }

    [Fact] // AC-27
    public async Task CallsOfOneRoundRunOneAtATimeInTheOrderTheModelEmittedThem()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await ToolFixtures.AddProbeAsync(app);
        var running = 0;
        var overlapped = false;
        app.Probe.Handler = async (arguments, _) =>
        {
            overlapped |= Interlocked.Increment(ref running) > 1;
            await Task.Delay(40, Ct);
            Interlocked.Decrement(ref running);
            return $"done {arguments.GetProperty("text").GetString()}";
        };
        app.ModelServer.EnqueueRounds(
            ChatScript.CallTools(Probe("first", "call_1"), Probe("second", "call_2"), Probe("third", "call_3")),
            ChatScript.Reply("All done."));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Go");

        Assert.False(overlapped, "Two calls of the same round ran at the same time.");
        Assert.Equal(
            ["""{"text":"first"}""", """{"text":"second"}""", """{"text":"third"}"""],
            app.Probe.Calls);
        Assert.Equal(
            ["tool_call", "tool_result", "tool_call", "tool_result", "tool_call", "tool_result"],
            events.Where(e => e.Type.StartsWith("tool_", StringComparison.Ordinal)).Select(e => e.Type));
        Assert.Equal(
            ["done first", "done second", "done third"],
            Messages(app, 1).TakeLast(3).Select(m => m.GetProperty("content").GetString()));
    }

    [Fact] // AC-23
    public async Task ACallThatExceedsTheCallTimeoutIsCancelledAndTheModelGetsAnError()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, clock);
        using var client = await app.SetUpAsync();
        await SetLimitsAsync(client, callSeconds: 5);
        await ToolFixtures.AddProbeAsync(app);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        app.Probe.Handler = async (_, token) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, clock, token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                throw;
            }

            return "never";
        };
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Probe("slow")), ChatScript.Reply("It took too long."));
        var id = await ChatApi.CreateConversationAsync(client);

        var answer = ChatApi.SendAsync(client, id, "Go");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        clock.Advance(TimeSpan.FromSeconds(6));
        var events = await answer.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.True(cancelled);
        Assert.Equal("timed_out", Assert.Single(events, e => e.Type == "tool_result").Data.GetProperty("status").GetString());
        Assert.Contains("time", Messages(app, 1)[^1].GetProperty("content").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Completed", events[^1].Data.GetProperty("status").GetString());
        Assert.Equal("TimedOut", Assert.Single(await ToolFixtures.AuditRowsAsync(app)).Outcome);
    }

    [Fact] // AC-24
    public async Task AnAnswerThatExceedsTheTotalTimeEndsAsFailedKeepingTheText()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, clock);
        using var client = await app.SetUpAsync();
        await SetLimitsAsync(client, totalSeconds: 10, callSeconds: 60);
        await ToolFixtures.AddProbeAsync(app);
        app.Probe.Handler = async (_, token) =>
        {
            clock.Advance(TimeSpan.FromSeconds(11));
            await Task.Delay(Timeout.InfiniteTimeSpan, clock, token);
            return "never";
        };
        app.ModelServer.EnqueueRounds(
            ChatScript.Reply("Working. ") with { ToolCalls = [Probe("slow")] },
            ChatScript.Reply("Should not be asked."));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Go");

        var done = events[^1].Data;
        Assert.Equal("Failed", done.GetProperty("status").GetString());
        Assert.Contains("total time", done.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal("Working. ", (await ChatApi.GetAsync(client, id)).Messages[^1].Content);
        Assert.Single(app.ModelServer.ChatRequests);
        Assert.Equal("TimedOut", Assert.Single(await ToolFixtures.AuditRowsAsync(app)).Outcome);
    }

    [Fact]
    public async Task StoppingTheAnswerDuringACallCancelsItAndStoresTheAnswerAsInterrupted()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await ToolFixtures.AddProbeAsync(app);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Probe.Handler = async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "never";
        };
        app.ModelServer.EnqueueRounds(ChatScript.Reply("Looking. ") with { ToolCalls = [Probe("slow")] });
        var id = await ChatApi.CreateConversationAsync(client);

        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ChatApi.Messages(id)) { Content = JsonContent.Create(new { content = "Go" }) };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            await stop.CancelAsync();
        }

        var conversation = await ChatApi.WaitForLastMessageAsync(client, id, "Interrupted");
        Assert.Equal("Looking. ", conversation.Messages[^1].Content);
        var audit = Assert.Single(await ToolFixtures.AuditRowsAsync(app));
        Assert.Equal("Cancelled", audit.Outcome);
        Assert.Empty(app.Logs.ErrorsAndAbove);
    }

    [Fact] // AC-29
    public async Task AModelThatRejectsTheToolsParameterFailsTheAnswerNamingTheSupportsToolsFlag()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.ModelServer.Chat = ChatScript.Reply("unused") with { RejectsTools = true };
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Hi");

        var done = events[^1].Data;
        Assert.Equal("Failed", done.GetProperty("status").GetString());
        var error = done.GetProperty("error").GetString()!;
        Assert.Contains("supports tools", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not support tools", error, StringComparison.Ordinal);
        Assert.Equal("Failed", (await ChatApi.GetAsync(client, id)).Messages[^1].Status);
    }

    [Fact]
    public async Task AnErrorOtherThanBadRequestIsStillReportedAsBefore()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.ModelServer.Chat = ChatScript.Reply() with { HttpErrorStatus = 503 };
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Hi");

        var error = events[^1].Data.GetProperty("error").GetString()!;
        Assert.Contains("HTTP 503", error, StringComparison.Ordinal);
        Assert.DoesNotContain("supports tools", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AFinalRoundWithoutTextIsAFailureNotAnEmptySuccess()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Calculate("1+1")), ChatScript.Reply());
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Go");

        Assert.Equal("Failed", events[^1].Data.GetProperty("status").GetString());
        Assert.Contains("without an answer", events[^1].Data.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARegeneratedAnswerRunsTheToolsAgain()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Calculate("1+1")), ChatScript.Reply("Two."));
        var id = await ChatApi.CreateConversationAsync(client);
        await ChatApi.SendAsync(client, id, "1+1?");

        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Calculate("1+1")), ChatScript.Reply("Still two."));
        using var response = await client.PostAsync(ChatApi.Regenerate(id), content: null, Ct);
        var events = await SseReader.ReadAllAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Still two.", SseReader.Text(events));
        Assert.Equal(1, events.Count(e => e.Type == "tool_result"));
    }
}
