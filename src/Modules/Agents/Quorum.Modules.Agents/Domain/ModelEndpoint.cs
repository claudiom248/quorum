namespace Quorum.Modules.Agents.Domain;

internal sealed class ModelEndpoint
{
    private ModelEndpoint()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string BaseUrl { get; private set; } = null!;

    public string ModelName { get; private set; } = null!;

    /// <summary>Encrypted with Data Protection; the key ring lives outside the database.</summary>
    public string? ProtectedApiKey { get; private set; }

    public int ContextTokenBudget { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static ModelEndpoint Create(Guid tenantId, Uri baseUrl, string modelName, string? protectedApiKey, int contextTokenBudget, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        TenantId = tenantId,
        BaseUrl = baseUrl.ToString(),
        ModelName = modelName,
        ProtectedApiKey = protectedApiKey,
        ContextTokenBudget = contextTokenBudget,
        UpdatedAt = now,
    };
}
