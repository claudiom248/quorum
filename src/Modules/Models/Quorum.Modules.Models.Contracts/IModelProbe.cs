using Quorum.Shared.Results;

namespace Quorum.Modules.Models.Contracts;

/// <summary>Checks that a model server answers, and what its models can do (spec 019 D-5).</summary>
public interface IModelProbe
{
    /// <summary>The server lists its models; with a model name, the probe also asks whether that model supports tools.</summary>
    Task<Result<ModelProbeResult>> ProbeAsync(Uri baseUrl, string? apiKey, string? modelName, CancellationToken cancellationToken);

    /// <summary>Embeds a fixed short text with the model; the result is the vector's dimensions. A failure has a stable <c>embeddings.*</c> code.</summary>
    Task<Result<int>> ProbeEmbeddingsAsync(Uri baseUrl, string? apiKey, string modelName, CancellationToken cancellationToken);
}

/// <param name="SupportsTools">
/// <see langword="true"/> or <see langword="false"/> when the server reports it (Ollama does); <see langword="null"/> when it does not say.
/// </param>
public sealed record ModelProbeResult(IReadOnlyList<string> Models, bool? SupportsTools = null);
