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

        using var response = await client.PostAsJsonAsync(
            new Uri("embeddings", UriKind.Relative),
            new JsonObject { ["model"] = model, ["input"] = documentPrefix + text },
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"The embedding endpoint answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}");
        }

        var body = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken);
        var values = body?["data"]?[0]?["embedding"]?.AsArray();
        if (values is null or { Count: 0 })
        {
            throw new InvalidOperationException("The embedding endpoint answered without a vector.");
        }

        return values.Select(value => value!.GetValue<float>()).ToArray();
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
