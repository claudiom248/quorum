using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.Tools;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using static Quorum.Integration.Tests.Fixtures.FakeModelServer;

namespace Quorum.Integration.Tests.Skills;

/// <summary>The skill runtime of spec 017 PR 2: the prompt block, <c>load_skill</c>, its ledger and audit entries and its scope (AC-8 to AC-17).</summary>
public sealed class SkillRuntimeTests(SharedQuorumApp host) : IClassFixture<SharedQuorumApp>, IAsyncLifetime
{
    public ValueTask InitializeAsync() => new(host.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private const string RootMarker = "Acme";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FakeToolCall Load(string name, string id = "s1") =>
        new("load_skill", JsonSerializer.Serialize(new { name }), id);

    private static string SystemPrompt(JsonElement request) =>
        request.GetProperty("messages")[0].GetProperty("content").GetString() ?? string.Empty;

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

        /// <summary>Runs on every request, before the script is chosen.</summary>
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

    private static async Task<Guid> TenantIdAsync(QuorumApp app) =>
        await ToolFixtures.ScalarAsync<Guid>(app, "SELECT \"TenantId\" FROM agents.assistants LIMIT 1");

    private static async Task<Guid> RootAgentAsync(QuorumApp app) =>
        await ToolFixtures.ScalarAsync<Guid>(app, "SELECT \"RootAgentId\" FROM agents.assistants LIMIT 1");

    private static async Task<Guid> AssistantIdAsync(QuorumApp app) =>
        await ToolFixtures.ScalarAsync<Guid>(app, "SELECT \"Id\" FROM agents.assistants LIMIT 1");

    /// <summary>Creates a skill of the tenant, assigned to the agent when one is given, and returns its id.</summary>
    private static async Task<Guid> AddSkillAsync(QuorumApp app, string slug, string description, string body, Guid? assignedTo = null)
    {
        var tenantId = await TenantIdAsync(app);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
        var skill = Skill.Create(tenantId, slug, slug, description, body, AgentScope.Tenant, [], DateTimeOffset.UtcNow);
        db.Skills.Add(skill);
        if (assignedTo is { } agentId)
        {
            db.AgentSkills.Add(AgentSkill.Create(tenantId, agentId, skill.Id));
        }

        await db.SaveChangesAsync(Ct);
        return skill.Id;
    }

    private static async Task AssignAsync(QuorumApp app, Guid agentId, Guid skillId)
    {
        var tenantId = await TenantIdAsync(app);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
        db.AgentSkills.Add(AgentSkill.Create(tenantId, agentId, skillId));
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<Guid> AddSubAgentAsync(QuorumApp app, Guid parentId, string slug, string prompt, string description)
    {
        var tenantId = await TenantIdAsync(app);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
        var child = Agent.Create(tenantId, slug, slug, description, prompt, null, AgentScope.Tenant, [], DateTimeOffset.UtcNow);
        db.Agents.Add(child);
        db.AgentSubAgents.Add(AgentSubAgent.Create(tenantId, parentId, child.Id, SubAgentFailurePolicy.ReturnError));
        await db.SaveChangesAsync(Ct);
        return child.Id;
    }

    private sealed record AuditLine(string Tool, Guid? AgentId, Guid AssistantId, string Arguments, string Outcome);

    private static async Task<List<AuditLine>> AuditAsync(QuorumApp app)
    {
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT \"ToolName\", \"AgentId\", \"AssistantId\", \"Arguments\", \"Outcome\" FROM agents.tool_audit_entries ORDER BY \"StartedAt\", \"Id\"",
            connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var lines = new List<AuditLine>();
        while (await reader.ReadAsync(Ct))
        {
            lines.Add(new AuditLine(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return lines;
    }

    [Fact] // AC-8
    public async Task TheSystemPromptListsEachSkillAndOffersLoadSkillWithoutAnyBody()
    {
        var app = host.App;
        var client = host.Admin;
        var root = await RootAgentAsync(app);
        await AddSkillAsync(app, "alpha", "Alpha does x when asked.", "BODY-ALPHA-SECRET", root);
        await AddSkillAsync(app, "beta", "Beta does y when asked.", "BODY-BETA-SECRET", root);
        new Router(app).Script(RootMarker, ChatScript.Reply("Done."));
        var id = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, id, "Hello");

        var request = RequestsOf(app, RootMarker)[0];
        var system = SystemPrompt(request);
        Assert.Contains("alpha: Alpha does x when asked.", system, StringComparison.Ordinal);
        Assert.Contains("beta: Beta does y when asked.", system, StringComparison.Ordinal);
        Assert.DoesNotContain("BODY-ALPHA-SECRET", system, StringComparison.Ordinal);
        Assert.DoesNotContain("BODY-BETA-SECRET", system, StringComparison.Ordinal);
        Assert.Contains("load_skill", ToolNames(request));
        var parameters = request.GetProperty("tools").EnumerateArray()
            .Single(t => t.GetProperty("function").GetProperty("name").GetString() == "load_skill")
            .GetProperty("function").GetProperty("parameters");
        Assert.Equal(["name"], parameters.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact] // AC-9
    public async Task LoadSkillReturnsTheBodyOfAnAssignedSkill()
    {
        var app = host.App;
        var client = host.Admin;
        var root = await RootAgentAsync(app);
        const string body = "# Alpha\n\n1. Read the brief.\n2. Answer in three lines.";
        await AddSkillAsync(app, "alpha", "Alpha does x when asked.", body, root);
        new Router(app).Script(RootMarker, ChatScript.CallTools(Load("alpha")), ChatScript.Reply("Loaded."));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Go");

        var result = Assert.Single(events, e => e.Type == "tool_result");
        Assert.Equal("succeeded", result.Data.GetProperty("status").GetString());
        Assert.Equal(body, result.Data.GetProperty("content").GetString());
    }

    [Fact] // AC-10
    public async Task AnUnknownSlugGivesAReadableErrorListingTheAgentsSkillsAndTheTurnContinues()
    {
        var app = host.App;
        var client = host.Admin;
        var root = await RootAgentAsync(app);
        await AddSkillAsync(app, "alpha", "Alpha does x.", "alpha body", root);
        await AddSkillAsync(app, "beta", "Beta does y.", "beta body", root);
        await AddSkillAsync(app, "not-assigned", "Not for this agent.", "other body");
        new Router(app).Script(RootMarker, ChatScript.CallTools(Load("gamma")), ChatScript.Reply("Sorry, carrying on."));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Go");

        var result = Assert.Single(events, e => e.Type == "tool_result");
        Assert.Equal("failed", result.Data.GetProperty("status").GetString());
        var content = result.Data.GetProperty("content").GetString()!;
        Assert.Contains("alpha", content, StringComparison.Ordinal);
        Assert.Contains("beta", content, StringComparison.Ordinal);
        Assert.DoesNotContain("not-assigned", content, StringComparison.Ordinal);
        Assert.Equal("Completed", events[^1].Data.GetProperty("status").GetString());
    }

    [Fact] // AC-11
    public async Task AnAgentWithoutSkillsHasNoSkillBlockAndNoLoadSkill()
    {
        var app = host.App;
        var client = host.Admin;
        new Router(app).Script(RootMarker, ChatScript.Reply("Hi."));
        var id = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, id, "Hello");

        var request = RequestsOf(app, RootMarker)[0];
        Assert.DoesNotContain("load_skill", ToolNames(request));
        Assert.DoesNotContain("Skills", SystemPrompt(request), StringComparison.Ordinal);
    }

    [Fact] // AC-12
    public async Task AnAgentOnAnEndpointWithoutToolsKeepsItsSkillsInert()
    {
        var app = host.App;
        var client = host.Admin;
        var root = await RootAgentAsync(app);
        await AddSkillAsync(app, "alpha", "Alpha does x.", "alpha body", root);
        await UseEndpointWithoutToolsAsync(app, root);
        new Router(app).Script(RootMarker, ChatScript.Reply("Hi."));
        var id = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, id, "Hello");

        var request = RequestsOf(app, RootMarker)[0];
        Assert.DoesNotContain("load_skill", ToolNames(request));
        Assert.DoesNotContain("alpha: Alpha does x.", SystemPrompt(request), StringComparison.Ordinal);
    }

    [Fact] // AC-13
    public async Task ASubAgentSeesOnlyItsOwnSkills()
    {
        var app = host.App;
        var client = host.Admin;
        var root = await RootAgentAsync(app);
        var child = await AddSubAgentAsync(app, root, "child", "CHILD-MARK prompt.", "Does the child work.");
        await AddSkillAsync(app, "root-skill", "Only the root uses this.", "root body", root);
        await AddSkillAsync(app, "child-skill", "Only the child uses this.", "child body", child);
        new Router(app)
            .Script(RootMarker, ChatScript.CallTools(new FakeToolCall("child", """{"task":"do it"}""", "c1")), ChatScript.Reply("Done."))
            .Script("CHILD-MARK", ChatScript.Reply("Child done."));
        var id = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, id, "Go");

        var rootSystem = SystemPrompt(RequestsOf(app, RootMarker)[0]);
        Assert.Contains("root-skill: Only the root uses this.", rootSystem, StringComparison.Ordinal);
        Assert.DoesNotContain("child-skill", rootSystem, StringComparison.Ordinal);
        var childSystem = SystemPrompt(app.ModelServer.ChatRequests.Single(r => SystemPrompt(r).Contains("CHILD-MARK", StringComparison.Ordinal)));
        Assert.Contains("child-skill: Only the child uses this.", childSystem, StringComparison.Ordinal);
        Assert.DoesNotContain("root-skill", childSystem, StringComparison.Ordinal);
    }

    [Fact] // AC-14
    public async Task ABodyLargerThanTheResultLimitIsRefusedNotTruncated()
    {
        var app = host.App;
        var client = host.Admin;
        var root = await RootAgentAsync(app);
        var body = "# Big\n\n" + new string('x', ToolSettings.DefaultMaxResultCharacters);
        await AddSkillAsync(app, "big", "A very long procedure.", body, root);
        new Router(app).Script(RootMarker, ChatScript.CallTools(Load("big")), ChatScript.Reply("Noted."));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Go");

        var result = Assert.Single(events, e => e.Type == "tool_result");
        Assert.Equal("failed", result.Data.GetProperty("status").GetString());
        Assert.StartsWith("The result is too large", result.Data.GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 100), result.Data.GetProperty("content").GetString()!, StringComparison.Ordinal);
    }

    [Fact] // AC-15
    public async Task LoadSkillIsAnOrdinaryToolCallWithoutConfirmation()
    {
        var app = host.App;
        var client = host.Admin;
        var root = await RootAgentAsync(app);
        await AddSkillAsync(app, "alpha", "Alpha does x.", "alpha body", root);
        new Router(app).Script(RootMarker, ChatScript.CallTools(Load("alpha")), ChatScript.Reply("Loaded."));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Go");

        var call = Assert.Single(events, e => e.Type == "tool_call");
        Assert.Equal("load_skill", call.Data.GetProperty("tool").GetString());
        Assert.False(call.Data.GetProperty("requiresConfirmation").GetBoolean());
        Assert.False(call.Data.GetProperty("isDelegation").GetBoolean());
        Assert.Contains("alpha", call.Data.GetProperty("arguments").GetRawText(), StringComparison.Ordinal);
    }

    [Fact] // AC-16
    public async Task TheAuditEntryHoldsTheSlugAndTheOutcomeButNotTheBody()
    {
        var app = host.App;
        var client = host.Admin;
        var root = await RootAgentAsync(app);
        var assistant = await AssistantIdAsync(app);
        const string body = "AUDIT-BODY-MUST-NOT-APPEAR";
        await AddSkillAsync(app, "alpha", "Alpha does x.", body, root);
        new Router(app).Script(RootMarker, ChatScript.CallTools(Load("alpha")), ChatScript.Reply("Loaded."));
        var id = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, id, "Go");

        var entry = Assert.Single(await AuditAsync(app), line => line.Tool == "load_skill");
        Assert.Equal(root, entry.AgentId);
        Assert.Equal(assistant, entry.AssistantId);
        Assert.Equal("Succeeded", entry.Outcome);
        Assert.Contains("alpha", entry.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain(body, entry.Arguments, StringComparison.Ordinal);
    }

    [Fact] // AC-17
    public async Task AnEditDuringTheTurnDoesNotChangeWhatTheTurnLoads()
    {
        var app = host.App;
        var client = host.Admin;
        var root = await RootAgentAsync(app);
        var skill = await AddSkillAsync(app, "alpha", "Alpha does x.", "ORIGINAL-BODY", root);
        var router = new Router(app).Script(RootMarker, ChatScript.CallTools(Load("alpha")), ChatScript.Reply("Loaded."));
        var edited = false;
        router.OnRequest = system =>
        {
            if (!edited && system.Contains(RootMarker, StringComparison.Ordinal))
            {
                edited = true;
                ReviseBodyAsync(app, skill, "EDITED-BODY").GetAwaiter().GetResult();
            }
        };
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Go");

        Assert.True(edited);
        var result = Assert.Single(events, e => e.Type == "tool_result");
        Assert.Equal("ORIGINAL-BODY", result.Data.GetProperty("content").GetString());
    }

    private static async Task ReviseBodyAsync(QuorumApp app, Guid skillId, string body)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
        var skill = await db.Skills.SingleAsync(s => s.Id == skillId);
        skill.Revise(skill.Slug, skill.DisplayName, skill.Description, body, skill.Scope, skill.ScopeTenantIds, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
    }

    /// <summary>Moves the root agent to an endpoint that does not support tools (spec 017 D-16).</summary>
    private static async Task UseEndpointWithoutToolsAsync(QuorumApp app, Guid agentId)
    {
        var tenantId = await TenantIdAsync(app);
        const string baseUrl = "http://plain-endpoint.test:11434/v1";
        app.ModelServer.Serve(baseUrl, "plain-model");
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
        var endpoint = ModelEndpoint.Create(tenantId, "plain-endpoint", new Uri(baseUrl), "plain-model", null, 4096, supportsTools: false, DateTimeOffset.UtcNow);
        db.ModelEndpoints.Add(endpoint);
        var agent = await db.Agents.IgnoreQueryFilters().SingleAsync(a => a.Id == agentId);
        agent.Revise(agent.Slug, agent.DisplayName, agent.Description, agent.SystemPrompt, endpoint.Id, agent.Scope, agent.ScopeTenantIds, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(Ct);
    }
}
