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
            request.BaseUrl,
            request.ModelName,
            apiKeyProtector.Protect(request.ApiKey),
            request.ContextTokenBudget,
            request.SupportsTools,
            now);
        var assistant = Assistant.Create(tenantId, AssistantDefaults.AssistantName, request.SystemPrompt, endpoint.Id, now);

        db.ModelEndpoints.Add(endpoint);
        db.Assistants.Add(assistant);
        await db.SaveChangesAsync(cancellationToken);

        // A new installation starts with the native tools active; existing installations keep their behaviour on upgrade.
        var nativeTools = await NativeToolProvisioner.EnsureAsync(db, tenantId, cancellationToken);
        db.AssistantTools.AddRange(nativeTools.Select(tool => AssistantTool.Create(tenantId, assistant.Id, tool.Id)));
        await db.SaveChangesAsync(cancellationToken);
    }
}
