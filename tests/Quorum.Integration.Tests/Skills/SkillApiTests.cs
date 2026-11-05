using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.Skills;

/// <summary>Skills, their assignment to agents, scope rules and the display name style (spec 017, PR 1).</summary>
public sealed class SkillApiTests(SharedQuorumApp host) : IClassFixture<SharedQuorumApp>, IAsyncLifetime
{
    public ValueTask InitializeAsync() => new(host.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static readonly Uri Skills = new("/api/skills", UriKind.Relative);
    private static readonly Uri Settings = new("/api/skills/settings", UriKind.Relative);
    private static readonly Uri Agents = new("/api/agents", UriKind.Relative);
    private static readonly Uri Endpoints = new("/api/model-endpoints", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Uri One(Guid id) => new($"/api/skills/{id}", UriKind.Relative);

    private static Uri AgentSkills(Guid agentId) => new($"/api/agents/{agentId}/skills", UriKind.Relative);

    private static Uri Agent(Guid id) => new($"/api/agents/{id}", UriKind.Relative);

    private static object SkillBody(
        string slug,
        string scope = "Tenant",
        Guid[]? tenants = null,
        string? description = null,
        string? body = null) => new
        {
            slug,
            displayName = slug.ToUpperInvariant(),
            description = description ?? $"Use {slug} when the user asks for it.",
            body = body ?? $"# {slug}\n\n1. Do the thing.",
            scope,
            scopeTenantIds = tenants ?? [],
        };

    private static object AgentBody(string slug, Guid endpointId, string scope = "Tenant", Guid[]? tenants = null) => new
    {
        slug,
        displayName = slug.ToUpperInvariant(),
        description = $"Call {slug} for its speciality.",
        systemPrompt = $"You are {slug}.",
        modelEndpointId = endpointId,
        scope,
        scopeTenantIds = tenants ?? [],
        subAgents = Array.Empty<object>(),
    };

    private static async Task<Guid> EndpointIdAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<IdBody>>(Endpoints, Ct))!.First().Id;

    private static async Task<Guid> CreateSkillAsync(HttpClient client, object body)
    {
        using var response = await client.PostAsJsonAsync(Skills, body, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<IdBody>(Ct))!.Id;
    }

    private static async Task<Guid> CreateAgentAsync(HttpClient client, object body)
    {
        using var response = await client.PostAsJsonAsync(Agents, body, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<IdBody>(Ct))!.Id;
    }

    private static async Task<List<string>> AssignedSlugsAsync(HttpClient client, Guid agentId) =>
        (await client.GetFromJsonAsync<List<SkillSummary>>(AgentSkills(agentId), Ct))!.Select(s => s.Slug).Order().ToList();

    [Fact] // AC-1
    public async Task CreatesAValidSkillAndRefusesMalformedOnes()
    {
        var client = host.Admin;

        using var created = await client.PostAsJsonAsync(Skills, SkillBody("contract-review"), Ct);
        var list = await client.GetFromJsonAsync<List<SkillSummary>>(Skills, Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Contains(list!, s => s.Slug == "contract-review");

        using var duplicate = await client.PostAsJsonAsync(Skills, SkillBody("contract-review"), Ct);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        using var malformed = await client.PostAsJsonAsync(Skills, SkillBody("Contract Review"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

        using var emptyBody = await client.PostAsJsonAsync(Skills, SkillBody("empty-body", body: "   "), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, emptyBody.StatusCode);

        using var longDescription = await client.PostAsJsonAsync(Skills, SkillBody("long-one", description: new string('d', 1025)), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, longDescription.StatusCode);
    }

    [Fact] // AC-2
    public async Task EverySkillEndpointNeedsTheSkillsPermission()
    {
        var admin = host.Admin;
        var id = await CreateSkillAsync(admin, SkillBody("permission-check"));
        using var member = await host.App.CreateMemberClientAsync();
        using var anonymous = host.App.CreateUiClient();

        var requests = new List<Func<HttpClient, Task<HttpResponseMessage>>>
        {
            client => client.GetAsync(Skills, Ct),
            client => client.GetAsync(One(id), Ct),
            client => client.PostAsJsonAsync(Skills, SkillBody("forbidden-create"), Ct),
            client => client.PutAsJsonAsync(One(id), SkillBody("permission-check", description: "changed"), Ct),
            client => client.GetAsync(Settings, Ct),
            client => client.PutAsJsonAsync(Settings, new { displayNameStyle = "Humanized" }, Ct),
            client => client.DeleteAsync(One(id), Ct),
        };

        foreach (var send in requests)
        {
            using var asMember = await send(member);
            using var asAnonymous = await send(anonymous);

            Assert.Equal(HttpStatusCode.Forbidden, asMember.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, asAnonymous.StatusCode);
        }
    }

    [Fact] // AC-4
    public async Task ReplacingTheAssignmentsIsAtomic()
    {
        var client = host.Admin;
        var endpoint = await EndpointIdAsync(client);
        var agent = await CreateAgentAsync(client, AgentBody("reviewer", endpoint));
        var first = await CreateSkillAsync(client, SkillBody("first-skill"));
        var second = await CreateSkillAsync(client, SkillBody("second-skill"));

        using var assigned = await client.PutAsJsonAsync(AgentSkills(agent), new { skillIds = new[] { first, second } }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, assigned.StatusCode);
        Assert.Equal(["first-skill", "second-skill"], await AssignedSlugsAsync(client, agent));

        using var replaced = await client.PutAsJsonAsync(AgentSkills(agent), new { skillIds = new[] { second } }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, replaced.StatusCode);
        Assert.Equal(["second-skill"], await AssignedSlugsAsync(client, agent));
    }

    [Fact] // AC-5
    public async Task AGlobalAgentRefusesATenantSkill()
    {
        var client = host.Admin;
        var endpoint = await EndpointIdAsync(client);
        var agent = await CreateAgentAsync(client, AgentBody("global-agent", endpoint, scope: "Global"));
        var tenantSkill = await CreateSkillAsync(client, SkillBody("tenant-only"));

        using var response = await client.PutAsJsonAsync(AgentSkills(agent), new { skillIds = new[] { tenantSkill } }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await AssignedSlugsAsync(client, agent));
    }

    [Fact] // AC-6
    public async Task NarrowingASkillOrWideningAnAgentThatBreaksAnAssignmentIsRefused()
    {
        var client = host.Admin;
        var endpoint = await EndpointIdAsync(client);
        var globalSkill = await CreateSkillAsync(client, SkillBody("shared-skill", scope: "Global"));
        var agent = await CreateAgentAsync(client, AgentBody("uses-shared", endpoint, scope: "Global"));
        await client.PutAsJsonAsync(AgentSkills(agent), new { skillIds = new[] { globalSkill } }, Ct);

        using var narrowed = await client.PutAsJsonAsync(One(globalSkill), SkillBody("shared-skill", scope: "Tenant"), Ct);
        Assert.Equal(HttpStatusCode.Conflict, narrowed.StatusCode);
        var references = (await narrowed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("references");
        Assert.Contains(references.EnumerateArray(), r => r.GetProperty("name").GetString() == "USES-SHARED");

        var tenantSkill = await CreateSkillAsync(client, SkillBody("tenant-skill"));
        var tenantAgent = await CreateAgentAsync(client, AgentBody("tenant-agent", endpoint));
        await client.PutAsJsonAsync(AgentSkills(tenantAgent), new { skillIds = new[] { tenantSkill } }, Ct);

        using var widened = await client.PutAsJsonAsync(Agent(tenantAgent), AgentBody("tenant-agent", endpoint, scope: "Global"), Ct);
        Assert.Equal(HttpStatusCode.Conflict, widened.StatusCode);
    }

    [Fact] // AC-7
    public async Task DeletingAnAssignedSkillIsRefusedAndAnUnassignedOneIsDeleted()
    {
        var client = host.Admin;
        var endpoint = await EndpointIdAsync(client);
        var assigned = await CreateSkillAsync(client, SkillBody("assigned-skill"));
        var free = await CreateSkillAsync(client, SkillBody("free-skill"));
        var agent = await CreateAgentAsync(client, AgentBody("holder", endpoint));
        await client.PutAsJsonAsync(AgentSkills(agent), new { skillIds = new[] { assigned } }, Ct);

        using var refused = await client.DeleteAsync(One(assigned), Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var references = (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("references");
        Assert.Contains(references.EnumerateArray(), r => r.GetProperty("name").GetString() == "HOLDER");

        using var deleted = await client.DeleteAsync(One(free), Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Fact] // AC-30
    public async Task ASkillSlugMayEqualAToolOrAgentName()
    {
        var client = host.Admin;
        var endpoint = await EndpointIdAsync(client);
        await CreateAgentAsync(client, AgentBody("contracts-expert", endpoint));

        using var sameAsAgent = await client.PostAsJsonAsync(Skills, SkillBody("contracts-expert"), Ct);
        using var sameAsTool = await client.PostAsJsonAsync(Skills, SkillBody("calculator"), Ct);

        Assert.Equal(HttpStatusCode.Created, sameAsAgent.StatusCode);
        Assert.Equal(HttpStatusCode.Created, sameAsTool.StatusCode);
    }

    [Fact] // AC-32 (the administrator's half; the refusal is in AC-2)
    public async Task TheDisplayNameStyleDefaultsToSlugAndCanBeChanged()
    {
        var client = host.Admin;
        var initial = await client.GetFromJsonAsync<SettingsBody>(Settings, Ct);
        Assert.Equal("Slug", initial!.DisplayNameStyle);

        using var saved = await client.PutAsJsonAsync(Settings, new { displayNameStyle = "Humanized" }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        Assert.Equal("Humanized", (await client.GetFromJsonAsync<SettingsBody>(Settings, Ct))!.DisplayNameStyle);

        using var invalid = await client.PutAsJsonAsync(Settings, new { displayNameStyle = "Shouting" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    private sealed record IdBody(Guid Id);

    private sealed record SkillSummary(Guid Id, string Slug, string DisplayName, string Description, string Scope, int AgentCount);

    private sealed record SettingsBody(string DisplayNameStyle);
}
