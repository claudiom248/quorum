using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Models.Contracts;
using Quorum.Modules.Models.Persistence;
using Quorum.Modules.Models.Secrets;
using Quorum.Shared.Results;

namespace Quorum.Modules.Models.Embeddings;

/// <summary>Embeds a query with a model by its id: the model's own server, key and query prefix (spec 019 D-4, D-9, D-15).</summary>
internal sealed class ModelEmbeddings(IHttpClientFactory httpClientFactory, ModelsDbContext db, ApiKeyProtector apiKeyProtector) : IModelEmbeddings
{
    public const string HttpClientName = "model-embeddings";

    public async Task<Result<IReadOnlyList<float>>> EmbedQueryAsync(string text, Guid modelId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Resolved without the tenant filter: a knowledge base shared with another tenant embeds with its owner's model (D-9).
        var model = await db.Models.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.Id == modelId)
            .Select(m => new { m.BaseUrl, m.ModelName, m.ProtectedApiKey, m.Capabilities, m.QueryPrefix })
            .SingleOrDefaultAsync(cancellationToken);
        if (model is null)
        {
            return Error.NotFound("embeddings.no_model", "The embedding model does not exist.");
        }

        if (!model.Capabilities.HasFlag(ModelCapabilities.Embeddings))
        {
            return Error.Unprocessable("embeddings.not_supported", "The model is not declared to embed text.");
        }

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{model.BaseUrl.TrimEnd('/')}/embeddings"))
        {
            Content = JsonContent.Create(new { model = model.ModelName, input = (model.QueryPrefix ?? string.Empty) + text }),
        };
        var key = apiKeyProtector.Unprotect(model.ProtectedApiKey);
        if (!string.IsNullOrEmpty(key))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return Error.Unprocessable("embeddings.model_not_found", "The embedding server does not know this model.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return Error.Unprocessable("embeddings.failed", $"The embeddings request failed with HTTP {(int)response.StatusCode}.");
            }

            var answer = await response.Content.ReadFromJsonAsync<EmbeddingList>(cancellationToken);
            var vector = answer?.Data is { Count: > 0 } items ? items[0].Embedding : null;
            if (vector is { Count: > 0 })
            {
                return Result<IReadOnlyList<float>>.Success(vector);
            }

            return Error.Unprocessable("embeddings.invalid_response", "The embeddings answer has no vector.");
        }
        catch (HttpRequestException)
        {
            return Error.Unprocessable("embeddings.unreachable", "The embedding server cannot be reached.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error.Unprocessable("embeddings.unreachable", "The embedding request timed out.");
        }
        catch (JsonException)
        {
            return Error.Unprocessable("embeddings.invalid_response", "The embeddings answer is not an OpenAI-compatible list.");
        }
    }

    private sealed record EmbeddingList(IReadOnlyList<EmbeddingEntry>? Data);

    private sealed record EmbeddingEntry(IReadOnlyList<float>? Embedding);
}
