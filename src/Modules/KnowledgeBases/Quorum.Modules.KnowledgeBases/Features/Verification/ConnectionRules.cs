using System.Text.Json;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Storage;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBases.Features.Verification;

/// <summary>What the administrator typed to reach a store, read by the adapter of the kind they chose.</summary>
internal sealed record ValidatedConnection(IStoreAdapter Adapter, IStoreConnection Connection, string EmbeddingModel);

/// <summary>What the administrator typed to reach a store, checked once for inspection and registration alike.</summary>
internal static class ConnectionRules
{
    private const int MaxNameLength = 255;

    public static Result<ValidatedConnection> Validate(StoreAdapters adapters, string? kind, JsonElement? connection, string? embeddingModel)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        if (!DictionaryFieldRules.TryParseMember<KnowledgeBaseKind>(kind, out var parsed) || !adapters.Kinds.Contains(parsed))
        {
            return Error.Validation("knowledge_base.unsupported_kind", adapters.UnsupportedKindMessage());
        }

        var adapter = adapters.For(parsed);
        var read = adapter.ReadConnection(connection);
        if (!read.IsSuccess)
        {
            return read.Error;
        }

        var model = embeddingModel?.Trim();
        if (string.IsNullOrEmpty(model) || model.Length > MaxNameLength)
        {
            return Error.Validation("knowledge_base.invalid_embedding_model", "The name of the embedding model is required.");
        }

        return new ValidatedConnection(adapter, read.Value, model);
    }
}
