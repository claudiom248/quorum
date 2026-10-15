using Quorum.Shared.Results;

namespace Quorum.Modules.Agents.Contracts;

/// <summary>Checks that an OpenAI-compatible endpoint answers and lists its models.</summary>
public interface IModelEndpointProbe
{
    /// <param name="baseUrl">The OpenAI base URL, including the version segment (e.g. <c>http://ollama:11434/v1</c>).</param>
    Task<Result<ModelProbeResult>> ProbeAsync(Uri baseUrl, string? apiKey, CancellationToken cancellationToken);
}

public sealed record ModelProbeResult(IReadOnlyList<string> Models);
