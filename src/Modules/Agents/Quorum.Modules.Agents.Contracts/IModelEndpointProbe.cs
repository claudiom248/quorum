using Quorum.Shared.Results;

namespace Quorum.Modules.Agents.Contracts;

/// <summary>Checks that an OpenAI-compatible endpoint answers and lists its models.</summary>
public interface IModelEndpointProbe
{
    /// <param name="baseUrl">The OpenAI base URL, including the version segment (e.g. <c>http://ollama:11434/v1</c>).</param>
    /// <param name="modelName">
    /// The model the caller is about to use, or <see langword="null"/>. When given, the probe also asks the server what the
    /// model can do (see <see cref="ModelProbeResult.SupportsTools"/>).
    /// </param>
    Task<Result<ModelProbeResult>> ProbeAsync(Uri baseUrl, string? apiKey, string? modelName, CancellationToken cancellationToken);
}

/// <param name="SupportsTools">
/// <see langword="true"/> or <see langword="false"/> when the server reports whether the model supports tool calling
/// (Ollama does, through its model details); <see langword="null"/> when it does not say.
/// </param>
public sealed record ModelProbeResult(IReadOnlyList<string> Models, bool? SupportsTools = null);
