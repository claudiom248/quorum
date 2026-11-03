using System.Net;
using System.Net.Http.Json;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.Tools;

namespace Quorum.Integration.Tests.Agents;

/// <summary>What an installation shows of its agents while they are read only (spec 013, PR 2).</summary>
public sealed class AgentListTests(PostgresFixture postgres)
{
    private static readonly Uri Agents = new("/api/agents", UriKind.Relative);
    private static readonly Uri Assistants = new("/api/assistants", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheWizardLeavesOneRootAgentWithTheBuiltInToolsAndItsEndpoint()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();

        var agents = await client.GetFromJsonAsync<List<AgentBody>>(Agents, Ct);
        var assistants = await client.GetFromJsonAsync<List<AssistantBody>>(Assistants, Ct);

        var agent = Assert.Single(agents!);
        Assert.Equal("assistant", agent.Slug);
        Assert.Equal("Tenant", agent.Scope);
        Assert.NotNull(agent.ModelEndpointId);
        Assert.Equal(0, agent.SubAgentCount);
        Assert.True(agent.ToolCount > 0);
        var assistant = Assert.Single(assistants!);
        Assert.Equal("assistant", assistant.RootAgentSlug);
        Assert.Equal(1, assistant.MaxParallelSubAgents);
    }

    [Fact]
    public async Task OnlyAnAdministratorReadsTheAgents()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        using var member = await app.CreateMemberClientAsync();

        using var forbidden = await member.GetAsync(Agents, Ct);
        using var allowed = await admin.GetAsync(Agents, Ct);
        // The list of assistants stays open to any signed-in user.
        using var assistants = await member.GetAsync(Assistants, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, assistants.StatusCode);
    }

    [Fact]
    public async Task AnAssistantAddedLaterGetsItsOwnRootAgentAndToolSet()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var defaultAssistant = await ToolsApi.DefaultAssistantIdAsync(client);
        var (second, secondAgent) = await AssistantFixtures.AddWithAgentAsync(app, "Second");

        var pool = (await ToolsApi.GetAssistantToolsAsync(client, second)).Pool;
        var calculator = (await ToolsApi.GetAssistantToolsAsync(client, defaultAssistant)).Pool.Single(t => t.Name == "builtin__calculator").ToolId;
        using var activate = await client.PutAsJsonAsync(ToolsApi.AssistantTools(defaultAssistant), new { toolIds = new[] { calculator } }, Ct);
        activate.EnsureSuccessStatusCode();
        var secondActive = (await ToolsApi.GetAssistantToolsAsync(client, second)).Pool.Where(t => t.Active).ToList();
        var defaultActive = (await ToolsApi.GetAssistantToolsAsync(client, defaultAssistant)).Pool.Where(t => t.Active).ToList();

        // Tools belong to the agent: changing the default assistant's set leaves the other assistant's alone.
        Assert.NotEmpty(pool);
        Assert.Empty(secondActive);
        Assert.Equal(["builtin__calculator"], defaultActive.Select(t => t.Name));
        Assert.Equal(secondAgent, await AssistantFixtures.RootAgentIdAsync(app, second));
    }

    private sealed record AgentBody(Guid Id, string Slug, string DisplayName, string Description, string Scope, Guid? ModelEndpointId, int SubAgentCount, int ToolCount);

    private sealed record AssistantBody(Guid Id, string Name, string RootAgentSlug, int MaxParallelSubAgents);
}
