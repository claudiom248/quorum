using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.Tools;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using static Quorum.Integration.Tests.Fixtures.FakeModelServer;

namespace Quorum.Integration.Tests.Agents;

/// <summary>The delegation runtime of spec 013: a root agent calls its sub-agents as tools (AC-9 to AC-18, AC-20, AC-21, AC-25, AC-28 to AC-31).</summary>
public sealed class DelegationTests(PostgresFixture postgres)
{
    private const string RootMarker = "Acme";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FakeToolCall Delegate(string slug, string task, string id) =>
        new(slug, JsonSerializer.Serialize(new { task }), id);

    private static FakeToolCall Probe(string text, string id) =>
        new("builtin__probe", JsonSerializer.Serialize(new { text }), id);

    private static string SystemPrompt(JsonElement request) =>
        request.GetProperty("messages")[0].GetProperty("content").GetString() ?? string.Empty;

    private static List<JsonElement> Messages(JsonElement request) => request.GetProperty("messages").EnumerateArray().ToList();

    private static string[] ToolNames(JsonElement request) =>
        request.TryGetProperty("tools", out var tools)
            ? [.. tools.EnumerateArray().Select(tool => tool.GetProperty("function").GetProperty("name").GetString()!)]
            : [];

    private static List<JsonElement> RequestsOf(QuorumApp app, string marker) =>
        [.. app.ModelServer.ChatRequests.Where(request => SystemPrompt(request).Contains(marker, StringComparison.Ordinal))];

    /// <summary>Answers each chat request from the queue of the agent whose system prompt carries the marker, since several agents talk to the model at once.</summary>
    private sealed class Router
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<ChatScript>> _scripts = new(StringComparer.Ordinal);

        public Router(QuorumApp app) => app.ModelServer.Responder = Pick;

        public Router Script(string marker, params ChatScript[] scripts)
        {
            var queue = _scripts.GetOrAdd(marker, _ => new ConcurrentQueue<ChatScript>());
            foreach (var script in scripts)
            {
                queue.Enqueue(script);
            }

            return this;
        }

        /// <summary>Runs when the agent of <paramref name="marker"/> makes its first request.</summary>
        public Action<string>? OnRequest { get; set; }

