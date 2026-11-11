using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Quorum.Modules.Models.Contracts;
using Quorum.Shared.Results;

namespace Quorum.Modules.Models.Probing;

/// <summary>The connection test of a model server: reachability, the model list, tool support, and the vector size of an embedding model.</summary>
internal sealed partial class ModelProbe(IHttpClientFactory httpClientFactory, ILogger<ModelProbe> logger) : IModelProbe
{
    public const string HttpClientName = "model-probe";

    /// <summary>A short fixed text: its vector gives the dimensions and proves the model embeds (spec 019 D-5).</summary>
    private const string ProbeText = "Quorum";

    public async Task<Result<ModelProbeResult>> ProbeAsync(Uri baseUrl, string? apiKey, string? modelName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{baseUrl.ToString().TrimEnd('/')}/models"));
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Unreachable(baseUrl, $"it answered HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            var models = await response.Content.ReadFromJsonAsync<ModelList>(cancellationToken);
            var supportsTools = string.IsNullOrWhiteSpace(modelName) ? null : await ReadToolSupportAsync(client, baseUrl, apiKey, modelName.Trim(), cancellationToken);
            return new ModelProbeResult(models?.Data?.Select(model => model.Id).ToList() ?? [], supportsTools);
        }
        catch (HttpRequestException exception)
        {
            LogProbeFailed(logger, baseUrl, exception.Message);
            return Unreachable(baseUrl, exception.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unreachable(baseUrl, "the request timed out");
        }
        catch (JsonException)
        {
            return Unreachable(baseUrl, "the response is not an OpenAI-compatible model list");
        }
    }

    public async Task<Result<int>> ProbeEmbeddingsAsync(Uri baseUrl, string? apiKey, string modelName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{baseUrl.ToString().TrimEnd('/')}/embeddings"))
        {
            Content = JsonContent.Create(new { model = modelName.Trim(), input = ProbeText }),
        };
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return Failure("embeddings.not_supported", "The server has no embeddings API at its base URL, or the model does not exist on it.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return Failure("embeddings.failed", $"The embeddings request failed with HTTP {(int)response.StatusCode}.");
            }

            var answer = await response.Content.ReadFromJsonAsync<EmbeddingList>(cancellationToken);
            var dimensions = answer?.Data is { Count: > 0 } items ? items[0].Embedding?.Count ?? 0 : 0;
            return dimensions > 0
                ? dimensions
                : Failure("embeddings.invalid_response", "The embeddings answer has no vector.");
        }
        catch (HttpRequestException exception)
        {
            LogProbeFailed(logger, baseUrl, exception.Message);
            return Failure("embeddings.unreachable", $"Cannot reach the model server at {baseUrl}.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure("embeddings.unreachable", "The embeddings request timed out.");
        }
        catch (JsonException)
        {
            return Failure("embeddings.invalid_response", "The embeddings answer is not an OpenAI-compatible list.");
        }
    }

    /// <summary>
    /// Asks the server what the model can do through Ollama's <c>POST /api/show</c>, which lives at the server's root rather than under
    /// the OpenAI base URL. Other servers have no such API: any failure just means the support is unknown.
    /// </summary>
    private static async Task<bool?> ReadToolSupportAsync(HttpClient client, Uri baseUrl, string? apiKey, string modelName, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, "/api/show"))
        {
            Content = JsonContent.Create(new { model = modelName }),
        };
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var details = await response.Content.ReadFromJsonAsync<ModelDetails>(cancellationToken);
            return details?.Capabilities is null ? null : HasTools(details.Capabilities);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool HasTools(IReadOnlyList<string> capabilities)
    {
        foreach (var capability in capabilities)
        {
            if (string.Equals(capability, "tools", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static Error Unreachable(Uri baseUrl, string reason) =>
        Error.Unprocessable("model.unreachable", $"Cannot reach the model server at {baseUrl}: {reason}.");

    private static Error Failure(string code, string message) => Error.Unprocessable(code, message);

    [LoggerMessage(Level = LogLevel.Information, Message = "Model probe of {BaseUrl} failed: {Reason}")]
    private static partial void LogProbeFailed(ILogger logger, Uri baseUrl, string reason);

    private sealed record ModelList(IReadOnlyList<ModelEntry>? Data);

    private sealed record ModelEntry(string Id);

    private sealed record ModelDetails(IReadOnlyList<string>? Capabilities);

    private sealed record EmbeddingList(IReadOnlyList<EmbeddingEntry>? Data);

    private sealed record EmbeddingEntry(IReadOnlyList<float>? Embedding);
}
