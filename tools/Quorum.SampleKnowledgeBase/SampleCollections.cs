using System.Text.Json.Nodes;

namespace Quorum.SampleKnowledgeBase;

/// <summary>
/// The sample collections of spec 003 (D-20): one per case the registration has to handle. The content is a few company
/// policies, written so that a question about leave, passwords or travel expenses finds its own chunk.
/// </summary>
public static class SampleCollections
{
    public const string Flat = "sample_flat";
    public const string Nested = "sample_nested";
    public const string Tags = "sample_tags";
    public const string Unindexed = "sample_unindexed";
    public const string NamedVectors = "sample_named_vectors";
    public const string OtherVectorSize = "sample_other_vector_size";
    public const string Empty = "sample_empty";

    /// <summary>The name of the vector to choose in <see cref="NamedVectors"/>: its size matches <see cref="DeterministicEmbedding.DefaultDimensions"/>.</summary>
    public const string DenseVectorName = "dense";

    private const string BaseUrl = "https://intranet.example.test/policies";

    private static readonly Chunk[] Corpus =
    [
        new(1, "leave", "Annual leave policy", 1, "Employees accrue twenty-five days of paid annual leave per year. Leave requests must be approved by the line manager at least two weeks in advance.", "hr", 2024, "2024-01-15", false, ["hr", "leave"]),
        new(2, "leave", "Annual leave policy", 2, "Unused annual leave can be carried over to the next year up to five days. Carried over days expire on the thirty-first of March.", "hr", 2024, "2024-01-15", false, ["hr", "leave"]),
        new(3, "remote", "Remote work guidelines", 1, "Employees may work remotely up to three days per week after agreement with the manager. Remote workers must be reachable during core hours.", "hr", 2023, "2023-06-01", false, ["hr", "remote"]),
        new(4, "security", "Password and access policy", 1, "Passwords must have at least fourteen characters and are changed only when compromised. Multi factor authentication is mandatory for every system that holds company data.", "it", 2024, "2024-03-10", false, ["it", "security"]),
        new(5, "security", "Password and access policy", 2, "Lost or stolen devices must be reported to the security desk within one hour. The device is wiped remotely and access tokens are revoked.", "it", 2024, "2024-03-10", false, ["it", "security"]),
        new(6, "travel", "Travel expense rules", 1, "Travel expenses are reimbursed within thirty days with receipts attached. Flights must be booked in economy class for journeys shorter than six hours.", "finance", 2022, "2022-09-20", false, ["finance", "travel"]),
        new(7, "travel", "Travel expense rules", 2, "Meals during business travel are reimbursed up to forty euros per day. Alcohol is never reimbursed.", "finance", 2022, "2022-09-20", false, ["finance", "travel"]),
        new(8, "onboarding", "Onboarding checklist", 1, "New employees receive a laptop, badge and accounts on their first day. The buddy introduces the team and the tools during the first week.", "hr", 2025, "2025-02-03", false, ["hr", "onboarding"]),
        new(9, "nda", "Confidentiality agreement", 1, "Confidential information may not be shared outside the company without written approval. The obligation continues for five years after the end of employment.", "legal", 2021, "2021-11-30", true, ["legal", "confidential"]),
        new(10, "office", "Office access", 1, "The office is open from seven to nineteen on working days. Visitors must be registered at the reception and wear a badge.", "facilities", 2023, "2023-04-12", false, ["facilities"]),
    ];

    /// <summary>Every sample collection, named <paramref name="prefix"/> + its standard name.</summary>
    public static IReadOnlyList<SampleDataset> All(string prefix = "") =>
    [
        CreateFlat(prefix + Flat),
        CreateNested(prefix + Nested),
        CreateTags(prefix + Tags),
        CreateUnindexed(prefix + Unindexed),
        CreateNamedVectors(prefix + NamedVectors),
        CreateOtherVectorSize(prefix + OtherVectorSize),
        CreateEmpty(prefix + Empty),
    ];

    /// <summary>Flat payload, indexed filter fields: the straightforward case.</summary>
    public static SampleDataset CreateFlat(string name = Flat) => new(
        name,
        "Flat payload (text, document_id, title, page, source_url, department, year, published_on, confidential) with payload indexes on the filter fields.",
        [new SampleVector(null, DeterministicEmbedding.DefaultDimensions)],
        [new("department", "keyword"), new("year", "integer"), new("published_on", "datetime"), new("confidential", "bool")],
        Corpus.Select(FlatPoint).ToList(),
        SampleSchemas.Flat);

