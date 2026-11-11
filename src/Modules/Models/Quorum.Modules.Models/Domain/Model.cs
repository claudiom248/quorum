using Quorum.Modules.Models.Contracts;

namespace Quorum.Modules.Models.Domain;

/// <summary>Who may select a model (spec 019 D-7): every tenant, or the owner and the listed tenants.</summary>
public enum ModelScope
{
    Global,
    Tenant,
}

/// <summary>
/// One registered model of an OpenAI-compatible server (spec 019 D-1). The connection stays inside the model: URL and key are repeated
/// per model on the same server. Chat fields and embedding fields are stored only for the capabilities that use them (D-3).
/// </summary>
internal sealed class Model
{
    private Model()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>What the model is called in the lists and in the pickers; unique per owner tenant (D-6).</summary>
    public string Name { get; private set; } = null!;

    public string BaseUrl { get; private set; } = null!;

    /// <summary>The name the server knows the model by.</summary>
    public string ModelName { get; private set; } = null!;

    /// <summary>Encrypted with Data Protection, with the purpose the endpoints used before (D-17).</summary>
    public string? ProtectedApiKey { get; private set; }

    public ModelCapabilities Capabilities { get; private set; }

    /// <summary>With <see cref="ModelCapabilities.Chat"/>: how many tokens of context the model has.</summary>
    public int? ContextTokenBudget { get; private set; }

    /// <summary>With <see cref="ModelCapabilities.Embeddings"/>: the text put before a query before it is embedded (D-4).</summary>
    public string? QueryPrefix { get; private set; }

    /// <summary>With <see cref="ModelCapabilities.Embeddings"/>: informational, from the connection test (D-3, D-5).</summary>
    public int? EmbeddingDimensions { get; private set; }

    public ModelScope Scope { get; private set; } = ModelScope.Tenant;

    public List<Guid> ScopeTenantIds { get; private set; } = [];

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Model Create(
        Guid tenantId,
        string name,
        Uri baseUrl,
        string modelName,
        string? protectedApiKey,
        ModelCapabilities capabilities,
        int? contextTokenBudget,
        string? queryPrefix,
        int? embeddingDimensions,
        ModelScope scope,
        IEnumerable<Guid> scopeTenantIds,
        DateTimeOffset now)
    {
        var model = new Model
        {
            Id = Guid.CreateVersion7(now),
            TenantId = tenantId,
            UpdatedAt = now,
        };
        model.Name = name;
        model.Configure(baseUrl, modelName, capabilities, contextTokenBudget, queryPrefix, embeddingDimensions, scope, scopeTenantIds, now);
        model.ProtectedApiKey = protectedApiKey;
        return model;
    }

    /// <summary>Changes what the model is and what it does; the fields of capabilities it does not have are cleared (D-3).</summary>
    public void Configure(
        Uri baseUrl,
        string modelName,
        ModelCapabilities capabilities,
        int? contextTokenBudget,
        string? queryPrefix,
        int? embeddingDimensions,
        ModelScope scope,
        IEnumerable<Guid> scopeTenantIds,
        DateTimeOffset now)
    {
        BaseUrl = baseUrl.ToString();
        ModelName = modelName;
        Capabilities = capabilities;
        ContextTokenBudget = capabilities.HasFlag(ModelCapabilities.Chat) ? contextTokenBudget : null;
        var embeds = capabilities.HasFlag(ModelCapabilities.Embeddings);
        QueryPrefix = embeds ? queryPrefix : null;
        EmbeddingDimensions = embeds ? embeddingDimensions : null;
        Scope = scope;
        ScopeTenantIds = scope == ModelScope.Global ? [] : [.. scopeTenantIds.Distinct().Where(id => id != TenantId)];
        UpdatedAt = now;
    }

    public void Rename(string name, DateTimeOffset now)
    {
        Name = name;
        UpdatedAt = now;
    }

    public void ReplaceApiKey(string? protectedApiKey, DateTimeOffset now)
    {
        ProtectedApiKey = protectedApiKey;
        UpdatedAt = now;
    }
}
