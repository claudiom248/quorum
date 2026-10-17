using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Quorum.SampleKnowledgeBase;

/// <summary>
/// Writes sample collections into a Qdrant server over REST. This is what an ingestion pipeline does and what Quorum itself never
/// does, so the product has no such code: it lives here, next to the data it creates. The client's base address is the server.
/// </summary>
public sealed class QdrantSeeder(HttpClient client)
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
            await SendAsync(
                HttpMethod.Put,
                $"collections/{Uri.EscapeDataString(collection.Name)}/points?wait=true",
                new JsonObject { ["points"] = new JsonArray(collection.Points.Select(point => (JsonNode?)PointBody(collection, point)).ToArray()) },
                cancellationToken);
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

        await SendAsync(
            HttpMethod.Put,
            $"collections/{Uri.EscapeDataString(shape.Name)}/points?wait=true",
            new JsonObject { ["points"] = new JsonArray(points.Select(point => (JsonNode?)PointBody(shape, point)).ToArray()) },
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

    private static JsonObject PointBody(SampleDataset collection, SamplePoint point)
    {
        JsonNode vector;
        if (collection.Vectors is [{ Name: null } single])
        {
            vector = ToArray(DeterministicEmbedding.Embed(point.EmbeddingText, single.Size));
        }
        else
        {
            var named = new JsonObject();
            foreach (var item in collection.Vectors)
            {
                named[item.Name!] = ToArray(DeterministicEmbedding.Embed(point.EmbeddingText, item.Size));
            }

            vector = named;
        }

        return new JsonObject { ["id"] = point.Id, ["vector"] = vector, ["payload"] = point.Payload.DeepClone() };
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
