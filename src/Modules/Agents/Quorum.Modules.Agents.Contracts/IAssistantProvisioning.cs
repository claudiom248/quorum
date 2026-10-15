namespace Quorum.Modules.Agents.Contracts;

/// <summary>Creates a tenant's default assistant and its model endpoint, inside the caller's platform transaction.</summary>
public interface IAssistantProvisioning
{
    Task CreateDefaultAsync(Guid tenantId, DefaultAssistantRequest request, CancellationToken cancellationToken);
}

public sealed record DefaultAssistantRequest(Uri BaseUrl, string ModelName, string? ApiKey, int ContextTokenBudget, string SystemPrompt);
