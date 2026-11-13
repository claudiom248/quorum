using Quorum.Shared.Results;

namespace Quorum.Modules.Models.Contracts;

/// <summary>Turns a query into a vector with an embedding model, by its id, applying the model's query prefix (spec 019 D-4, D-15).</summary>
public interface IModelEmbeddings
{
    /// <summary>
    /// A failure carries a stable <c>embeddings.*</c> code and a message that never shows the text, the key or the address.
    /// An id that does not resolve is <c>embeddings.no_model</c>; a model the consumer may not use is <c>embeddings.not_usable</c>.
    /// </summary>
    /// <param name="consumer">The agent or knowledge base the vector is for: the model must be usable by it (spec 019 D-8, D-9).</param>
    Task<Result<IReadOnlyList<float>>> EmbedQueryAsync(string text, Guid modelId, ModelUseScope consumer, CancellationToken cancellationToken);
}
