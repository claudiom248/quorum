using Microsoft.Extensions.DependencyInjection;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;

namespace Quorum.Integration.Tests.Fixtures;

internal static class AssistantFixtures
{
    /// <summary>Adds another assistant to the tenant of the first one, on the same model endpoint, and returns its id.</summary>
    public static async Task<Guid> AddAsync(QuorumApp app, string name = "Second")
    {
        var tenantId = await Tools.ToolFixtures.ScalarAsync<Guid>(app, "SELECT \"TenantId\" FROM agents.assistants LIMIT 1");
        var endpointId = await Tools.ToolFixtures.ScalarAsync<Guid>(app, "SELECT \"Id\" FROM agents.model_endpoints LIMIT 1");

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
        var assistant = Assistant.Create(tenantId, name, "You are another assistant.", endpointId, DateTimeOffset.UtcNow);
        db.Assistants.Add(assistant);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return assistant.Id;
    }
}