        private ChatScript? Pick(JsonElement request)
        {
            var system = SystemPrompt(request);
            OnRequest?.Invoke(system);
            foreach (var (marker, queue) in _scripts)
            {
                if (system.Contains(marker, StringComparison.Ordinal) && queue.TryDequeue(out var script))
                {
                    return script;
                }
            }

            return null;
        }
    }

    private static async Task<Guid> RootAgentAsync(QuorumApp app) =>
        await ToolFixtures.ScalarAsync<Guid>(app, "SELECT \"RootAgentId\" FROM agents.assistants LIMIT 1");

    private static async Task<Guid> AddSubAgentAsync(
        QuorumApp app,
        Guid parentId,
        string slug,
        string prompt,
        SubAgentFailurePolicy policy = SubAgentFailurePolicy.ReturnError,
        string? description = null,
        Guid? endpointId = null)
    {
        var tenantId = await ToolFixtures.ScalarAsync<Guid>(app, "SELECT \"TenantId\" FROM agents.assistants LIMIT 1");
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
        var child = Agent.Create(
            tenantId, slug, slug, description ?? $"Does the work of {slug}.", prompt, endpointId, AgentScope.Tenant, [], DateTimeOffset.UtcNow);
        db.Agents.Add(child);
        db.AgentSubAgents.Add(AgentSubAgent.Create(tenantId, parentId, child.Id, policy));
        await db.SaveChangesAsync(Ct);
        return child.Id;
    }

    private static async Task<Guid> AddEndpointAsync(QuorumApp app, string baseUrl, string modelName)
    {
        var tenantId = await ToolFixtures.ScalarAsync<Guid>(app, "SELECT \"TenantId\" FROM agents.assistants LIMIT 1");
        app.ModelServer.Serve(baseUrl, modelName);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
        var endpoint = ModelEndpoint.Create(tenantId, modelName, new Uri(baseUrl), modelName, null, 4096, supportsTools: true, DateTimeOffset.UtcNow);
        db.ModelEndpoints.Add(endpoint);
        await db.SaveChangesAsync(Ct);
        return endpoint.Id;
    }

    /// <summary>Attaches the probe tool to one agent, creating its row on first use.</summary>
    private static async Task AttachProbeAsync(QuorumApp app, Guid agentId, ToolPolicy policy = ToolPolicy.Auto)
    {
        var tenantId = await ToolFixtures.ScalarAsync<Guid>(app, "SELECT \"TenantId\" FROM agents.assistants LIMIT 1");
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
        var tool = db.Tools.FirstOrDefault(t => t.Name == ProbeTool.ToolName);
        if (tool is null)
        {
            tool = Tool.CreateBuiltin(tenantId, ProbeTool.ToolName, "A tool that tests control.", ToolFixtures.ProbeSchema);
            tool.Review(ToolState.Enabled, policy);
            db.Tools.Add(tool);
        }

        db.AgentTools.Add(AgentTool.Create(tenantId, agentId, tool.Id));
        await db.SaveChangesAsync(Ct);
    }

    private static async Task AttachBuiltinAsync(QuorumApp app, Guid agentId, string name)
    {
        var tenantId = await ToolFixtures.ScalarAsync<Guid>(app, "SELECT \"TenantId\" FROM agents.assistants LIMIT 1");
        var toolId = await ToolFixtures.ScalarAsync<Guid>(app, $"SELECT \"Id\" FROM agents.tools WHERE \"Name\" = '{name}' AND \"Origin\" = 'Builtin'");
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
        db.AgentTools.Add(AgentTool.Create(tenantId, agentId, toolId));
        await db.SaveChangesAsync(Ct);
    }

    private sealed record AuditLine(string Tool, string? Server, Guid? AgentId, Guid AssistantId, string Arguments, string Outcome, int? ResultCharacters);

    private static async Task<List<AuditLine>> AuditAsync(QuorumApp app)
    {
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT \"ToolName\", \"ServerName\", \"AgentId\", \"AssistantId\", \"Arguments\", \"Outcome\", \"ResultCharacters\" FROM agents.tool_audit_entries ORDER BY \"StartedAt\", \"Id\"",
            connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var lines = new List<AuditLine>();
        while (await reader.ReadAsync(Ct))
        {
            lines.Add(new AuditLine(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.GetGuid(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6)));
        }

        return lines;
    }

    private static ChatScript SlowReply(string text) => new([text], TimeSpan.FromMilliseconds(400), null);

    [Fact] // AC-9
    public async Task TheModelSeesAToolNamedByTheSubAgentSlugAndCallingItReturnsTheSubAgentsFinalMessage()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await AddSubAgentAsync(app, await RootAgentAsync(app), "researcher", "RESEARCHER prompt.", description: "Researches facts. Call it with a topic.");
        new Router(app)
            .Script(RootMarker, ChatScript.CallTools(Delegate("researcher", "find the capital of France", "c1")), ChatScript.Reply("It is ", "Paris."))
            .Script("RESEARCHER", ChatScript.Reply("The capital is ", "Paris."));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "What is the capital of France?");

        var tool = RequestsOf(app, RootMarker)[0].GetProperty("tools").EnumerateArray()
            .Single(t => t.GetProperty("function").GetProperty("name").GetString() == "researcher").GetProperty("function");
        Assert.Equal("Researches facts. Call it with a topic.", tool.GetProperty("description").GetString());
        Assert.Equal(["task"], tool.GetProperty("parameters").GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        var call = Assert.Single(events, e => e.Type == "tool_call");
        Assert.Equal("researcher", call.Data.GetProperty("tool").GetString());
        var result = Assert.Single(events, e => e.Type == "tool_result");
        Assert.Equal("succeeded", result.Data.GetProperty("status").GetString());
        Assert.Equal("The capital is Paris.", result.Data.GetProperty("content").GetString());
        Assert.Equal("The capital is Paris.", Messages(RequestsOf(app, RootMarker)[1])[^1].GetProperty("content").GetString());
        Assert.Equal("It is Paris.", SseReader.Text(events));
        Assert.Equal("Completed", events[^1].Data.GetProperty("status").GetString());
    }

    [Fact] // D-21
    public async Task AGlobalSubAgentOwnedByAnotherTenantIsOfferedAndRun()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await AddSubAgentAsync(app, await RootAgentAsync(app), "shared", "SHARED prompt.");
        var owner = Guid.NewGuid();
        await ToolFixtures.ExecuteAsync(app, $"UPDATE agents.agents SET \"TenantId\" = '{owner}', \"Scope\" = 'Global' WHERE \"Slug\" = 'shared'");
        await ToolFixtures.ExecuteAsync(app, $"UPDATE agents.agent_sub_agents SET \"TenantId\" = '{owner}'");
        var sharedId = await ToolFixtures.ScalarAsync<Guid>(app, "SELECT \"Id\" FROM agents.agents WHERE \"Slug\" = 'shared'");
        await using (var scope = app.Services.CreateAsyncScope())
        {
            // The tool and its activation are the owner's, as they were when the owner shared the agent.
            var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
            var tool = Tool.CreateBuiltin(owner, ProbeTool.ToolName, "A tool that tests control.", ToolFixtures.ProbeSchema);
            tool.Review(ToolState.Enabled, ToolPolicy.Auto);
            db.Tools.Add(tool);
            db.AgentTools.Add(AgentTool.Create(owner, sharedId, tool.Id));
            await db.SaveChangesAsync(Ct);
        }

        new Router(app)
            .Script(RootMarker, ChatScript.CallTools(Delegate("shared", "work", "c1")), ChatScript.Reply("Done."))
            .Script("SHARED", ChatScript.CallTools(Probe("fact", "p1")), ChatScript.Reply("shared answer"));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Go");

        Assert.Equal("succeeded", events.Last(e => e.Type == "tool_result").Data.GetProperty("status").GetString());
        Assert.Equal("shared answer", events.Last(e => e.Type == "tool_result").Data.GetProperty("content").GetString());
        Assert.Equal(["builtin__probe"], ToolNames(RequestsOf(app, "SHARED")[0]));
        Assert.Single(app.Probe.Calls);
    }

    [Fact] // AC-10
    public async Task TheSubAgentReceivesOnlyTheTaskNeverTheConversation()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await AddSubAgentAsync(app, await RootAgentAsync(app), "researcher", "RESEARCHER prompt.");
        new Router(app)
            .Script(RootMarker, ChatScript.CallTools(Delegate("researcher", "look up zebras", "c1")), ChatScript.Reply("Done."))
            .Script("RESEARCHER", ChatScript.Reply("Zebras are striped."));
        var id = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, id, "My secret word is pineapple, now look into zebras");

        var request = Assert.Single(RequestsOf(app, "RESEARCHER"));
        var messages = Messages(request);
        Assert.Equal(["system", "user"], messages.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal("look up zebras", messages[1].GetProperty("content").GetString());
        Assert.DoesNotContain("pineapple", request.GetRawText(), StringComparison.Ordinal);
    }

    [Fact] // AC-11
    public async Task ASubAgentWithoutAnEndpointUsesTheRootAgentsAndOneWithItsOwnUsesIt()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var root = await RootAgentAsync(app);
        var endpoint = await AddEndpointAsync(app, "http://other.test:11434/v1", "child-model");
        await AddSubAgentAsync(app, root, "inheriting", "INHERITING prompt.");
        await AddSubAgentAsync(app, root, "owning", "OWNING prompt.", endpointId: endpoint);
        new Router(app)
            .Script(RootMarker, ChatScript.CallTools(Delegate("inheriting", "a", "c1"), Delegate("owning", "b", "c2")), ChatScript.Reply("Done."))
            .Script("INHERITING", ChatScript.Reply("a done"))
            .Script("OWNING", ChatScript.Reply("b done"));
        var id = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, id, "Go");

        Assert.Equal("llama3.2", Assert.Single(RequestsOf(app, "INHERITING")).GetProperty("model").GetString());
        Assert.Equal("child-model", Assert.Single(RequestsOf(app, "OWNING")).GetProperty("model").GetString());
    }

    [Fact] // AC-12
    public async Task ASubAgentThatHasSubAgentsOfItsOwnOffersNoDelegationToolsWhenItRunsAsOne()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var middle = await AddSubAgentAsync(app, await RootAgentAsync(app), "middle", "MIDDLE prompt.");
        await AddSubAgentAsync(app, middle, "leaf", "LEAF prompt.");
        await AttachBuiltinAsync(app, middle, "calculator");
        new Router(app)
            .Script(RootMarker, ChatScript.CallTools(Delegate("middle", "work", "c1")), ChatScript.Reply("Done."))
            .Script("MIDDLE", ChatScript.Reply("worked"));
        var id = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, id, "Go");

        Assert.Equal(["builtin__calculator"], ToolNames(Assert.Single(RequestsOf(app, "MIDDLE"))));
        Assert.Empty(RequestsOf(app, "LEAF"));
    }

    [Fact] // AC-13
    public async Task EachAgentHasOnlyItsOwnTools()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var worker = await AddSubAgentAsync(app, await RootAgentAsync(app), "worker", "WORKER prompt.");
        await AttachProbeAsync(app, worker);
        new Router(app)
            .Script(RootMarker, ChatScript.CallTools(Delegate("worker", "work", "c1")), ChatScript.Reply("Done."))
            .Script("WORKER", ChatScript.Reply("worked"));
        var id = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, id, "Go");

        Assert.Equal(["builtin__probe"], ToolNames(Assert.Single(RequestsOf(app, "WORKER"))));
        var rootTools = ToolNames(RequestsOf(app, RootMarker)[0]);
        Assert.Contains("builtin__calculator", rootTools);
        Assert.Contains("worker", rootTools);
        Assert.DoesNotContain("builtin__probe", rootTools);
    }

    [Fact] // AC-31
    public async Task AskUserIsNotOfferedToAnAgentRunningAsASubAgent()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var worker = await AddSubAgentAsync(app, await RootAgentAsync(app), "worker", "WORKER prompt.");
        await AttachBuiltinAsync(app, worker, "ask_user");
        await AttachBuiltinAsync(app, worker, "calculator");
        new Router(app)
            .Script(RootMarker, ChatScript.CallTools(Delegate("worker", "work", "c1")), ChatScript.Reply("Done."))
            .Script("WORKER", ChatScript.Reply("worked"));
        var id = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, id, "Go");

        var request = Assert.Single(RequestsOf(app, "WORKER"));
        Assert.Equal(["builtin__calculator"], ToolNames(request));
        Assert.DoesNotContain("ask_user", SystemPrompt(request), StringComparison.Ordinal);
    }

    [Theory] // AC-14
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public async Task TheAssistantsLimitDecidesHowManySubAgentsRunAtTheSameTime(int limit, int expectedAtOnce)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var root = await RootAgentAsync(app);
        await AddSubAgentAsync(app, root, "alpha", "ALPHA prompt.");
        await AddSubAgentAsync(app, root, "beta", "BETA prompt.");
        await ToolFixtures.ExecuteAsync(app, $"UPDATE agents.assistants SET \"MaxParallelSubAgents\" = {limit}");
        new Router(app)
            .Script(RootMarker, ChatScript.CallTools(Delegate("alpha", "a", "c1"), Delegate("beta", "b", "c2")), ChatScript.Reply("Done."))
            .Script("ALPHA", SlowReply("alpha done"))
            .Script("BETA", SlowReply("beta done"));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Go");

        Assert.Equal(expectedAtOnce, app.ModelServer.MaxConcurrentChats);
        Assert.Equal(["alpha done", "beta done"], events.Where(e => e.Type == "tool_result").Select(e => e.Data.GetProperty("content").GetString()).Order());
        Assert.Equal("Completed", events[^1].Data.GetProperty("status").GetString());
    }

    [Fact] // AC-29
    public async Task DelegationsOfARoundRunTogetherWhileOrdinaryToolsRunOneAtATime()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var root = await RootAgentAsync(app);
        await AddSubAgentAsync(app, root, "alpha", "ALPHA prompt.");
        await AddSubAgentAsync(app, root, "beta", "BETA prompt.");
        await ToolFixtures.AddProbeAsync(app);
        await ToolFixtures.ExecuteAsync(app, "UPDATE agents.assistants SET \"MaxParallelSubAgents\" = 2");
        var running = 0;
        var maxRunning = 0;
        app.Probe.Handler = async (_, token) =>
        {
            var now = Interlocked.Increment(ref running);
            Interlocked.Exchange(ref maxRunning, Math.Max(Volatile.Read(ref maxRunning), now));
            await Task.Delay(150, token);
            Interlocked.Decrement(ref running);
            return "probe result";
        };
        new Router(app)
            .Script(
                RootMarker,
                ChatScript.CallTools(Delegate("alpha", "a", "c1"), Delegate("beta", "b", "c2"), Probe("one", "c3"), Probe("two", "c4")),
                ChatScript.Reply("Done."))
            .Script("ALPHA", SlowReply("alpha done"))
            .Script("BETA", SlowReply("beta done"));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Go");

        Assert.Equal(2, app.ModelServer.MaxConcurrentChats);
        Assert.Equal(1, maxRunning);
        Assert.Equal(2, app.Probe.Calls.Count);
        Assert.Equal(4, events.Count(e => e.Type == "tool_result"));
        Assert.Equal("Completed", events[^1].Data.GetProperty("status").GetString());
    }

    [Fact] // AC-15
    public async Task AFailingSubAgentGivesTheModelAReadableErrorAndTheTurnContinues()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await AddSubAgentAsync(app, await RootAgentAsync(app), "researcher", "RESEARCHER prompt.");
        new Router(app)
            .Script(RootMarker, ChatScript.CallTools(Delegate("researcher", "work", "c1")), ChatScript.Reply("I could not ask the researcher."))
            .Script("RESEARCHER", ChatScript.Reply("unused") with { HttpErrorStatus = 500 });
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Go");

        var result = Assert.Single(events, e => e.Type == "tool_result");
        Assert.Equal("failed", result.Data.GetProperty("status").GetString());
        var content = result.Data.GetProperty("content").GetString()!;
        Assert.Contains("researcher", content, StringComparison.Ordinal);
        Assert.Contains("failed", content, StringComparison.Ordinal);
        Assert.Equal(content, Messages(RequestsOf(app, RootMarker)[1])[^1].GetProperty("content").GetString());
        Assert.Equal("I could not ask the researcher.", SseReader.Text(events));
        Assert.Equal("Completed", events[^1].Data.GetProperty("status").GetString());
    }

    [Fact] // AC-16
    public async Task AFailureOnAnAbortTurnEdgeEndsTheTurnWithAnErrorToTheUser()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await AddSubAgentAsync(app, await RootAgentAsync(app), "researcher", "RESEARCHER prompt.", SubAgentFailurePolicy.AbortTurn);
        new Router(app)
            .Script(RootMarker, ChatScript.CallTools(Delegate("researcher", "work", "c1")), ChatScript.Reply("Should not be asked."))
            .Script("RESEARCHER", ChatScript.Reply("unused") with { HttpErrorStatus = 500 });
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Go");

        var done = events[^1].Data;
        Assert.Equal("Failed", done.GetProperty("status").GetString());
        Assert.Contains("researcher", done.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Single(RequestsOf(app, RootMarker));
    }

    [Fact] // AC-17
    public async Task AFinalMessageOverTheResultLimitIsNotTruncatedAndTheCallerGetsTheTooLargeError()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await ToolFixtures.SetLimitsAsync(client, maxResult: 1000);
        await AddSubAgentAsync(app, await RootAgentAsync(app), "researcher", "RESEARCHER prompt.");
        new Router(app)
            .Script(RootMarker, ChatScript.CallTools(Delegate("researcher", "work", "c1")), ChatScript.Reply("Too long."))
            .Script("RESEARCHER", ChatScript.Reply(new string('x', 1500)));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Go");

        var result = Assert.Single(events, e => e.Type == "tool_result");
        Assert.Equal("failed", result.Data.GetProperty("status").GetString());
        var content = result.Data.GetProperty("content").GetString()!;
        Assert.Contains("too large", content, StringComparison.Ordinal);
        Assert.DoesNotContain("xxxxx", content, StringComparison.Ordinal);
        Assert.Equal(content, Messages(RequestsOf(app, RootMarker)[1])[^1].GetProperty("content").GetString());
        Assert.Equal(1500, (await AuditAsync(app)).Single(line => line.Tool == "researcher").ResultCharacters);
    }

    [Fact] // AC-18
    public async Task StoppingTheAnswerCancelsTheSubAgentAndItsToolCall()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var worker = await AddSubAgentAsync(app, await RootAgentAsync(app), "worker", "WORKER prompt.");
        await AttachProbeAsync(app, worker);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Probe.Handler = async (_, token) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
                throw;
            }

            return "never";
        };
        new Router(app)
            .Script(RootMarker, ChatScript.Reply("Delegating. ") with { ToolCalls = [Delegate("worker", "work", "c1")] })
            .Script("WORKER", ChatScript.CallTools(Probe("slow", "p1")));
        var id = await ChatApi.CreateConversationAsync(client);

        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ChatApi.Messages(id)) { Content = JsonContent.Create(new { content = "Go" }) };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            await stop.CancelAsync();
        }

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await ChatApi.WaitForLastMessageAsync(client, id, "Interrupted");
        // The entries are completed as the cancellation unwinds, which can be just after the message is stored.
        var audit = await AuditAsync(app);
        for (var attempt = 0; attempt < 50 && (audit.Count < 2 || audit.Any(line => line.Outcome == "Started")); attempt++)
        {
            await Task.Delay(100, Ct);
            audit = await AuditAsync(app);
        }

        Assert.Equal(["Cancelled", "Cancelled"], audit.Select(line => line.Outcome));
        Assert.Equal(["worker", "probe"], audit.Select(line => line.Tool));
    }

    [Fact] // AC-20, AC-21, D-28
    public async Task TheAuditLogHoldsTheDelegationAndTheSubAgentsToolCallsWithTheirAgentButNoSubAgentText()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var root = await RootAgentAsync(app);
        var worker = await AddSubAgentAsync(app, root, "worker", "WORKER prompt.");
        await AttachProbeAsync(app, worker);
        var assistant = await ToolFixtures.ScalarAsync<Guid>(app, "SELECT \"Id\" FROM agents.assistants LIMIT 1");
        new Router(app)
            .Script(RootMarker, ChatScript.CallTools(Delegate("worker", "check the facts", "c1")), ChatScript.Reply("Done."))
            .Script("WORKER", ChatScript.Reply("Thinking secret. ") with { ToolCalls = [Probe("fact", "p1")] }, ChatScript.Reply("Final secret answer."));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Go");

        var audit = await AuditAsync(app);
        Assert.Equal(["worker", "probe"], audit.Select(line => line.Tool).ToArray());
        var delegation = audit[0];
        Assert.Equal(("agent", root, assistant, "Succeeded"), (delegation.Server, delegation.AgentId, delegation.AssistantId, delegation.Outcome));
        Assert.Contains("check the facts", delegation.Arguments, StringComparison.Ordinal);
        Assert.Equal("Final secret answer.".Length, delegation.ResultCharacters);
        var inner = audit[1];
        Assert.Equal((worker, assistant, "Succeeded"), (inner.AgentId, inner.AssistantId, inner.Outcome));
        Assert.DoesNotContain(audit, line => line.Arguments.Contains("secret", StringComparison.Ordinal));
        Assert.DoesNotContain("secret", SseReader.Text(events), StringComparison.Ordinal);
    }

    [Fact] // D-11, D-32
    public async Task TheSubAgentsToolCallsAreNestedUnderTheDelegationAndItsTextIsNotStreamed()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var worker = await AddSubAgentAsync(app, await RootAgentAsync(app), "worker", "WORKER prompt.");
        await AttachProbeAsync(app, worker);
        new Router(app)
            .Script(RootMarker, ChatScript.CallTools(Delegate("worker", "work", "c1")), ChatScript.Reply("Done."))
            .Script("WORKER", ChatScript.Reply("Thinking secret. ") with { ToolCalls = [Probe("fact", "p1")] }, ChatScript.Reply("Final secret answer."));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Go");

        var calls = events.Where(e => e.Type == "tool_call").ToList();
        Assert.Equal(["worker", "builtin__probe"], calls.Select(e => e.Data.GetProperty("tool").GetString()));
        var delegationId = calls[0].Data.GetProperty("callId").GetString();
        Assert.False(calls[0].Data.TryGetProperty("parentCallId", out var rootParent) && rootParent.ValueKind != JsonValueKind.Null);
        Assert.Equal(delegationId, calls[1].Data.GetProperty("parentCallId").GetString());
        Assert.Equal("worker", calls[1].Data.GetProperty("agentSlug").GetString());
        var results = events.Where(e => e.Type == "tool_result").ToList();
        Assert.Equal(delegationId, results[^1].Data.GetProperty("callId").GetString());
        Assert.Equal("Done.", SseReader.Text(events));
    }

    [Fact] // AC-25
    public async Task EditingAnAgentWhileATurnRunsDoesNotAffectThatTurn()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await AddSubAgentAsync(app, await RootAgentAsync(app), "researcher", "RESEARCHER original prompt.");
        var router = new Router(app)
            .Script(RootMarker, ChatScript.CallTools(Delegate("researcher", "work", "c1")), ChatScript.Reply("Done."))
            .Script("RESEARCHER", ChatScript.Reply("researched"));
        var edited = 0;
        router.OnRequest = system =>
        {
            if (system.Contains(RootMarker, StringComparison.Ordinal) && Interlocked.Exchange(ref edited, 1) == 0)
            {
                ToolFixtures.ExecuteAsync(app, "UPDATE agents.agents SET \"SystemPrompt\" = 'CHANGED prompt.', \"Slug\" = 'renamed' WHERE \"Slug\" = 'researcher'")
                    .GetAwaiter().GetResult();
            }
        };
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Go");

        Assert.Equal(1, edited);
        Assert.Contains("RESEARCHER original prompt.", SystemPrompt(Assert.Single(RequestsOf(app, "RESEARCHER"))), StringComparison.Ordinal);
        Assert.Equal("succeeded", Assert.Single(events, e => e.Type == "tool_result").Data.GetProperty("status").GetString());
    }

    [Fact] // AC-28
    public async Task AConfirmToolOfASubAgentAsksTheUserInTheNestedEntryAndRunsOnApproval()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var worker = await AddSubAgentAsync(app, await RootAgentAsync(app), "worker", "WORKER prompt.");
        await AttachProbeAsync(app, worker, ToolPolicy.Confirm);
        new Router(app)
            .Script(RootMarker, ChatScript.CallTools(Delegate("worker", "work", "c1")), ChatScript.Reply("Done."))
            .Script("WORKER", ChatScript.CallTools(Probe("danger", "p1")), ChatScript.Reply("worker finished"));
        var id = await ChatApi.CreateConversationAsync(client);
        await using var answer = await LiveAnswer.StartAsync(client, id, "Go");

        var delegation = await answer.UntilAsync("tool_call");
        var nested = await answer.UntilAsync("tool_call");

        Assert.False(delegation.Data.GetProperty("requiresConfirmation").GetBoolean());
        Assert.True(nested.Data.GetProperty("requiresConfirmation").GetBoolean());
        Assert.Equal("worker", nested.Data.GetProperty("agentSlug").GetString());
        Assert.Equal(delegation.Data.GetProperty("callId").GetString(), nested.Data.GetProperty("parentCallId").GetString());
        Assert.Equal("danger", nested.Data.GetProperty("arguments").GetProperty("text").GetString());
        Assert.Empty(app.Probe.Calls);

        using var decision = await ChatApi.DecideAsync(client, id, Guid.Parse(nested.Data.GetProperty("callId").GetString()!), approved: true);
        var events = await answer.FinishAsync();

        Assert.Equal(HttpStatusCode.NoContent, decision.StatusCode);
        Assert.Single(app.Probe.Calls);
        Assert.Equal("Completed", events[^1].Data.GetProperty("status").GetString());
    }

    [Fact] // AC-28
    public async Task DenyingAConfirmToolOfASubAgentTellsTheSubAgentAndNothingRuns()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var worker = await AddSubAgentAsync(app, await RootAgentAsync(app), "worker", "WORKER prompt.");
        await AttachProbeAsync(app, worker, ToolPolicy.Confirm);
        new Router(app)
            .Script(RootMarker, ChatScript.CallTools(Delegate("worker", "work", "c1")), ChatScript.Reply("Done."))
            .Script("WORKER", ChatScript.CallTools(Probe("danger", "p1")), ChatScript.Reply("denied, so I stopped"));
        var id = await ChatApi.CreateConversationAsync(client);
        await using var answer = await LiveAnswer.StartAsync(client, id, "Go");
        await answer.UntilAsync("tool_call");
        var nested = await answer.UntilAsync("tool_call");

        using var decision = await ChatApi.DecideAsync(client, id, Guid.Parse(nested.Data.GetProperty("callId").GetString()!), approved: false);
        var events = await answer.FinishAsync();

        Assert.Equal(HttpStatusCode.NoContent, decision.StatusCode);
        Assert.Empty(app.Probe.Calls);
        Assert.Contains("denied by the user", Messages(RequestsOf(app, "WORKER")[1])[^1].GetProperty("content").GetString()!, StringComparison.Ordinal);
        Assert.Equal("Completed", events[^1].Data.GetProperty("status").GetString());
    }

    [Fact] // AC-30
    public async Task ADelegationThatRunsLongerThanTheCallTimeoutIsNotCut()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await ToolFixtures.SetLimitsAsync(client, callSeconds: 1);
        await AddSubAgentAsync(app, await RootAgentAsync(app), "researcher", "RESEARCHER prompt.");
        new Router(app)
            .Script(RootMarker, ChatScript.CallTools(Delegate("researcher", "work", "c1")), ChatScript.Reply("Done."))
            .Script("RESEARCHER", new ChatScript(["slow ", "answer"], TimeSpan.FromMilliseconds(1100), null));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Go");

        var result = Assert.Single(events, e => e.Type == "tool_result");
        Assert.Equal("succeeded", result.Data.GetProperty("status").GetString());
        Assert.Equal("slow answer", result.Data.GetProperty("content").GetString());
    }
}
