using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Quorum.SampleKnowledgeBase;

/// <summary>
/// Embeds texts with a real model on an OpenAI-compatible endpoint, the way a data owner's ingestion would. The deterministic
/// embedding cannot be queried by a real model, so a manual run of the sample collections through Quorum needs vectors made
/// with the model the knowledge base is registered with.
/// </summary>
public sealed class ModelEmbedder(HttpClient client, string model, string documentPrefix)
{
    /// <summary>Asks the model for one vector to learn its dimension.</summary>
    public async Task<int> ProbeDimensionsAsync(CancellationToken cancellationToken) =>
        (await EmbedAsync("dimension probe", cancellationToken)).Length;

    /// <summary>The vector of <paramref name="text"/>, with the document prefix of the model (for example <c>search_document: </c>) in front.</summary>
    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);

        var vectors = await EmbedBatchAsync([text], cancellationToken)
            ?? throw new InvalidOperationException("The embedding endpoint answered without a vector.");
        return vectors[0];
    }

    /// <summary>
    /// The vectors of every text. The endpoint is asked in batches, because an ingestion of three thousand chunks cannot afford three
    /// thousand requests; an endpoint that answers one vector per request whatever the input gets the texts one by one instead.
    /// </summary>
    public async Task<IReadOnlyList<float[]>> EmbedAllAsync(IReadOnlyList<string> texts, int batchSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var all = new List<float[]>(texts.Count);
        for (var from = 0; from < texts.Count; from += batchSize)
        {
            var batch = texts.Skip(from).Take(batchSize).ToArray();
            if (await EmbedBatchAsync(batch, cancellationToken) is { } vectors)
            {
                all.AddRange(vectors);
                continue;
            }

            foreach (var text in batch)
            {
                var one = await EmbedBatchAsync([text], cancellationToken);
                if (one is null or { Count: 0 })
                {
                    throw new InvalidOperationException("The embedding endpoint answered no vector, for a batch or for a single text.");
                }

                all.Add(one[0]);
            }
        }

        return all;
    }

    /// <summary>The vectors of the texts, or <see langword="null"/> when the endpoint did not answer one vector per text.</summary>
    private async Task<IReadOnlyList<float[]>?> EmbedBatchAsync(string[] texts, CancellationToken cancellationToken)
    {
        var input = new JsonArray([.. texts.Select(text => (JsonNode?)JsonValue.Create(documentPrefix + text))]);
        using var response = await client.PostAsJsonAsync(
            new Uri("embeddings", UriKind.Relative),
            new JsonObject { ["model"] = model, ["input"] = input },
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"The embedding endpoint answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}");
        }

        var body = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken);
        var data = body?["data"]?.AsArray();
        if (data is null or { Count: 0 })
        {
            return null;
        }

        var vectors = data
            .OrderBy(item => item?["index"]?.GetValue<int>() ?? 0)
            .Select(item => item?["embedding"]?.AsArray())
            .Select(values => values is null or { Count: 0 }
                ? throw new InvalidOperationException("The embedding endpoint answered without a vector.")
                : values.Select(value => value!.GetValue<float>()).ToArray())
            .ToList();
        return vectors.Count == texts.Length ? vectors : null;
    }

    public static ModelEmbedder Create(HttpClient client, Uri baseAddress, string model, string documentPrefix, string? apiKey)
    {
        ArgumentNullException.ThrowIfNull(client);

        client.BaseAddress = new Uri(baseAddress.AbsoluteUri.TrimEnd('/') + "/");
        if (!string.IsNullOrEmpty(apiKey))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return new ModelEmbedder(client, model, documentPrefix);
    }
}
