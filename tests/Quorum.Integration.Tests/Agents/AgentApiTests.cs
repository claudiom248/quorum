using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.Tools;
using Quorum.Modules.Agents.Domain;

namespace Quorum.Integration.Tests.Agents;

/// <summary>Create, read, update and delete of agents and their sub-agent edges (spec 013, PR 3).</summary>
public sealed class AgentApiTests(PostgresFixture postgres)
{
    private static readonly Uri Agents = new("/api/agents", UriKind.Relative);
    private static readonly Uri Endpoints = new("/api/model-endpoints", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Uri Agent(Guid id) => new($"/api/agents/{id}", UriKind.Relative);

    private static object Body(
        string slug,
        Guid? endpointId,
        object[]? subAgents = null,
        string scope = "Tenant",
        Guid[]? tenants = null,
        string? description = null) => new
        {
            slug,
            displayName = slug.ToUpperInvariant(),
            description = description ?? $"Call {slug} for its speciality.",
            systemPrompt = $"You are {slug}.",
            modelEndpointId = endpointId,
            scope,
            scopeTenantIds = tenants ?? [],
            subAgents = subAgents ?? [],
        };

    private static object Edge(string slug, string? policy = null) => new { slug, failurePolicy = policy };

    private static async Task<Guid> EndpointIdAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<IdBody>>(Endpoints, Ct))!.First().Id;

    private static async Task<Guid> CreateAsync(HttpClient client, object body)
    {
        using var response = await client.PostAsJsonAsync(Agents, body, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<IdBody>(Ct))!.Id;
    }