    /// <summary>Roles and metadata under different names, and a nested <c>meta</c> object (spec 003 AC-7).</summary>
    public static SampleDataset CreateNested(string name = Nested) => new(
        name,
        "Nested payload: chunk_text, doc_id, file_name, page, source_url and a meta object with department, year and published_on.",
        [new SampleVector(null, DeterministicEmbedding.DefaultDimensions)],
        [new("meta.department", "keyword"), new("meta.year", "integer")],
        Corpus.Select(NestedPoint).ToList(),
        SampleSchemas.Nested);

    /// <summary>A list of texts per chunk.</summary>
    public static SampleDataset CreateTags(string name = Tags) => new(
        name,
        "A tags list (list of texts) beside the text, title and a year.",
        [new SampleVector(null, DeterministicEmbedding.DefaultDimensions)],
        [new("tags", "keyword")],
        Corpus.Select(TagsPoint).ToList());

    /// <summary>The flat payload with no payload index at all: filters on it work but may be slow on large collections.</summary>
    public static SampleDataset CreateUnindexed(string name = Unindexed) => new(
        name,
        "The flat payload without any payload index.",
        [new SampleVector(null, DeterministicEmbedding.DefaultDimensions)],
        [],
        Corpus.Select(FlatPoint).ToList(),
        SampleSchemas.Flat);

    /// <summary>Two named vectors: the registration must ask which one to search.</summary>
    public static SampleDataset CreateNamedVectors(string name = NamedVectors) => new(
        name,
        $"Two named vectors, '{DenseVectorName}' ({DeterministicEmbedding.DefaultDimensions} dimensions) and 'summary' (32): the vector name must be chosen.",
        [new SampleVector(DenseVectorName, DeterministicEmbedding.DefaultDimensions), new SampleVector("summary", 32)],
        [new("department", "keyword"), new("year", "integer")],
        Corpus.Select(FlatPoint).ToList(),
        SampleSchemas.Flat);

    /// <summary>Vectors of 128 dimensions: ingested with another model than the 64-dimension fake, so the verification warns.</summary>
    public static SampleDataset CreateOtherVectorSize(string name = OtherVectorSize) => new(
        name,
        "128-dimension vectors, as if ingested with a different embedding model.",
        [new SampleVector(null, 128)],
        [new("department", "keyword")],
        Corpus.Select(FlatPoint).ToList(),
        SampleSchemas.Flat);

    /// <summary>Ready but not yet filled: registration works, a search finds nothing.</summary>
    public static SampleDataset CreateEmpty(string name = Empty) => new(
        name,
        "A collection with no points yet.",
        [new SampleVector(null, DeterministicEmbedding.DefaultDimensions)],
        [],
        []);

    private static SamplePoint FlatPoint(Chunk chunk) => new(
        chunk.Id,
        chunk.Text,
        new JsonObject
        {
            ["text"] = chunk.Text,
            ["document_id"] = chunk.Document,
            ["title"] = chunk.Title,
            ["page"] = chunk.Page,
            ["source_url"] = $"{BaseUrl}/{chunk.Document}",
            ["department"] = chunk.Department,
            ["year"] = chunk.Year,
            ["published_on"] = chunk.PublishedOn,
            ["confidential"] = chunk.Confidential,
        });

    private static SamplePoint NestedPoint(Chunk chunk) => new(
        chunk.Id,
        chunk.Text,
        new JsonObject
        {
            ["chunk_text"] = chunk.Text,
            ["doc_id"] = chunk.Document,
            ["file_name"] = chunk.Title,
            ["page"] = chunk.Page,
            ["source_url"] = $"{BaseUrl}/{chunk.Document}",
            ["meta"] = new JsonObject
            {
                ["department"] = chunk.Department,
                ["year"] = chunk.Year,
                ["published_on"] = chunk.PublishedOn,
            },
        });

    private static SamplePoint TagsPoint(Chunk chunk) => new(
        chunk.Id,
        chunk.Text,
        new JsonObject
        {
            ["content"] = chunk.Text,
            ["title"] = chunk.Title,
            ["year"] = chunk.Year,
            ["tags"] = new JsonArray(chunk.Tags.Select(tag => (JsonNode?)JsonValue.Create(tag)).ToArray()),
        });

    private sealed record Chunk(
        ulong Id,
        string Document,
        string Title,
        int Page,
        string Text,
        string Department,
        int Year,
        string PublishedOn,
        bool Confidential,
        string[] Tags);
}
