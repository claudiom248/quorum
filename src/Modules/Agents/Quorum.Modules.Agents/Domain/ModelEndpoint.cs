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

    /// <summary>
    /// Whether the model accepts the <c>tools</c> parameter. Models that do not would fail every request that carries tools,
    /// so an assistant on such a model must be told not to send them.
    /// </summary>
    public bool SupportsTools { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static ModelEndpoint Create(
        Guid tenantId,
        Uri baseUrl,
        string modelName,
        string? protectedApiKey,
        int contextTokenBudget,
        bool supportsTools,
        DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(now),
            TenantId = tenantId,
            BaseUrl = baseUrl.ToString(),
            ModelName = modelName,
            ProtectedApiKey = protectedApiKey,
            ContextTokenBudget = contextTokenBudget,
            SupportsTools = supportsTools,
            UpdatedAt = now,
        };

    /// <param name="supportsTools"><see langword="null"/> keeps the current value.</param>
    public void Configure(Uri baseUrl, string modelName, int contextTokenBudget, bool? supportsTools, DateTimeOffset now)
    {
        BaseUrl = baseUrl.ToString();
        ModelName = modelName;
        ContextTokenBudget = contextTokenBudget;
        SupportsTools = supportsTools ?? SupportsTools;
        UpdatedAt = now;
    }

    public void ReplaceApiKey(string? protectedApiKey, DateTimeOffset now)
    {
        ProtectedApiKey = protectedApiKey;
        UpdatedAt = now;
    }
}
