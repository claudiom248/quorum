namespace Quorum.Web.Client.Api;

/// <summary>A model as the tenant sees it. <see cref="BaseUrl"/> and <see cref="HasApiKey"/> are present only to the owner (spec 019 D-9).</summary>
public sealed record ModelInfo(
    Guid Id,
    string Name,
    string ModelName,
    IReadOnlyList<string> Capabilities,
    int? ContextTokenBudget,
    int? EmbeddingDimensions,
    string? QueryPrefix,
    string Scope,
    IReadOnlyList<Guid> ScopeTenantIds,
    bool Owned,
    string? BaseUrl,
    bool? HasApiKey);

/// <summary>What the admin sends for a model (spec 019 D-1 to D-3). An omitted <see cref="ApiKey"/> keeps the stored key on update.</summary>
public sealed record ModelRequest(
    string Name,
    string BaseUrl,
    string ModelName,
    string? ApiKey,
    IReadOnlyList<string> Capabilities,
    int? ContextTokenBudget,
    string? QueryPrefix,
    int? EmbeddingDimensions,
    string Scope,
    IReadOnlyList<Guid> ScopeTenantIds);

/// <summary>The result of the connection test (spec 019 D-5).</summary>
public sealed record ModelTestInfo(bool Reachable, IReadOnlyList<string> Models, IReadOnlyList<string> SuggestedCapabilities, int? EmbeddingDimensions, string? EmbeddingsProblem);

/// <summary>The capability names the API speaks (spec 019 D-2). The server's names are checked against these by a test.</summary>
public static class ModelCapabilityNames
{
    public const string Chat = "chat";

    public const string Tools = "tools";

    public const string Embeddings = "embeddings";
}
