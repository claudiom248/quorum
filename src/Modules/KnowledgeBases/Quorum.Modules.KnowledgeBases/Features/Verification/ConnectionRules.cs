using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Features.Inspect;
using Quorum.Modules.KnowledgeBases.Storage.Qdrant;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBases.Features.Verification;

/// <summary>What the administrator typed to reach a store, checked once for inspection and registration alike.</summary>
internal static class ConnectionRules
{
    private const int MaxUrlLength = 2048;

    private const int MaxNameLength = 255;

    public static Result<(QdrantConnection Connection, string EmbeddingModel)> Validate(string? kind, ConnectionRequest? connection, string? embeddingModel)
    {
        if (!DictionaryFieldRules.TryParseMember<KnowledgeBaseKind>(kind, out _))
        {
            return Error.Validation("knowledge_base.unsupported_kind", "The kind must be Qdrant: it is the only storage kind supported.");
        }

        if (connection?.Url is null || !Uri.TryCreate(connection.Url.Trim(), UriKind.Absolute, out var url)
            || url.Scheme is not ("http" or "https") || connection.Url.Length > MaxUrlLength || !string.IsNullOrEmpty(url.UserInfo))
        {
            return Error.Validation(
                "knowledge_base.invalid_connection",
                "The URL must be an absolute http or https address of the Qdrant gRPC port (for example http://qdrant:6334), without credentials: the API key has its own field.");
        }

        var collection = connection.Collection?.Trim();
        if (string.IsNullOrEmpty(collection) || collection.Length > MaxNameLength)
        {
            return Error.Validation("knowledge_base.invalid_connection", "The collection name is required.");
        }

        var model = embeddingModel?.Trim();
        if (string.IsNullOrEmpty(model) || model.Length > MaxNameLength)
        {
            return Error.Validation("knowledge_base.invalid_embedding_model", "The name of the embedding model is required.");
        }

        var vectorName = string.IsNullOrWhiteSpace(connection.VectorName) ? null : connection.VectorName.Trim();
        var apiKey = string.IsNullOrEmpty(connection.ApiKey) ? null : connection.ApiKey;
        if (apiKey is not null && !apiKey.All(character => character is >= ' ' and <= '~'))
        {
            // The key travels in a header, which carries ASCII only: a pasted typographic character would fail far from its cause.
            return Error.Validation("knowledge_base.invalid_connection", "The API key can only contain printable ASCII characters: check that nothing but the key was pasted.");
        }

        return (new QdrantConnection(url, collection, apiKey, vectorName), model);
    }
}
