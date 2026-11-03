using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.Agents.Secrets;
using Quorum.Modules.Agents.Tools;

namespace Quorum.Modules.Agents.Provisioning;

internal sealed class AssistantProvisioning(AgentsDbContext db, ApiKeyProtector apiKeyProtector, TimeProvider clock) : IAssistantProvisioning
{
    public async Task CreateDefaultAsync(Guid tenantId, DefaultAssistantRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = clock.GetUtcNow();
        var endpoint = ModelEndpoint.Create(
            tenantId,
            ModelEndpointRules.DefaultName(request.ModelName),
            request.BaseUrl,
            request.ModelName,
            apiKeyProtector.Protect(request.ApiKey),
            request.ContextTokenBudget,
            request.SupportsTools,
            now);
        var agent = Agent.Create(
            tenantId,
            AgentRules.SlugFrom(AssistantDefaults.AssistantName),
            AssistantDefaults.AssistantName,
            AssistantDefaults.AgentDescription,
            request.SystemPrompt,
            endpoint.Id,
            AgentScope.Tenant,
            [],
            now);
        var assistant = Assistant.Create(tenantId, AssistantDefaults.AssistantName, agent.Id, now);

        db.ModelEndpoints.Add(endpoint);
        db.Agents.Add(agent);
        db.Assistants.Add(assistant);
        await db.SaveChangesAsync(cancellationToken);

        // A new installation starts with the built-in tools active; existing installations keep their behaviour on upgrade.
        var builtinTools = await BuiltinToolProvisioner.EnsureAsync(db, tenantId, cancellationToken);
        db.AgentTools.AddRange(builtinTools.Select(tool => AgentTool.Create(tenantId, agent.Id, tool.Id)));
        await db.SaveChangesAsync(cancellationToken);
    }
}
