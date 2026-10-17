using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.Agents.Secrets;
using Quorum.Shared.Results;

namespace Quorum.Modules.Agents.Embeddings;

/// <summary>
/// <see cref="IEmbeddings"/> over the tenant's OpenAI-compatible endpoint, with the endpoint's key and the model name the caller
/// gives. The call itself is Microsoft.Extensions.AI's <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> over the OpenAI client;
/// what is ours is the edge: which endpoint, mapping failures to stable codes, and validating the vector, because one of the wrong
/// shape would silently ruin a search.
/// </summary>
internal sealed partial class OpenAiEmbeddings(AgentsDbContext db, ApiKeyProtector apiKeyProtector, IHttpClientFactory httpClientFactory, ILogger<OpenAiEmbeddings> logger)
    : IEmbeddings
{
    public const string HttpClientName = "model-embeddings";

    public const string NoEndpoint = "embeddings.no_endpoint";
    public const string Unreachable = "embeddings.unreachable";
    public const string NotSupported = "embeddings.not_supported";
    public const string ModelNotFound = "embeddings.model_not_found";
    public const string Failed = "embeddings.failed";
    public const string InvalidResponse = "embeddings.invalid_response";

    /// <summary>Largest answer read: a vector of the largest size accepted is about 2 MB of text; this only stops an endpoint that sends without end.</summary>
    public const long MaxResponseBytes = 8 * 1024 * 1024;

    // Far above any real model (the largest common ones have a few thousand dimensions): a bound against a hostile or broken server.
    private const int MaxDimensions = 65_536;

    private const int MaxErrorLength = 200;

    // Servers that need no key (Ollama) still need the client to have one to build.
    private const string NoKey = "none";

    public async Task<Result<IReadOnlyList<float>>> EmbedAsync(string text, string modelName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var endpoint = await db.ModelEndpoints.AsNoTracking().OrderBy(e => e.Id).FirstOrDefaultAsync(cancellationToken);
        if (endpoint is null)
        {
            return Error.Unprocessable(NoEndpoint, "No model endpoint is configured, so the text cannot be embedded.");
        }

        using var generator = CreateGenerator(endpoint.BaseUrl, apiKeyProtector.Unprotect(endpoint.ProtectedApiKey), modelName);
        try
        {
            var embeddings = await generator.GenerateAsync([text], options: null, cancellationToken);
            return Validate(embeddings.Count == 0 ? default : embeddings[0].Vector);
        }
        catch (ClientResultException exception) when (exception.Status != 0)
        {
            return ToError(exception);
        }
        catch (ClientResultException exception) when (exception.InnerException is JsonException or FormatException or InvalidOperationException)
        {
            return InvalidVector();
        }
        catch (ClientResultException exception)
        {
            LogUnreachable(logger, exception.InnerException?.GetType().Name ?? exception.GetType().Name);
            return Error.Unprocessable(Unreachable, "The model endpoint cannot be reached to embed the text.");
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            return InvalidVector();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error.Unprocessable(Unreachable, "The model endpoint did not answer the embedding request in time.");
        }
    }

    private IEmbeddingGenerator<string, Embedding<float>> CreateGenerator(string baseUrl, string? apiKey, string modelName)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
        http.MaxResponseContentBufferSize = MaxResponseBytes;

        // The base URL ends with the version segment; the client appends "embeddings" to a path that ends with a slash.
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(baseUrl.TrimEnd('/') + "/"),
            Transport = new HttpClientPipelineTransport(http),
            // A failure is shown to the admin or the model at once: retrying with back-off would only make the answer slower.
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
            NetworkTimeout = http.Timeout,
        };

        options.AddPolicy(new WireFormatPolicy(stripAuthorization: string.IsNullOrEmpty(apiKey)), PipelinePosition.BeforeTransport);

        return new OpenAIClient(new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? NoKey : apiKey), options)
            .GetEmbeddingClient(modelName)
            .AsIEmbeddingGenerator();
    }

    private static Result<IReadOnlyList<float>> Validate(ReadOnlyMemory<float> vector)
    {
        if (vector.Length is 0 or > MaxDimensions)
        {
            return InvalidVector();
        }

        var values = vector.ToArray();
        return values.All(float.IsFinite) ? values : InvalidVector();
    }

    private static Error InvalidVector() =>
        Error.Unprocessable(InvalidResponse, "The model endpoint answered the embedding request with something that is not a vector.");

    private Error ToError(ClientResultException exception)
    {
        var detail = ReadDetail(exception);
        LogFailed(logger, exception.Status);

        if (exception.Status == 404)
        {
            // Ollama answers 404 for a model it does not have; a server without the API answers 404 for the route.
            return detail.Contains("model", StringComparison.OrdinalIgnoreCase) && detail.Contains("not found", StringComparison.OrdinalIgnoreCase)
                ? Error.Unprocessable(ModelNotFound, $"The model endpoint does not serve the embedding model ({detail}).")
                : Error.Unprocessable(NotSupported, "The model endpoint has no /embeddings API.");
        }

        return Error.Unprocessable(Failed, $"The model endpoint refused the embedding request (HTTP {exception.Status}){(detail.Length == 0 ? string.Empty : $": {detail}")}.");
    }

    /// <summary>The server's own error message, from the usual <c>{"error": ...}</c> body, or the text as it is; bounded.</summary>
    private static string ReadDetail(ClientResultException exception)
    {
        var body = exception.GetRawResponse()?.Content.ToString() ?? string.Empty;
        try
        {
            using var document = JsonDocument.Parse(body);
            var error = document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out var property) ? property : default;
            body = error.ValueKind switch
            {
                JsonValueKind.String => error.GetString() ?? string.Empty,
                JsonValueKind.Object when error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String => message.GetString() ?? string.Empty,
                _ => body,
            };
        }
        catch (JsonException)
        {
            // Not JSON: use the text as it is.
        }

        body = body.ReplaceLineEndings(" ").Trim();
        return body.Length > MaxErrorLength ? body[..MaxErrorLength] : body;
    }

    /// <summary>
    /// Keeps the request to what every OpenAI-compatible server understands. The client asks for base64 vectors, which not every server
    /// implements, and needs a key to be built, so it sends a bearer token even to endpoints that have none.
    /// </summary>
    private sealed class WireFormatPolicy(bool stripAuthorization) : PipelinePolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            Adjust(message);
            ProcessNext(message, pipeline, currentIndex);
        }

        public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            Adjust(message);
            await ProcessNextAsync(message, pipeline, currentIndex);
        }

        private void Adjust(PipelineMessage message)
        {
            if (stripAuthorization)
            {
                message.Request.Headers.Remove("Authorization");
            }

            if (message.Request.Content is { } content)
            {
                using var buffer = new MemoryStream();
                content.WriteTo(buffer);
                if (JsonNode.Parse(buffer.ToArray()) is JsonObject body)
                {
                    body["encoding_format"] = "float";
                    message.Request.Content = BinaryContent.Create(BinaryData.FromString(body.ToJsonString()));
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The embedding request failed with HTTP {StatusCode}")]
    private static partial void LogFailed(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "The embedding endpoint could not be reached ({Reason})")]
    private static partial void LogUnreachable(ILogger logger, string reason);
}