    private static async Task<DocumentBody> GetAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<DocumentBody>(Agent(id), Ct))!;

    private static async Task<JsonElement> ErrorsAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors");

    [Fact] // AC-2
    public async Task CreatesAnAgentAndListsIt()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var endpoint = await EndpointIdAsync(client);

        using var response = await client.PostAsJsonAsync(Agents, Body("contracts-expert", endpoint), Ct);
        var list = await client.GetFromJsonAsync<List<SummaryBody>>(Agents, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<DocumentBody>(Ct);
        Assert.EndsWith($"/api/agents/{created!.Id}", response.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Equal("contracts-expert", created.Slug);
        Assert.Equal("Tenant", created.Scope);
        Assert.Equal(endpoint, created.ModelEndpointId);
        Assert.Empty(created.SubAgents);
        var summary = list!.Single(a => a.Slug == "contracts-expert");
        Assert.Equal(0, summary.ToolCount);
        Assert.Equal(0, summary.SubAgentCount);
    }

    [Theory] // AC-2
    [InlineData("")]
    [InlineData("Has Spaces")]
    [InlineData("under_score")]
    [InlineData("-leading")]
    [InlineData("this-slug-is-far-too-long-to-be-a-tool-name")]
    public async Task AMalformedSlugIsRejected(string slug)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();

        using var response = await client.PostAsJsonAsync(Agents, Body(slug, await EndpointIdAsync(client)), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await ErrorsAsync(response)).TryGetProperty("slug", out _));
    }

    [Fact] // AC-2
    public async Task ADuplicateSlugIsRejectedOnCreateAndOnUpdate()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var endpoint = await EndpointIdAsync(client);
        await CreateAsync(client, Body("researcher", endpoint));
        var other = await CreateAsync(client, Body("writer", endpoint));

        using var duplicate = await client.PostAsJsonAsync(Agents, Body("researcher", endpoint), Ct);
        using var rename = await client.PutAsJsonAsync(Agent(other), Body("researcher", endpoint), Ct);
        using var keep = await client.PutAsJsonAsync(Agent(other), Body("writer", endpoint, description: "Now it writes better."), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.True((await ErrorsAsync(duplicate)).TryGetProperty("slug", out _));
        Assert.Equal(HttpStatusCode.BadRequest, rename.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, keep.StatusCode);
        Assert.Equal("Now it writes better.", (await GetAsync(client, other)).Description);
    }

    [Fact]
    public async Task RequiredFieldsAndTheEndpointAreChecked()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();

        using var response = await client.PostAsJsonAsync(
            Agents,
            new { slug = "ok", displayName = " ", description = "", systemPrompt = "", modelEndpointId = Guid.NewGuid(), scope = "Planet", subAgents = Array.Empty<object>() },
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = await ErrorsAsync(response);
        Assert.Equal(
            ["description", "displayName", "modelEndpointId", "scope", "systemPrompt"],
            errors.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AnAgentMayHaveNoEndpointOfItsOwn()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();

        var id = await CreateAsync(client, Body("inherits", endpointId: null));

        Assert.Null((await GetAsync(client, id)).ModelEndpointId);
    }

    [Fact]
    public async Task ReadingOrChangingAnUnknownAgentIsNotFound()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var unknown = Agent(Guid.NewGuid());

        using var get = await client.GetAsync(unknown, Ct);
        using var put = await client.PutAsJsonAsync(unknown, Body("x", null), Ct);
        using var delete = await client.DeleteAsync(unknown, Ct);

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    }

    [Fact]
    public async Task TheListOfSubAgentsIsReplacedAtomically()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var endpoint = await EndpointIdAsync(client);
        await CreateAsync(client, Body("web-researcher", endpoint));
        await CreateAsync(client, Body("contracts-expert", endpoint));
        var parent = await CreateAsync(client, Body("router", endpoint, [Edge("web-researcher"), Edge("contracts-expert", "AbortTurn")]));

        var first = await GetAsync(client, parent);
        using var swap = await client.PutAsJsonAsync(Agent(parent), Body("router", endpoint, [Edge("contracts-expert", "ReturnError")]), Ct);
        var second = await GetAsync(client, parent);
        using var broken = await client.PutAsJsonAsync(Agent(parent), Body("router", endpoint, [Edge("web-researcher"), Edge("nobody")]), Ct);
        var afterBroken = await GetAsync(client, parent);

        Assert.Equal(
            [("contracts-expert", "AbortTurn"), ("web-researcher", "ReturnError")],
            first.SubAgents.Select(s => (s.Slug, s.FailurePolicy)));
        Assert.Equal(HttpStatusCode.NoContent, swap.StatusCode);
        Assert.Equal([("contracts-expert", "ReturnError")], second.SubAgents.Select(s => (s.Slug, s.FailurePolicy)));
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
        Assert.Equal([("contracts-expert", "ReturnError")], afterBroken.SubAgents.Select(s => (s.Slug, s.FailurePolicy)));
        Assert.Equal(1, (await client.GetFromJsonAsync<List<SummaryBody>>(Agents, Ct))!.Single(a => a.Slug == "router").SubAgentCount);
    }

    [Fact]
    public async Task TheSlugOfASubAgentMustExistBeUniqueAndHaveAKnownPolicy()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var endpoint = await EndpointIdAsync(client);
        await CreateAsync(client, Body("helper", endpoint));

        using var unknown = await client.PostAsJsonAsync(Agents, Body("a", endpoint, [Edge("missing")]), Ct);
        using var twice = await client.PostAsJsonAsync(Agents, Body("b", endpoint, [Edge("helper"), Edge("helper")]), Ct);
        using var policy = await client.PostAsJsonAsync(Agents, Body("c", endpoint, [Edge("helper", "Explode")]), Ct);

        foreach (var response in new[] { unknown, twice, policy })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True((await ErrorsAsync(response)).TryGetProperty("subAgents", out _));
        }
    }

    [Fact] // AC-3
    public async Task AnAgentCannotBeItsOwnSubAgent()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var endpoint = await EndpointIdAsync(client);

        using var onCreate = await client.PostAsJsonAsync(Agents, Body("loop", endpoint, [Edge("loop")]), Ct);
        var id = await CreateAsync(client, Body("loop", endpoint));
        using var onUpdate = await client.PutAsJsonAsync(Agent(id), Body("loop", endpoint, [Edge("loop")]), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, onCreate.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, onUpdate.StatusCode);
        Assert.Empty((await GetAsync(client, id)).SubAgents);
    }

    [Fact] // AC-4
    public async Task AParentOnAnEndpointWithoutToolsIsRejected()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var plain = await CreateEndpointAsync(client, "plain", supportsTools: false);
        await CreateAsync(client, Body("child", plain));

        using var response = await client.PostAsJsonAsync(Agents, Body("parent", plain, [Edge("child")]), Ct);
        using var withoutEdges = await client.PostAsJsonAsync(Agents, Body("leaf", plain), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await ErrorsAsync(response)).TryGetProperty("modelEndpointId", out _));
        Assert.Equal(HttpStatusCode.Created, withoutEdges.StatusCode);
    }

    [Fact] // AC-4
    public async Task AnEndpointThatLosesToolSupportIsRefusedWhileAnAgentNeedsThem()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var spare = await CreateEndpointAsync(client, "spare", supportsTools: true);
        var parent = await CreateAsync(client, Body("parent", spare));
        var child = await CreateAsync(client, Body("child", spare));
        using var link = await client.PutAsJsonAsync(Agent(parent), Body("parent", spare, [Edge("child")]), Ct);

        using var refused = await client.PutAsJsonAsync(EndpointUri(spare), EndpointBody("spare", supportsTools: false), Ct);
        using var cleared = await client.PutAsJsonAsync(Agent(parent), Body("parent", spare), Ct);
        using var allowed = await client.PutAsJsonAsync(EndpointUri(spare), EndpointBody("spare", supportsTools: false), Ct);

        Assert.Equal(HttpStatusCode.NoContent, link.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var references = (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("references").EnumerateArray().ToList();
        Assert.Equal(parent, Assert.Single(references).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        Assert.NotEqual(Guid.Empty, child);
    }

    [Fact] // AC-4 (tools)
    public async Task ToolsCannotBeAttachedToAnAgentOnAnEndpointWithoutTools()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var plain = await CreateEndpointAsync(client, "plain", supportsTools: false);
        var agent = await CreateAsync(client, Body("plain-agent", plain));
        var calculator = await CalculatorIdAsync(client);

        using var attach = await client.PutAsJsonAsync(ToolsApi.AgentTools(agent), new { toolIds = new[] { calculator } }, Ct);
        using var clear = await client.PutAsJsonAsync(ToolsApi.AgentTools(agent), new { toolIds = Array.Empty<Guid>() }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, attach.StatusCode);
        Assert.Equal("tool.endpoint_without_tools", await ToolsApi.ProblemCodeAsync(attach));
        Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);
    }

    [Fact] // AC-5
    public async Task AGlobalParentAcceptsOnlyGlobalChildren()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var endpoint = await EndpointIdAsync(client);
        await CreateAsync(client, Body("local-child", endpoint, scope: "Tenant"));
        await CreateAsync(client, Body("shared-child", endpoint, scope: "Global"));

        using var rejected = await client.PostAsJsonAsync(Agents, Body("shared-parent", endpoint, [Edge("local-child")], scope: "Global"), Ct);
        using var accepted = await client.PostAsJsonAsync(Agents, Body("shared-parent", endpoint, [Edge("shared-child")], scope: "Global"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.True((await ErrorsAsync(rejected)).TryGetProperty("subAgents", out _));
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
    }

    [Fact] // AC-5
    public async Task ATenantParentNeedsChildrenThatReachItsTenants()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var endpoint = await EndpointIdAsync(client);
        var (one, two) = (Guid.NewGuid(), Guid.NewGuid());
        await CreateAsync(client, Body("narrow", endpoint, tenants: [one]));
        await CreateAsync(client, Body("wide", endpoint, tenants: [one, two]));

        using var rejected = await client.PostAsJsonAsync(Agents, Body("parent-a", endpoint, [Edge("narrow")], tenants: [one, two]), Ct);
        using var accepted = await client.PostAsJsonAsync(Agents, Body("parent-b", endpoint, [Edge("wide")], tenants: [one, two]), Ct);
        using var global = await client.PostAsJsonAsync(Agents, Body("parent-c", endpoint, [Edge("narrow")], scope: "Global"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, global.StatusCode);
    }

    [Fact]
    public async Task AGlobalScopeListsNoTenants()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();

        using var response = await client.PostAsJsonAsync(Agents, Body("everywhere", await EndpointIdAsync(client), scope: "Global", tenants: [Guid.NewGuid()]), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await ErrorsAsync(response)).TryGetProperty("scopeTenantIds", out _));
    }

    [Fact] // AC-6
    public async Task NarrowingAChildThatBreaksAnEdgeIsAConflictListingTheParent()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var endpoint = await EndpointIdAsync(client);
        var other = Guid.NewGuid();
        var child = await CreateAsync(client, Body("child", endpoint, scope: "Global"));
        var parent = await CreateAsync(client, Body("parent", endpoint, [Edge("child")], tenants: [other]));

        using var refused = await client.PutAsJsonAsync(Agent(child), Body("child", endpoint, tenants: []), Ct);
        using var stillCovered = await client.PutAsJsonAsync(Agent(child), Body("child", endpoint, tenants: [other]), Ct);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var reference = Assert.Single(problem.GetProperty("references").EnumerateArray());
        Assert.Equal("agent", reference.GetProperty("kind").GetString());
        Assert.Equal(parent, reference.GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.NoContent, stillCovered.StatusCode);
        Assert.Equal("Tenant", (await GetAsync(client, child)).Scope);
    }

    [Fact] // AC-7
    public async Task AnAgentThatIsARootOrASubAgentIsNotDeletedAndListsTheReferences()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var endpoint = await EndpointIdAsync(client);
        var child = await CreateAsync(client, Body("child", endpoint));
        var parent = await CreateAsync(client, Body("parent", endpoint, [Edge("child")]));
        var root = (await client.GetFromJsonAsync<List<SummaryBody>>(Agents, Ct))!.Single(a => a.Slug == "assistant").Id;

        using var asChild = await client.DeleteAsync(Agent(child), Ct);
        using var asRoot = await client.DeleteAsync(Agent(root), Ct);

        Assert.Equal(HttpStatusCode.Conflict, asChild.StatusCode);
        var caller = Assert.Single((await asChild.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("references").EnumerateArray());
        Assert.Equal(("agent", parent), (caller.GetProperty("kind").GetString(), caller.GetProperty("id").GetGuid()));
        Assert.Equal(HttpStatusCode.Conflict, asRoot.StatusCode);
        var assistant = Assert.Single((await asRoot.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("references").EnumerateArray());
        Assert.Equal("assistant", assistant.GetProperty("kind").GetString());
    }

    [Fact] // AC-7
    public async Task AnUnreferencedAgentIsDeletedWithItsEdgesAndTools()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var endpoint = await EndpointIdAsync(client);
        var child = await CreateAsync(client, Body("child", endpoint));
        var parent = await CreateAsync(client, Body("parent", endpoint, [Edge("child")]));
        var calculator = await CalculatorIdAsync(client);
        using var attach = await client.PutAsJsonAsync(ToolsApi.AgentTools(parent), new { toolIds = new[] { calculator } }, Ct);

        using var deleteParent = await client.DeleteAsync(Agent(parent), Ct);
        using var deleteChild = await client.DeleteAsync(Agent(child), Ct);

        Assert.Equal(HttpStatusCode.NoContent, attach.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deleteParent.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deleteChild.StatusCode);
        Assert.Equal(0, await ToolFixtures.ScalarAsync<long>(app, "SELECT COUNT(*) FROM agents.agent_tools WHERE \"AgentId\" = '" + parent + "'"));
        Assert.DoesNotContain((await client.GetFromJsonAsync<List<SummaryBody>>(Agents, Ct))!, a => a.Slug is "parent" or "child");
    }

    [Fact] // AC-8
    public async Task AnEndpointUsedByAnAgentListsItWhenDeleted()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var endpoint = await CreateEndpointAsync(client, "dedicated", supportsTools: true);
        var agent = await CreateAsync(client, Body("dedicated-agent", endpoint));

        using var refused = await client.DeleteAsync(EndpointUri(endpoint), Ct);
        using var freed = await client.DeleteAsync(Agent(agent), Ct);
        using var deleted = await client.DeleteAsync(EndpointUri(endpoint), Ct);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var reference = Assert.Single((await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("references").EnumerateArray());
        Assert.Equal(agent, reference.GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.NoContent, freed.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Fact] // AC-24
    public async Task WithoutAgentsManageTheWritesAreForbidden()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        using var member = await app.CreateMemberClientAsync();
        var endpoint = await EndpointIdAsync(admin);
        var agent = await CreateAsync(admin, Body("guarded", endpoint));

        var responses = new[]
        {
            await member.GetAsync(Agent(agent), Ct),
            await member.PostAsJsonAsync(Agents, Body("intruder", endpoint), Ct),
            await member.PutAsJsonAsync(Agent(agent), Body("guarded", endpoint), Ct),
            await member.DeleteAsync(Agent(agent), Ct),
            await member.PostAsJsonAsync(Endpoints, EndpointBody("intruder", supportsTools: true), Ct),
            await member.PutAsJsonAsync(EndpointUri(endpoint), EndpointBody("renamed", supportsTools: true), Ct),
            await member.DeleteAsync(EndpointUri(endpoint), Ct),
        };

        try
        {
            Assert.All(responses, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact] // AC-26
    public void ASubAgentSlugEqualToAToolNameClashes()
    {
        // Every tool is offered as origin__name and a slug has no underscore, so the API cannot be driven into a clash today;
        // the rule keeps holding if the naming of tools ever changes.
        Assert.True(AgentRules.ClashesWithTool("ask-user", ["ask-user", "builtin__calculator"]));
        Assert.False(AgentRules.ClashesWithTool("calculator", ["builtin__calculator"]));
    }

    [Theory]
    [InlineData("Global", new int[0], "Global", new int[0], true)]
    [InlineData("Tenant", new[] { 1 }, "Global", new int[0], false)]
    [InlineData("Global", new int[0], "Tenant", new[] { 1 }, true)]
    [InlineData("Tenant", new[] { 1, 2 }, "Tenant", new[] { 1 }, true)]
    [InlineData("Tenant", new[] { 1 }, "Tenant", new[] { 1, 2 }, false)]
    [InlineData("Tenant", new int[0], "Tenant", new int[0], true)]
    public void AChildMustBeAtLeastAsWidelyVisibleAsItsParent(string child, int[] childTenants, string parent, int[] parentTenants, bool expected)
    {
        Assert.Equal(
            expected,
            AgentRules.IsVisibleAsWidelyAs(Enum.Parse<AgentScope>(child), [.. childTenants.Select(Tenant)], Enum.Parse<AgentScope>(parent), [.. parentTenants.Select(Tenant)]));

        static Guid Tenant(int n) => new(n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    private static async Task<Guid> CalculatorIdAsync(HttpClient client) =>
        (await ToolsApi.GetAssistantToolsAsync(client, await ToolsApi.DefaultAssistantIdAsync(client))).Pool.Single(t => t.Name == "builtin__calculator").ToolId;

    private static Uri EndpointUri(Guid id) => new($"/api/model-endpoints/{id}", UriKind.Relative);

    private static object EndpointBody(string name, bool supportsTools) =>
        new { name, baseUrl = QuorumApp.ModelBaseUrl, modelName = "llama3.2", contextTokenBudget = 4096, supportsTools };

    private static async Task<Guid> CreateEndpointAsync(HttpClient client, string name, bool supportsTools)
    {
        using var response = await client.PostAsJsonAsync(Endpoints, EndpointBody(name, supportsTools), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<IdBody>(Ct))!.Id;
    }


    private sealed record IdBody(Guid Id);

    private sealed record SummaryBody(Guid Id, string Slug, int SubAgentCount, int ToolCount);

    private sealed record SubAgentBody(string Slug, string FailurePolicy);

    private sealed record DocumentBody(
        Guid Id,
        string Slug,
        string DisplayName,
        string Description,
        string SystemPrompt,
        Guid? ModelEndpointId,
        string Scope,
        List<Guid> ScopeTenantIds,
        List<SubAgentBody> SubAgents);
}
