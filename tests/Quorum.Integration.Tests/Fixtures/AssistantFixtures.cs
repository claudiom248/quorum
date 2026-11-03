using Microsoft.Extensions.DependencyInjection;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;

namespace Quorum.Integration.Tests.Fixtures;

internal static class AssistantFixtures
{
    /// <summary>Adds another assistant to the tenant of the first one, on the same model endpoint, and returns its id.</summary>
    public static async Task<Guid> AddAsync(QuorumApp app, string name = "Second") => (await AddWithAgentAsync(app, name)).AssistantId;

    /// <summary>Adds another assistant with a root agent of its own, on the same model endpoint of the first tenant.</summary>
    public static async Task<(Guid AssistantId, Guid AgentId)> AddWithAgentAsync(QuorumApp app, string name = "Second")
    {
        var tenantId = await Tools.ToolFixtures.ScalarAsync<Guid>(app, "SELECT \"TenantId\" FROM agents.assistants LIMIT 1");
        var endpointId = await Tools.ToolFixtures.ScalarAsync<Guid>(app, "SELECT \"Id\" FROM agents.model_endpoints LIMIT 1");

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
        var now = DateTimeOffset.UtcNow;
        var agent = Agent.Create(
            tenantId, AgentRules.SlugFrom(name) + "-" + Guid.NewGuid().ToString("N")[..6], name, "Another agent.", "You are another assistant.", endpointId, AgentScope.Tenant, [], now);
        var assistant = Assistant.Create(tenantId, name, agent.Id, now);
        db.Agents.Add(agent);
        db.Assistants.Add(assistant);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (assistant.Id, agent.Id);
    }

    /// <summary>The root agent of an assistant: what a tool scope names and what holds the assistant's tools.</summary>
    public static Task<Guid> RootAgentIdAsync(QuorumApp app, Guid assistantId) =>
        Tools.ToolFixtures.ScalarAsync<Guid>(app, "SELECT \"RootAgentId\" FROM agents.assistants WHERE \"Id\" = '" + assistantId + "'");
}
