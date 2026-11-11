namespace Quorum.Modules.Models.Contracts;

/// <summary>
/// The models a consumer of the platform may use, and how to reach one (spec 019 D-9, D-18). Another module asks by id: the connection
/// is resolved without the tenant filter, so a shared agent runs on the model of its owner, with the owner's key.
/// </summary>
public interface IModelCatalog
{
    /// <summary>Whether the model exists, has the required capabilities and a scope no wider than the consumer's (D-8).</summary>
    Task<ModelUsability> CheckUsableAsync(Guid modelId, ModelCapabilities required, ModelUseScope consumer, CancellationToken cancellationToken);

    /// <summary>The connection of a model, or <see langword="null"/> when it does not exist.</summary>
    Task<ModelConnection?> ResolveAsync(Guid modelId, CancellationToken cancellationToken);
}

/// <summary>Why a model may or may not be used by a consumer. <see cref="Problem"/> is <see langword="null"/> when it may.</summary>
public sealed record ModelUsability(string? Problem);

/// <summary>What a chat or embedding client needs to reach a model. The key is in clear: it only goes to the client that calls the model.</summary>
public sealed record ModelConnection(
    Guid Id,
    string BaseUrl,
    string ModelName,
    string? ApiKey,
    ModelCapabilities Capabilities,
    int? ContextTokenBudget,
    string? QueryPrefix,
    int? EmbeddingDimensions);

/// <summary>The scope of a consumer (an agent or a knowledge base): the tenant that owns it, and whether and to whom it is shared.</summary>
public sealed record ModelUseScope(Guid OwnerTenantId, bool Global, IReadOnlyList<Guid> ScopeTenantIds);
