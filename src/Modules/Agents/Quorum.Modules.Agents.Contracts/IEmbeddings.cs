using Quorum.Shared.Results;

namespace Quorum.Modules.Agents.Contracts;

/// <summary>Turns a text into a vector with a named embedding model served by the installation's model endpoint.</summary>
public interface IEmbeddings
{
    /// <summary>
    /// Embeds one text for the current tenant. A failure (endpoint unreachable, no <c>/embeddings</c> API, unknown model, unusable
    /// answer) is a result with a stable <c>embeddings.*</c> code and a message that is safe to show: it never carries the text,
    /// the key or the address of the endpoint.
    /// </summary>
    Task<Result<IReadOnlyList<float>>> EmbedAsync(string text, string modelName, CancellationToken cancellationToken);
}
