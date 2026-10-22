using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Quorum.SampleKnowledgeBase;

/// <summary>
/// Writes sample collections into a Qdrant server over REST. This is what an ingestion pipeline does and what Quorum itself never
/// does, so the product has no such code: it lives here, next to the data it creates. The client's base address is the server.
/// </summary>
public sealed class QdrantSeeder(HttpClient client, ModelEmbedder? model = null)
{
    /// <summary>Drops the collection if it exists, then creates it with its vectors, payload indexes and points.</summary>
    public async Task SeedAsync(SampleDataset collection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collection);

        await DeleteAsync(collection.Name, cancellationToken);
        await SendAsync(HttpMethod.Put, $"collections/{Uri.EscapeDataString(collection.Name)}", CreateBody(collection), cancellationToken);

        foreach (var index in collection.Indexes)
        {
            await SendAsync(
                HttpMethod.Put,
                $"collections/{Uri.EscapeDataString(collection.Name)}/index?wait=true",
                new JsonObject { ["field_name"] = index.Path, ["field_schema"] = index.Schema },
                cancellationToken);
        }

        if (collection.Points.Count > 0)
        {
            await UpsertAsync(collection, collection.Points, cancellationToken);
        }
    }

    public async Task SeedAsync(IEnumerable<SampleDataset> collections, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collections);

        foreach (var collection in collections)
        {
            await SeedAsync(collection, cancellationToken);
        }
    }

    /// <summary>Adds points to an existing collection (a test that needs a payload of its own).</summary>
    public async Task UpsertAsync(SampleDataset shape, IEnumerable<SamplePoint> points, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(points);

        var bodies = new List<JsonNode?>();
        foreach (var point in points)
        {
            bodies.Add(await PointBodyAsync(shape, point, cancellationToken));
        }

        await SendAsync(
            HttpMethod.Put,
            $"collections/{Uri.EscapeDataString(shape.Name)}/points?wait=true",
            new JsonObject { ["points"] = new JsonArray(bodies.ToArray()) },
            cancellationToken);
    }

    public async Task DeleteAsync(string collection, CancellationToken cancellationToken)
    {
        using var response = await client.DeleteAsync(new Uri($"collections/{Uri.EscapeDataString(collection)}", UriKind.Relative), cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            await EnsureSuccessAsync(response, cancellationToken);
        }
    }

    private static JsonObject CreateBody(SampleDataset collection)
    {
        // A single unnamed vector is the collection's "vectors" itself; named ones are an object of vector configurations.
        if (collection.Vectors is [{ Name: null } single])
        {
            return new JsonObject { ["vectors"] = Config(single) };
        }

        var vectors = new JsonObject();
        foreach (var vector in collection.Vectors)
        {
            vectors[vector.Name ?? throw new InvalidOperationException("A collection with several vectors names each of them.")] = Config(vector);
        }

        return new JsonObject { ["vectors"] = vectors };
    }

    private static JsonObject Config(SampleVector vector) => new() { ["size"] = vector.Size, ["distance"] = vector.Distance };

    private async Task<JsonObject> PointBodyAsync(SampleDataset collection, SamplePoint point, CancellationToken cancellationToken)
    {
        JsonNode vector;
        if (collection.Vectors is [{ Name: null } single])
        {
            vector = ToArray(await EmbedAsync(single, point, cancellationToken));
        }
        else
        {
            var named = new JsonObject();
            foreach (var item in collection.Vectors)
            {
                named[item.Name!] = ToArray(await EmbedAsync(item, point, cancellationToken));
            }

            vector = named;
        }

        return new JsonObject { ["id"] = point.Id, ["vector"] = vector, ["payload"] = point.Payload.DeepClone() };
    }

    private async Task<float[]> EmbedAsync(SampleVector vector, SamplePoint point, CancellationToken cancellationToken)
    {
        if (!vector.FromModel)
        {
            return DeterministicEmbedding.Embed(point.EmbeddingText, vector.Size);
        }

        var embedder = model ?? throw new InvalidOperationException("A vector made by a model needs an embedder.");
        var values = await embedder.EmbedAsync(point.EmbeddingText, cancellationToken);
        return values.Length == vector.Size
            ? values
            : throw new InvalidOperationException($"The model answered {values.Length} dimensions, the collection has {vector.Size}.");
    }

    private static JsonArray ToArray(float[] values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    private async Task SendAsync(HttpMethod method, string path, JsonObject body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body) };
        using var response = await client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Qdrant answered {(int)response.StatusCode} to {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {await response.Content.ReadAsStringAsync(cancellationToken)}");
        }
    }
}
