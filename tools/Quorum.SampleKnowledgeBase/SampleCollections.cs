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
    public const string Lookups = "sample_lookups";
    public const string LookupsUnindexed = "sample_lookups_unindexed";
    public const string Suppliers = "sample_suppliers";
    public const string Company = "sample_company";
    public const string Vendors = "sample_vendors";

    /// <summary>The name of the vector to choose in <see cref="NamedVectors"/>: its size matches <see cref="DeterministicEmbedding.DefaultDimensions"/>.</summary>
    public const string DenseVectorName = "dense";

    internal const string BaseUrl = "https://intranet.example.test/policies";

    internal static readonly Chunk[] Corpus =
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
        CreateLookups(prefix + Lookups),
        CreateLookupsUnindexed(prefix + LookupsUnindexed),
        CreateSuppliers(prefix + Suppliers),
        CompanySampleData.CreateContracts(prefix + Company),
        CompanySampleData.CreateVendors(prefix + Vendors),
    ];

    /// <summary>Flat payload, indexed filter fields: the straightforward case.</summary>
    public static SampleDataset CreateFlat(string name = Flat) => new(
        name,
        "Flat payload (text, document_id, title, page, source_url, department, year, published_on, confidential) with payload indexes on the filter fields and on document_id.",
        [new SampleVector(null, DeterministicEmbedding.DefaultDimensions)],
        [new("document_id", "keyword"), new("department", "keyword"), new("year", "integer"), new("published_on", "datetime"), new("confidential", "bool")],
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


    /// <summary>
    /// Contracts of a company, several chunks each, for the lookups of spec 007: a supplier, a department, a nationality and a list of
    /// audiences per document, a flag that is true on the first chunk of each document and the index of the chunk. Indexed.
    /// </summary>
    public static SampleDataset CreateLookups(string name = Lookups) => new(
        name,
        "Contracts with supplier, department, nationality, audience (list), is_first_chunk and chunk_index, with payload indexes on all of them.",
        [new SampleVector(null, DeterministicEmbedding.DefaultDimensions)],
        [
            new("document_id", "keyword"),
            new("supplier", "keyword"),
            new("department", "keyword"),
            new("nationality", "keyword"),
            new("audience", "keyword"),
            new("is_first_chunk", "bool"),
            new("chunk_index", "integer"),
        ],
        ContractPoints(),
        SampleSchemas.Lookups);

    /// <summary>The same contracts with no payload index: a lookup on any of them fails, and says which field needs one (spec 007 AC-7).</summary>
    public static SampleDataset CreateLookupsUnindexed(string name = LookupsUnindexed) => new(
        name,
        "The contracts of the lookups collection without any payload index.",
        [new SampleVector(null, DeterministicEmbedding.DefaultDimensions)],
        [],
        ContractPoints(),
        SampleSchemas.Lookups);

    /// <summary>
    /// A collection of entities for the entity lookups of spec 007 (D-4): one point per supplier, with its name, country, the number of
    /// contracts and the date it became a supplier. The text is a description, since a mapping always needs the text role.
    /// </summary>
    public static SampleDataset CreateSuppliers(string name = Suppliers) => new(
        name,
        "One point per supplier (name, country, contract_count, supplier_since), as a data owner keeps a collection of entities.",
        [new SampleVector(null, DeterministicEmbedding.DefaultDimensions)],
        [new("country", "keyword"), new("contract_count", "integer"), new("supplier_since", "datetime")],
        SupplierRows().Select(SupplierPoint).ToList(),
        SampleSchemas.Suppliers);

    private static SamplePoint SupplierPoint((ulong Id, string Name, string Country, int Contracts, string Since) supplier)
    {
        var text = $"{supplier.Name} supplies the company from {supplier.Country}.";
        return new SamplePoint(
            supplier.Id,
            text,
            new JsonObject
            {
                ["text"] = text,
                ["name"] = supplier.Name,
                ["country"] = supplier.Country,
                ["contract_count"] = supplier.Contracts,
                ["supplier_since"] = supplier.Since,
            });
    }

    // Contract counts are all different, so an order by them is the same for every consumer.
    private static IEnumerable<(ulong Id, string Name, string Country, int Contracts, string Since)> SupplierRows() =>
    [
        (1, "Acme", "IT", 12, "2019-03-01"),
        (2, "Globex", "US", 5, "2021-06-15"),
        (3, "Initech", "DE", 1, "2023-01-10"),
        (4, "Hooli", "US", 8, "2020-09-01"),
    ];

    private static List<SamplePoint> ContractPoints()
    {
        var points = new List<SamplePoint>();
        ulong id = 1;
        foreach (var contract in Contracts)
        {
            for (var chunk = 0; chunk < contract.Sections.Length; chunk++)
            {
                var text = contract.Sections[chunk];
                points.Add(new SamplePoint(
                    id++,
                    text,
                    new JsonObject
                    {
                        ["text"] = text,
                        ["document_id"] = contract.Document,
                        ["title"] = contract.Title,
                        ["source_url"] = $"{BaseUrl}/{contract.Document}",
                        ["supplier"] = contract.Supplier,
                        ["department"] = contract.Department,
                        ["nationality"] = contract.Nationality,
                        ["audience"] = new JsonArray(contract.Audience.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
                        ["is_first_chunk"] = chunk == 0,
                        ["chunk_index"] = chunk,
                    }));
            }
        }

        return points;
    }

    /// <param name="Sections">The text of each chunk of the contract, in order: a model that reads them finds what the contract really says.</param>
    private sealed record Contract(string Document, string Title, string Supplier, string Department, string Nationality, string[] Audience, string[] Sections);

    // Documents per supplier: Acme 3, Globex 2, Initech 1. Per department: facilities 2, it 2, finance 1, legal 1. Eleven chunks in all.
    private static readonly Contract[] Contracts =
    [
        new(
            "c1",
            "Cleaning services contract",
            "Acme",
            "facilities",
            "IT",
            ["all"],
            [
                "Acme provides daily cleaning of all office floors, meeting rooms and restrooms, Monday to Friday between 6:00 and 8:00, and a deep clean of the kitchens and the windows once a month. Soap, paper and other consumables are supplied by Acme.",
                "The contract runs for 24 months from 1 January 2025 and renews automatically for 12 months unless either party gives 90 days' written notice. The fee is 4,800 euros per month, invoiced monthly and payable within 30 days.",
                "A cleaning that is missed must be redone within 24 hours at no cost. Acme carries liability insurance of 2 million euros, and the company may deduct 2% of the monthly fee for each missed service beyond three in a month.",
            ]),
        new(
            "c2",
            "Laptop supply agreement",
            "Acme",
            "it",
            "IT",
            ["it"],
            [
                "Acme supplies up to 200 business laptops per year, in the models and specifications chosen by the IT department. Each laptop has a three-year on-site warranty with replacement by the next business day, and is delivered to the IT warehouse within 15 working days of the order.",
                "Prices are fixed for 24 months: 1,250 euros for a standard laptop and 1,900 euros for a high-performance one. Devices that are returned are collected and wiped by Acme under the company's data-erasure procedure, with a certificate of erasure for each device.",
            ]),
        new(
            "c3",
            "Cloud hosting agreement",
            "Globex",
            "it",
            "US",
            ["it", "managers"],
            [
                "Globex hosts the internal applications of the company on dedicated cloud infrastructure in two European regions. It guarantees an availability of 99.9% per month, monitors the systems around the clock and keeps encrypted daily backups for 30 days.",
                "The fee is 9,500 dollars per month plus any usage above the agreed quota. Security incidents must be reported to the company within four hours. The data remains the property of the company and is returned in an open format within 30 days of the end of the contract.",
            ]),
        new(
            "c4",
            "Legal retainer",
            "Initech",
            "legal",
            "DE",
            ["managers"],
            [
                "Initech gives legal advice on commercial contracts, employment matters and data protection for a fixed monthly retainer of 6,000 euros that covers up to 25 hours. Further hours are billed at 220 euros. All advice is confidential and is delivered within three working days of a request.",
            ]),
        new(
            "c5",
            "Catering contract",
            "Acme",
            "facilities",
            "IT",
            ["all"],
            [
                "Acme provides lunch for up to 150 employees on every working day, from a weekly menu that always includes vegetarian, vegan and gluten-free dishes. Meals are served in the company canteen between 12:00 and 14:30.",
                "The price is 8.50 euros per meal, billed monthly on the number of meals served. Acme must follow the food-safety rules in force, allow an audit of the kitchen with 48 hours' notice, and replace any dish that does not meet the agreed quality.",
            ]),
        new(
            "c6",
            "Travel agency agreement",
            "Globex",
            "finance",
            "US",
            ["managers"],
            [
                "Globex books flights, hotels and rail tickets for business trips at the negotiated corporate rates, with an emergency line for travellers open around the clock. Bookings above 1,500 euros need the approval of the traveller's manager, and Globex charges a service fee of 25 euros per booking.",
            ]),
    ];

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

    internal sealed record Chunk(
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
