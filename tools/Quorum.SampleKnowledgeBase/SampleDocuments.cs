using System.Globalization;
using System.Text.Json.Nodes;

namespace Quorum.SampleKnowledgeBase;

/// <summary>A document, or an entity with a page of its own, that the sample data point to with a <c>source_url</c>.</summary>
/// <param name="Kind">The first segment of the address: <c>policies</c>, <c>contracts</c>, <c>handbook</c>, <c>people</c>, ...</param>
/// <param name="Id">The second segment, decoded.</param>
/// <param name="Details">What the data say about the document besides its text, in the order they were written.</param>
/// <param name="Sections">The text of each chunk, in order: what was embedded.</param>
public sealed record SampleDocument(string Kind, string Id, string Title, IReadOnlyList<KeyValuePair<string, string>> Details, IReadOnlyList<string> Sections)
{
    public string Path => $"/{Kind}/{Uri.EscapeDataString(Id)}";
}

/// <summary>
/// Where the documents of the sample data live. Every chunk of the samples that points to a document carries the address of a page
/// of the sample documents server (<see cref="SampleDocumentsServer"/>), which serves the text that was embedded, so a citation can
/// be opened. The address starts with <see cref="BaseUrl"/>: a made-up one by default (tests assert on it and nothing opens it), and the
/// address of the server for the samples seeded by hand (<c>--documents-url</c>).
/// </summary>
public static class SampleDocuments
{
    public const string DefaultBaseUrl = "https://intranet.example.test";

    private static readonly string[] TextKeys = ["text", "chunk_text", "content", "document"];
    private static readonly string[] TitleKeys = ["title", "file_name", "name"];
    private static readonly string[] OrderKeys = ["chunk_index", "page"];

    public static string BaseUrl { get; private set; } = DefaultBaseUrl;

    /// <summary>Makes the samples that are built from now on point to <paramref name="baseUrl"/> (the address of the server, without a path).</summary>
    public static void UseBaseUrl(string baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        BaseUrl = baseUrl.TrimEnd('/');
    }

    /// <summary>The address of a document; <paramref name="kind"/> is the first segment of its path.</summary>
    public static string Url(string kind, string id) => $"{BaseUrl}/{kind}/{Uri.EscapeDataString(id)}";

    /// <summary>
    /// Every document the samples of Qdrant and of PostgreSQL point to, put together from the chunks that carry a <c>source_url</c>: the
    /// catalog is the data, so a new sample that points to a document is served without anything else to write.
    /// </summary>
    public static IReadOnlyList<SampleDocument> Catalog()
    {
        var chunks = new List<Chunk>();
        foreach (var point in SampleCollections.All().SelectMany(dataset => dataset.Points))
        {
            chunks.Add(ChunkOf(point.Payload));
        }

        foreach (var relation in SampleRelations.All())
        {
            foreach (var row in relation.Rows)
            {
                var values = new JsonObject();
                foreach (var (column, value) in row.Values)
                {
                    values[column] = value switch
                    {
                        JsonNode node => node.DeepClone(),
                        string text => JsonValue.Create(text),
                        string[] list => new JsonArray(list.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()),
                        long number => JsonValue.Create(number),
                        int number => JsonValue.Create(number),
                        bool flag => JsonValue.Create(flag),
                        DateOnly date => JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                        _ => null,
                    };
                }

                chunks.Add(ChunkOf(values));
            }
        }

        return
        [
            .. chunks
                .Where(chunk => chunk.Url is not null)
                .GroupBy(chunk => chunk.Url!, StringComparer.Ordinal)
                .Select(group => ToDocument(group.Key, group.ToList()))
                .OfType<SampleDocument>()
                .OrderBy(document => document.Kind, StringComparer.Ordinal)
                .ThenBy(document => document.Id, StringComparer.Ordinal),
        ];
    }

    private sealed record Chunk(string? Url, string? Text, string? Title, double Order, List<KeyValuePair<string, string>> Details);

    private static Chunk ChunkOf(JsonObject payload)
    {
        var flat = new List<KeyValuePair<string, string>>();
        Flatten(payload, string.Empty, flat);
        string? Find(string[] keys) => keys.Select(key => flat.FirstOrDefault(pair => pair.Key == key)).FirstOrDefault(pair => pair.Key is not null).Value;

        var order = OrderKeys.Select(key => flat.FirstOrDefault(pair => pair.Key == key)).FirstOrDefault(pair => pair.Key is not null);
        var details = flat
            .Where(pair => pair.Key != "source_url" && !TextKeys.Contains(pair.Key) && !pair.Key.EndsWith(".source_url", StringComparison.Ordinal))
            .ToList();
        return new Chunk(
            flat.FirstOrDefault(pair => pair.Key is "source_url" or "cmetadata.source_url").Value,
            Find(TextKeys),
            Find(TitleKeys),
            order.Value is not null && double.TryParse(order.Value, CultureInfo.InvariantCulture, out var number) ? number : double.MaxValue,
            details);
    }

    // A nested object becomes dotted keys and a list one comma-separated value, so a page can show every field as a row.
    private static void Flatten(JsonNode? node, string prefix, List<KeyValuePair<string, string>> result)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    Flatten(value, prefix.Length == 0 ? key : $"{prefix}.{key}", result);
                }

                break;
            case JsonArray array:
                result.Add(new(prefix, string.Join(", ", array.Select(item => item?.ToString()).Where(item => item is not null))));
                break;
            case JsonValue value:
                result.Add(new(prefix, value.ToString()));
                break;
        }
    }

    private static SampleDocument? ToDocument(string url, List<Chunk> chunks)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length != 2)
        {
            return null;
        }

        var id = Uri.UnescapeDataString(segments[1]);
        var first = chunks[0];

        // The same text can be embedded by more than one sample (the flat collection and the relation made from it): it is shown once.
        var sections = chunks
            .Where(chunk => !string.IsNullOrWhiteSpace(chunk.Text))
            .OrderBy(chunk => chunk.Order)
            .Select(chunk => chunk.Text!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var details = chunks
            .SelectMany(chunk => chunk.Details)
            .Where(pair => pair.Key != "title" && pair.Key != "file_name" && pair.Key != "document_id" && pair.Key != "doc_id" && pair.Key != "id" && pair.Key != "uuid" && pair.Key is not ("page" or "chunk_index" or "is_first_chunk"))
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        return new SampleDocument(segments[0], id, chunks.Select(chunk => chunk.Title).FirstOrDefault(title => !string.IsNullOrWhiteSpace(title)) ?? first.Title ?? id, details, sections);
    }
}
