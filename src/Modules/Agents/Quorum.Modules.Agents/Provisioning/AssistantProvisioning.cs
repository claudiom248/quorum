using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.Agents.Secrets;

namespace Quorum.Modules.Agents.Provisioning;

internal sealed class AssistantProvisioning(AgentsDbContext db, ApiKeyProtector apiKeyProtector, TimeProvider clock) : IAssistantProvisioning
{
    public async Task CreateDefaultAsync(Guid tenantId, DefaultAssistantRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = clock.GetUtcNow();
        var endpoint = ModelEndpoint.Create(
            tenantId,
            request.BaseUrl,
            request.ModelName,
            apiKeyProtector.Protect(request.ApiKey),
            request.ContextTokenBudget,
            now);
        var assistant = Assistant.Create(tenantId, AssistantDefaults.AssistantName, request.SystemPrompt, endpoint.Id, now);

        db.ModelEndpoints.Add(endpoint);
        db.Assistants.Add(assistant);
        await db.SaveChangesAsync(cancellationToken);
    }
}
