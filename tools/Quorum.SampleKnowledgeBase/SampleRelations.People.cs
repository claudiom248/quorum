using System.Text.Json.Nodes;

namespace Quorum.SampleKnowledgeBase;

// The people and rules of the company, for the lookups of a PostgreSQL knowledge base (spec 008 Amendment 2): see PeopleSampleData.
public static partial class SampleRelations
{
    public const string Handbook = "sample_handbook";
    public const string HandbookJson = "sample_handbook_json";
    public const string Employees = "sample_employees";

    /// <summary>
    /// The handbook of policies, one row per section: flat columns, <c>audience</c> as a <c>text[]</c>, <c>country</c> where a rule is for
    /// one country, and <c>is_first_chunk</c> (the counted point), all indexed.
    /// </summary>
    public static SampleRelation CreateHandbook(string schema = "public", int dimensions = DeterministicEmbedding.DefaultDimensions, bool fromModel = false)
    {
        var table = Table(schema, Handbook);
        return new(
            Handbook,
            "The policies of the company (18 documents of two sections): department, audience (text[]), country, status, year, effective_on, confidential, is_first_chunk and chunk_index, all indexed; the lookups of spec 007.",
            [
                $"DROP TABLE IF EXISTS {table} CASCADE",
                $"""
                CREATE TABLE {table} (
                    id bigint PRIMARY KEY, document_id text NOT NULL, title text NOT NULL, text text NOT NULL, department text, audience text[],
                    country text, status text, year integer, effective_on date, confidential boolean, is_first_chunk boolean, chunk_index integer,
                    source_url text, embedding vector({dimensions}))
                """,
                $"CREATE INDEX ON {table} USING hnsw (embedding vector_cosine_ops)",
                $"CREATE INDEX ON {table} USING gin (audience)",
                $"CREATE INDEX ON {table} (document_id)",
                $"CREATE INDEX ON {table} (department)",
                $"CREATE INDEX ON {table} (country)",
                $"CREATE INDEX ON {table} (status)",
                $"CREATE INDEX ON {table} (year)",
                $"CREATE INDEX ON {table} (effective_on)",
                $"CREATE INDEX ON {table} (confidential)",
                $"CREATE INDEX ON {table} (is_first_chunk)",
                $"CREATE INDEX ON {table} (chunk_index)",
            ],
            PeopleSampleData.Chunks().Select(entry => new SampleRow(
                Handbook,
                new Dictionary<string, object?>
                {
                    ["id"] = entry.Id,
                    ["document_id"] = entry.Policy.Id,
                    ["title"] = entry.Policy.Title,
                    ["text"] = entry.Text,
                    ["department"] = entry.Policy.Department,
                    ["audience"] = entry.Policy.Audience,
                    ["country"] = entry.Policy.Country,
                    ["status"] = entry.Policy.Status,
                    ["year"] = entry.Policy.Year,
                    ["effective_on"] = PeopleSampleData.Date(entry.Policy.EffectiveOn),
                    ["confidential"] = entry.Policy.Confidential,
                    ["is_first_chunk"] = entry.Chunk == 0,
                    ["chunk_index"] = entry.Chunk,
                    ["source_url"] = PeopleSampleData.UrlOf(entry.Policy),
                    ["embedding"] = new SampleVectorValue(entry.Text, dimensions, FromModel: fromModel),
                })).ToList());
    }

    /// <summary>The same handbook as LangChain's PGVector store would leave it: every metadata value, the audience list included, in one <c>jsonb</c>.</summary>
    public static SampleRelation CreateHandbookJson(string schema = "public", int dimensions = DeterministicEmbedding.DefaultDimensions, bool fromModel = false)
    {
        var table = Table(schema, HandbookJson);
        return new(
            HandbookJson,
            "The handbook in the shape of a LangChain table: uuid, document, cmetadata (jsonb with department, audience as a list, country, status, year, effective_on, confidential, is_first_chunk, chunk_index) and the vector.",
            [
                $"DROP TABLE IF EXISTS {table} CASCADE",
                $"CREATE TABLE {table} (uuid uuid PRIMARY KEY, document text NOT NULL, cmetadata jsonb, embedding vector({dimensions}))",
                $"CREATE INDEX ON {table} USING hnsw (embedding vector_cosine_ops)",
                $"CREATE INDEX ON {table} USING gin (cmetadata jsonb_path_ops)",
            ],
            PeopleSampleData.Chunks().Select(entry => new SampleRow(
                HandbookJson,
                new Dictionary<string, object?>
                {
                    ["uuid"] = new Guid((int)entry.Id, 1, 0, [0, 0, 0, 0, 0, 0, 0, 2]),
                    ["document"] = entry.Text,
                    ["cmetadata"] = PeopleSampleData.MetadataOf(entry.Policy, entry.Chunk),
                    ["embedding"] = new SampleVectorValue(entry.Text, dimensions, FromModel: fromModel),
                })).ToList(),
            SampleSchemas.RelationLangChain);
    }

    /// <summary>
    /// The employees as entities, one row each: department and country shared with the contracts, <c>skills</c> and
    /// <c>vendor_contacts</c> as <c>text[]</c> (the vendors of the contracts they deal with), for the entity lookup with <c>name</c> as the entity name.
    /// </summary>
    public static SampleRelation CreateEmployees(string schema = "public", int dimensions = DeterministicEmbedding.DefaultDimensions, bool fromModel = false)
    {
        var table = Table(schema, Employees);
        return new(
            Employees,
            "36 employees (name, department, country, role, level, hired_on, remote, skills and vendor_contacts as text[]) in the departments and countries of the contracts: entity lookups.",
            [
                $"DROP TABLE IF EXISTS {table} CASCADE",
                $"""
                CREATE TABLE {table} (
                    id bigint PRIMARY KEY, name text NOT NULL, text text NOT NULL, department text, country text, role text, level text,
                    hired_on date, remote boolean, skills text[], vendor_contacts text[], source_url text, embedding vector({dimensions}))
                """,
                $"CREATE INDEX ON {table} USING hnsw (embedding vector_cosine_ops)",
                $"CREATE INDEX ON {table} (name)",
                $"CREATE INDEX ON {table} (department)",
                $"CREATE INDEX ON {table} (country)",
                $"CREATE INDEX ON {table} (level)",
                $"CREATE INDEX ON {table} (hired_on)",
                $"CREATE INDEX ON {table} USING gin (skills)",
                $"CREATE INDEX ON {table} USING gin (vendor_contacts)",
            ],
            PeopleSampleData.Employees.Select(employee => new SampleRow(
                Employees,
                new Dictionary<string, object?>
                {
                    ["id"] = employee.Id,
                    ["name"] = employee.Name,
                    ["text"] = employee.Description,
                    ["department"] = employee.Department,
                    ["country"] = employee.Country,
                    ["role"] = employee.Role,
                    ["level"] = employee.Level,
                    ["hired_on"] = PeopleSampleData.Date(employee.HiredOn),
                    ["remote"] = employee.Remote,
                    ["skills"] = employee.Skills,
                    ["vendor_contacts"] = employee.VendorContacts,
                    ["source_url"] = PeopleSampleData.UrlOf(employee),
                    ["embedding"] = new SampleVectorValue(employee.Description, dimensions, FromModel: fromModel),
                })).ToList());
    }
}
