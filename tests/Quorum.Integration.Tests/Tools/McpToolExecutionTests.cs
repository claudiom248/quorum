using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;
using static Quorum.Integration.Tests.Fixtures.FakeModelServer;

namespace Quorum.Integration.Tests.Tools;

/// <summary>Running the tools of an MCP server in an answer, through the same pipeline as built-in tools (spec 004, AC-19, AC-20, AC-23 to AC-26, AC-47).</summary>
public sealed class McpToolExecutionTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FakeToolCall Search(string text, string name = "files__search", string id = "call_1") =>
        new(name, JsonSerializer.Serialize(new { text }), id);

    private static List<JsonElement> Messages(QuorumApp app, int request) =>
        app.ModelServer.ChatRequests[request].GetProperty("messages").EnumerateArray().ToList();

    private static Guid CallId(SseEvent toolCall) => Guid.Parse(toolCall.Data.GetProperty("callId").GetString()!);

    /// <summary>Registers the server, enables its tools with the policy and activates them on the default assistant.</summary>
    private static async Task RegisterAndActivateAsync(
        HttpClient client,
        McpTestServer mcp,
        string serverName = "files",
        string policy = "Auto",
        object? headers = null,
        string? url = null)
    {
        var created = await ToolsApi.RegisterServerOkAsync(client, serverName, url ?? mcp.Url, headers);
        foreach (var tool in created.Tools)
        {
            using var review = await client.PutAsJsonAsync(ToolsApi.Tool(tool.Id), new { state = "Enabled", policy }, Ct);
            review.EnsureSuccessStatusCode();
        }

        var pool = (await ToolsApi.GetAssistantToolsAsync(client, await ToolsApi.DefaultAssistantIdAsync(client))).Pool;
        await ToolFixtures.ActivateOnlyAsync(client, [.. pool.Where(t => t.Name.StartsWith(serverName + "__", StringComparison.Ordinal)).Select(t => t.Name)]);
    }

    [Fact] // AC-19, AC-20
    public async Task AnMcpToolIsOfferedWithItsPrefixedNameRunOnTheServerAndItsResultGoesBackToTheModel()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await using var mcp = await McpTestServer.StartAsync(McpTestServer.NewTool("search", "Searches files."));
        await RegisterAndActivateAsync(client, mcp);
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Search("quarterly")), ChatScript.Reply("Found it."));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Find the report");

        var offered = app.ModelServer.ChatRequests[0].GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("function")).ToList();
        var tool = Assert.Single(offered);
        Assert.Equal("files__search", tool.GetProperty("name").GetString());
        Assert.Equal("Searches files.", tool.GetProperty("description").GetString());

        var call = Assert.Single(events, e => e.Type == "tool_call").Data;
        Assert.Equal("files__search", call.GetProperty("tool").GetString());
        Assert.False(call.GetProperty("requiresConfirmation").GetBoolean());
        var result = Assert.Single(events, e => e.Type == "tool_result").Data;
        Assert.Equal(("succeeded", "called search"), (result.GetProperty("status").GetString(), result.GetProperty("content").GetString()));
        Assert.Equal("Found it.", SseReader.Text(events));
        Assert.Equal("called search", Messages(app, 1)[^1].GetProperty("content").GetString());

        var (name, arguments) = Assert.Single(mcp.Calls);
        Assert.Equal("search", name);
        Assert.Equal("quarterly", JsonDocument.Parse(arguments).RootElement.GetProperty("text").GetString());

        var audit = Assert.Single(await ToolFixtures.AuditRowsAsync(app));
        Assert.Equal(("search", "files", "Auto", "Succeeded"), (audit.ToolName, audit.ServerName, audit.Decision, audit.Outcome));
    }

    [Fact] // D-16
    public async Task AToolNameTheModelApisWouldRefuseIsSanitizedAndTheServerStillReceivesTheRealName()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await using var mcp = await McpTestServer.StartAsync(McpTestServer.NewTool("search.files"), McpTestServer.NewTool("search_files"));
        await RegisterAndActivateAsync(client, mcp);
        var offered = (await ToolsApi.GetAssistantToolsAsync(client, await ToolsApi.DefaultAssistantIdAsync(client))).Pool
            .Select(t => t.Name).Where(n => n.StartsWith("files__", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(2, offered.Distinct().Count());
        var sanitized = offered.Single(n => n != "files__search_files");
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Search("x", sanitized)), ChatScript.Reply("Done."));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Go");

        Assert.Equal("succeeded", Assert.Single(events, e => e.Type == "tool_result").Data.GetProperty("status").GetString());
        Assert.Equal("search.files", Assert.Single(mcp.Calls).Name);
        var audit = Assert.Single(await ToolFixtures.AuditRowsAsync(app));
        Assert.Equal(("search.files", "files"), (audit.ToolName, audit.ServerName));
    }

    [Fact]
    public async Task TheServerHeadersAreSentWithTheCall()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await using var mcp = await McpTestServer.StartAsync(McpTestServer.NewTool("search"));
        await RegisterAndActivateAsync(client, mcp, headers: new Dictionary<string, string> { ["X-Api-Key"] = "k-123" });
        mcp.ReceivedHeaders.Clear();
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Search("x")), ChatScript.Reply("Done."));
        var id = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, id, "Go");

        Assert.Single(mcp.Calls);
        Assert.Contains("X-Api-Key: k-123", mcp.ReceivedHeaders);
    }

    [Fact] // D-9, D-14: a new MCP tool asks for confirmation
    public async Task AConfirmToolWaitsForTheUserAndRunsOnServerOnlyAfterApproval()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await using var mcp = await McpTestServer.StartAsync(McpTestServer.NewTool("search"));
        await RegisterAndActivateAsync(client, mcp, policy: "Confirm");
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Search("danger")), ChatScript.Reply("Finished."));
        var id = await ChatApi.CreateConversationAsync(client);
        await using var answer = await LiveAnswer.StartAsync(client, id, "Go");

        var call = await answer.UntilAsync("tool_call");
        Assert.True(call.Data.GetProperty("requiresConfirmation").GetBoolean());
        Assert.Empty(mcp.Calls);

        using var decision = await ChatApi.DecideAsync(client, id, CallId(call), approved: true);
        var events = await answer.FinishAsync();

        Assert.Equal(HttpStatusCode.NoContent, decision.StatusCode);
        Assert.Equal("succeeded", Assert.Single(events, e => e.Type == "tool_result").Data.GetProperty("status").GetString());
        Assert.Single(mcp.Calls);
        var audit = Assert.Single(await ToolFixtures.AuditRowsAsync(app));
        Assert.Equal(("files", "Approved", "Succeeded"), (audit.ServerName, audit.Decision, audit.Outcome));
    }

    [Fact]
    public async Task ADeniedMcpCallNeverReachesTheServer()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await using var mcp = await McpTestServer.StartAsync(McpTestServer.NewTool("search"));
        await RegisterAndActivateAsync(client, mcp, policy: "Confirm");
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Search("danger")), ChatScript.Reply("Understood."));
        var id = await ChatApi.CreateConversationAsync(client);
        await using var answer = await LiveAnswer.StartAsync(client, id, "Go");
        var call = await answer.UntilAsync("tool_call");

        using var decision = await ChatApi.DecideAsync(client, id, CallId(call), approved: false);
        var events = await answer.FinishAsync();

        Assert.Equal("denied", Assert.Single(events, e => e.Type == "tool_result").Data.GetProperty("status").GetString());
        Assert.Empty(mcp.Calls);
        var audit = Assert.Single(await ToolFixtures.AuditRowsAsync(app));
        Assert.Equal(("files", "Denied", "Denied"), (audit.ServerName, audit.Decision, audit.Outcome));
    }

    [Theory] // D-9, D-14, D-17: what was true when the model was offered the tool is checked again when the call runs
    [InlineData("UPDATE agents.mcp_servers SET \"Status\" = 'Unavailable'")]
    [InlineData("UPDATE agents.tools SET \"State\" = 'Disabled' WHERE \"ServerId\" IS NOT NULL")]
    [InlineData("UPDATE agents.tools SET \"State\" = 'PendingReview' WHERE \"ServerId\" IS NOT NULL")]
    [InlineData("UPDATE agents.tools SET \"DefinitionHash\" = 'a-different-definition' WHERE \"ServerId\" IS NOT NULL")]
    [InlineData("DELETE FROM agents.assistant_tools WHERE \"ToolId\" IN (SELECT \"Id\" FROM agents.tools WHERE \"ServerId\" IS NOT NULL)")]
    public async Task AnMcpToolThatStopsBeingAvailableWhileWaitingIsNotRunEvenWhenApproved(string change)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await using var mcp = await McpTestServer.StartAsync(McpTestServer.NewTool("search"));
        await RegisterAndActivateAsync(client, mcp, policy: "Confirm");
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Search("x")), ChatScript.Reply("Understood."));
        var id = await ChatApi.CreateConversationAsync(client);
        await using var answer = await LiveAnswer.StartAsync(client, id, "Go");
        var call = await answer.UntilAsync("tool_call");
        await ToolFixtures.ExecuteAsync(app, change);

        using var decision = await ChatApi.DecideAsync(client, id, CallId(call), approved: true);
        var events = await answer.FinishAsync();

        Assert.Equal("failed", Assert.Single(events, e => e.Type == "tool_result").Data.GetProperty("status").GetString());
        Assert.Contains("no longer available", Messages(app, 1)[^1].GetProperty("content").GetString()!, StringComparison.Ordinal);
        Assert.Empty(mcp.Calls);
        var audit = Assert.Single(await ToolFixtures.AuditRowsAsync(app));
        Assert.Equal(("Approved", "Failed", "tool.unavailable"), (audit.Decision, audit.Outcome, audit.ErrorCode));
    }

    [Fact] // D-9, D-18: an approval is for the address it was given for
    public async Task AServerWhoseUrlChangedWhileWaitingForConfirmationIsNotCalledEvenWhenApproved()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await using var mcp = await McpTestServer.StartAsync(McpTestServer.NewTool("search"));
        await RegisterAndActivateAsync(client, mcp, policy: "Confirm");
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Search("x")), ChatScript.Reply("Understood."));
        var id = await ChatApi.CreateConversationAsync(client);
        await using var answer = await LiveAnswer.StartAsync(client, id, "Go");
        var call = await answer.UntilAsync("tool_call");
        await ToolFixtures.ExecuteAsync(app, $"UPDATE agents.mcp_servers SET \"Url\" = '{mcp.Url}?moved=1'");

        using var decision = await ChatApi.DecideAsync(client, id, CallId(call), approved: true);
        var events = await answer.FinishAsync();

        Assert.Equal("failed", Assert.Single(events, e => e.Type == "tool_result").Data.GetProperty("status").GetString());
        Assert.Contains("no longer available", Messages(app, 1)[^1].GetProperty("content").GetString()!, StringComparison.Ordinal);
        Assert.Empty(mcp.Calls);
    }

    [Fact] // D-18: the guard also holds when the call runs, not only when the server was registered
    public async Task AServerWhoseUrlPointsToABlockedAddressIsNotContactedWhenTheCallRuns()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await using var mcp = await McpTestServer.StartAsync(McpTestServer.NewTool("search"));
        await RegisterAndActivateAsync(client, mcp);
        await ToolFixtures.ExecuteAsync(app, "UPDATE agents.mcp_servers SET \"Url\" = 'http://169.254.169.254/mcp'");
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Search("x")), ChatScript.Reply("It failed."));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Go");

        var result = Assert.Single(events, e => e.Type == "tool_result").Data;
        Assert.Equal("failed", result.GetProperty("status").GetString());
        Assert.Contains("not allowed", result.GetProperty("content").GetString()!, StringComparison.Ordinal);
        Assert.Empty(mcp.Calls);
    }

    [Fact]
    public async Task OnlyTextIsPassedOnNonTextContentIsNamedAndStructuredContentIsUsedWhenThereIsNoText()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await using var mcp = await McpTestServer.StartAsync(McpTestServer.NewTool("search"));
        await RegisterAndActivateAsync(client, mcp);
        mcp.CallHandler = (request, _) => ValueTask.FromResult(
            request.Arguments!["text"].GetString() == "mixed"
                ? new CallToolResult { Content = [new TextContentBlock { Text = "Chart:" }, new ImageContentBlock { Data = "cG5n"u8.ToArray(), MimeType = "image/png" }] }
                : new CallToolResult { StructuredContent = JsonDocument.Parse("""{"count":3}""").RootElement.Clone() });
        app.ModelServer.EnqueueRounds(
            ChatScript.CallTools(Search("mixed", id: "call_1"), Search("structured", id: "call_2")),
            ChatScript.Reply("Done."));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Go");

        Assert.Equal(
            ["Chart:\n[image content (image/png) not shown]", """{"count":3}"""],
            events.Where(e => e.Type == "tool_result").Select(e => e.Data.GetProperty("content").GetString()));
    }

    [Fact] // AC-26
    public async Task AResultLargerThanTheLimitIsNotTruncatedTheModelGetsATooLargeError()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await ToolFixtures.SetLimitsAsync(client, maxResult: 1000);
        await using var mcp = await McpTestServer.StartAsync(McpTestServer.NewTool("search"));
        mcp.CallHandler = (_, _) => ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = new string('x', 1001) }] });
        await RegisterAndActivateAsync(client, mcp);
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Search("big")), ChatScript.Reply("Too large."));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Go");

        Assert.Equal("failed", Assert.Single(events, e => e.Type == "tool_result").Data.GetProperty("status").GetString());
        var sent = Messages(app, 1)[^1].GetProperty("content").GetString()!;
        Assert.Contains("too large", sent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("xxxxx", sent, StringComparison.Ordinal);
        var audit = Assert.Single(await ToolFixtures.AuditRowsAsync(app));
        Assert.Equal(("Failed", "tool.result_too_large", 1001), (audit.Outcome, audit.ErrorCode, audit.ResultCharacters));
    }

    [Fact]
    public async Task AnErrorTheServerReportsInItsResultGoesToTheModelAsAFailedCall()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await using var mcp = await McpTestServer.StartAsync(McpTestServer.NewTool("search"));
        mcp.CallHandler = (_, _) => ValueTask.FromResult(new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "Quota exceeded." }] });
        await RegisterAndActivateAsync(client, mcp);
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Search("x")), ChatScript.Reply("It failed."));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Go");

        var result = Assert.Single(events, e => e.Type == "tool_result").Data;
        Assert.Equal(("failed", "Quota exceeded."), (result.GetProperty("status").GetString(), result.GetProperty("content").GetString()));
        Assert.Equal("Completed", events[^1].Data.GetProperty("status").GetString());
        var audit = Assert.Single(await ToolFixtures.AuditRowsAsync(app));
        Assert.Equal(("Failed", "tool.failed"), (audit.Outcome, audit.ErrorCode));
    }

    [Fact]
    public async Task AProtocolErrorFromTheServerGoesToTheModelAsAFailedCallAndTheAnswerContinues()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await using var mcp = await McpTestServer.StartAsync(McpTestServer.NewTool("search"));
        mcp.CallHandler = (_, _) => throw new McpProtocolException("The index is offline.", McpErrorCode.InternalError);
        await RegisterAndActivateAsync(client, mcp);
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Search("x")), ChatScript.Reply("It failed."));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Go");

        Assert.Equal("failed", Assert.Single(events, e => e.Type == "tool_result").Data.GetProperty("status").GetString());
        Assert.Equal("It failed.", SseReader.Text(events));
        Assert.Equal("Completed", events[^1].Data.GetProperty("status").GetString());
    }

    [Fact] // D-18, AC-4
    public async Task ARedirectIsNotFollowedAndTheCallFailsWithoutLeakingTheHeaders()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await using var mcp = await McpTestServer.StartAsync(McpTestServer.NewTool("search"));
        await RegisterAndActivateAsync(client, mcp, headers: new Dictionary<string, string> { ["Authorization"] = "Bearer s3cret" });
        await ToolFixtures.ExecuteAsync(app, $"UPDATE agents.mcp_servers SET \"Url\" = '{mcp.RedirectUrl}'");
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Search("x")), ChatScript.Reply("It failed."));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Go");

        var result = Assert.Single(events, e => e.Type == "tool_result").Data;
        Assert.Equal("failed", result.GetProperty("status").GetString());
        Assert.DoesNotContain("s3cret", result.GetProperty("content").GetString()!, StringComparison.Ordinal);
        Assert.Equal(0, mcp.RedirectTargetHits);
        Assert.Empty(mcp.Calls);
        var audit = Assert.Single(await ToolFixtures.AuditRowsAsync(app));
        Assert.Equal(("Failed", "tool.failed"), (audit.Outcome, audit.ErrorCode));
    }

    [Fact]
    public async Task AServerThatAnswersWithAnErrorStatusFailsTheCall()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await using var mcp = await McpTestServer.StartAsync(McpTestServer.NewTool("search"));
        await RegisterAndActivateAsync(client, mcp);
        await ToolFixtures.ExecuteAsync(app, $"UPDATE agents.mcp_servers SET \"Url\" = '{mcp.ErrorUrl}'");
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Search("x")), ChatScript.Reply("It failed."));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Go");

        var result = Assert.Single(events, e => e.Type == "tool_result").Data;
        Assert.Equal("failed", result.GetProperty("status").GetString());
        Assert.Contains("500", result.GetProperty("content").GetString()!, StringComparison.Ordinal);
        Assert.Equal("Completed", events[^1].Data.GetProperty("status").GetString());
    }

    [Fact] // AC-23
    public async Task AnMcpCallThatExceedsTheCallTimeoutIsCancelledAndTheModelGetsAnError()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, clock);
        using var client = await app.SetUpAsync();
        await ToolFixtures.SetLimitsAsync(client, callSeconds: 5);
        await using var mcp = await McpTestServer.StartAsync(McpTestServer.NewTool("search"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mcp.CallHandler = async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new CallToolResult();
        };
        await RegisterAndActivateAsync(client, mcp);
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Search("slow")), ChatScript.Reply("It took too long."));
        var id = await ChatApi.CreateConversationAsync(client);

        var answer = ChatApi.SendAsync(client, id, "Go");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        clock.Advance(TimeSpan.FromSeconds(6));
        var events = await answer;

        Assert.Equal("timed_out", Assert.Single(events, e => e.Type == "tool_result").Data.GetProperty("status").GetString());
        Assert.Contains("did not finish in time", Messages(app, 1)[^1].GetProperty("content").GetString()!, StringComparison.Ordinal);
        Assert.Equal("Completed", events[^1].Data.GetProperty("status").GetString());
        var audit = Assert.Single(await ToolFixtures.AuditRowsAsync(app));
        Assert.Equal(("TimedOut", "tool.timed_out"), (audit.Outcome, audit.ErrorCode));
    }

    [Fact] // D-22: the connection is opened for the answer and reused by its calls
    public async Task TwoCallsOfOneAnswerRunInOrderOnTheSameServer()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await using var mcp = await McpTestServer.StartAsync(McpTestServer.NewTool("search"));
        await RegisterAndActivateAsync(client, mcp);
        app.ModelServer.EnqueueRounds(
            ChatScript.CallTools(Search("first", id: "call_1"), Search("second", id: "call_2")),
            ChatScript.Reply("Both done."));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Go");

        Assert.Equal(2, events.Count(e => e.Type == "tool_result" && e.Data.GetProperty("status").GetString() == "succeeded"));
        Assert.Equal(["first", "second"], mcp.Calls.Select(c => JsonDocument.Parse(c.Arguments).RootElement.GetProperty("text").GetString()));
    }

    [Fact] // AC-47
    public async Task AuditEntriesKeepTheOriginalNamesWhenTheServerIsRenamedOrDeleted()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await using var mcp = await McpTestServer.StartAsync(McpTestServer.NewTool("search"));
        await RegisterAndActivateAsync(client, mcp);
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Search("x")), ChatScript.Reply("Done."));
        var id = await ChatApi.CreateConversationAsync(client);
        await ChatApi.SendAsync(client, id, "Go");
        var server = Assert.Single(await ToolsApi.ListServersAsync(client));

        using var rename = await client.PutAsJsonAsync(ToolsApi.Server(server.Id), new { name = "documents", url = mcp.Url, scope = ToolsApi.TenantScope }, Ct);
        rename.EnsureSuccessStatusCode();
        var afterRename = await ReadAuditAsync(client);
        using var delete = await client.DeleteAsync(ToolsApi.Server(server.Id), Ct);
        delete.EnsureSuccessStatusCode();
        var afterDelete = await ReadAuditAsync(client);

        Assert.Equal(("files", "search"), Assert.Single(afterRename));
        Assert.Equal(("files", "search"), Assert.Single(afterDelete));
    }

    private static async Task<IReadOnlyList<(string? Server, string Tool)>> ReadAuditAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync(new Uri("/api/tools/audit", UriKind.Relative), Ct));
        return [.. document.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => (item.GetProperty("serverName").GetString(), item.GetProperty("toolName").GetString()!))];
    }
}
