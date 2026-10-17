namespace Quorum.Modules.Agents.Contracts;

/// <summary>
/// Creates a tenant's default assistant, its model endpoint and its built-in tools (active on the assistant),
/// inside the caller's platform transaction.
/// </summary>
public interface IAssistantProvisioning
{
    Task CreateDefaultAsync(Guid tenantId, DefaultAssistantRequest request, CancellationToken cancellationToken);
}

/// <param name="SupportsTools">Whether the model accepts the <c>tools</c> parameter, as detected by the connection test.</param>
public sealed record DefaultAssistantRequest(
    Uri BaseUrl,
    string ModelName,
    string? ApiKey,
    int ContextTokenBudget,
    string SystemPrompt,
    bool SupportsTools = true);
