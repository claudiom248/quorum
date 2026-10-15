using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Quorum.Modules.Agents.Contracts;
using Quorum.Shared.Results;

namespace Quorum.Modules.Agents.Probing;

internal sealed partial class ModelEndpointProbe(IHttpClientFactory httpClientFactory, ILogger<ModelEndpointProbe> logger) : IModelEndpointProbe
{
    public const string HttpClientName = "model-endpoint-probe";

    public async Task<Result<ModelProbeResult>> ProbeAsync(Uri baseUrl, string? apiKey, CancellationToken cancellationToken)
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
            return new ModelProbeResult(models?.Data?.Select(model => model.Id).ToList() ?? []);
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

    private static Error Unreachable(Uri baseUrl, string reason) =>
        Error.Unprocessable("model_endpoint.unreachable", $"Cannot reach the model endpoint at {baseUrl}: {reason}.");

    [LoggerMessage(Level = LogLevel.Information, Message = "Model endpoint probe of {BaseUrl} failed: {Reason}")]
    private static partial void LogProbeFailed(ILogger logger, Uri baseUrl, string reason);

    private sealed record ModelList(IReadOnlyList<ModelEntry>? Data);

    private sealed record ModelEntry(string Id);
}
