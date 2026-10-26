using System.Text.Json.Nodes;

namespace Quorum.SampleKnowledgeBase;

/// <summary>
/// A vector to write into a column. <paramref name="Half"/> says the column is a <c>halfvec</c>; <paramref name="FromModel"/> that the
/// values come from a real embedding model instead of <see cref="DeterministicEmbedding"/>.
/// </summary>
public sealed record SampleVectorValue(string Text, int Size, bool Half = false, bool FromModel = false);

/// <summary>A row to insert into <paramref name="Table"/>. A <see cref="SampleVectorValue"/> becomes a vector, a <see cref="JsonNode"/> a <c>jsonb</c>.</summary>
public sealed record SampleRow(string Table, IReadOnlyDictionary<string, object?> Values);

/// <summary>A relation as a data owner's ingestion would leave it: the statements that create it and the rows that fill it.</summary>
/// <param name="Statements">The DDL, starting with the statement that drops it so seeding again recreates it.</param>
public sealed record SampleRelation(string Name, string Description, IReadOnlyList<string> Statements, IReadOnlyList<SampleRow> Rows, string? Schema = null)
{
    /// <summary>Statements that need the rows to be there first (an IVFFlat index is trained on them).</summary>
    public IReadOnlyList<string> AfterRows { get; init; } = [];
}

/// <summary>
/// The sample relations of spec 008 (D-18): one per case the PgVector registration has to handle, written with the same policies as
/// <see cref="SampleCollections"/>. Relations are created in the schema the caller names.
/// </summary>
public static class SampleRelations
{
    public const string Flat = "sample_flat";
    public const string LangChain = "sample_langchain";
    public const string Tags = "sample_tags";
    public const string Unindexed = "sample_unindexed";
    public const string TwoVectors = "sample_two_vectors";
    public const string HalfVector = "sample_halfvec";
    public const string UnspecifiedDimensions = "sample_unspecified_dimensions";
    public const string OtherVectorSize = "sample_other_vector_size";
    public const string Empty = "sample_empty";
    public const string View = "sample_view";
    public const string IvfFlatL2 = "sample_ivfflat_l2";
    public const string InnerProduct = "sample_inner_product";
    public const string MixedCase = "Sample Mixed Case";
    public const string DottedColumn = "sample_dotted_column";
    public const string Unmappable = "sample_unmappable";

    /// <summary>The name of the second vector column of <see cref="TwoVectors"/>: its size is not the embedding's.</summary>
    public const string SummaryColumn = "embedding_summary";

    /// <summary>Quotes an identifier the way PostgreSQL does, so a name with capitals, spaces or quotes survives.</summary>
    public static string Quote(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>Every sample relation, in the order they must be created (the view needs the flat table).</summary>
    public static IReadOnlyList<SampleRelation> All(string schema = "public", int dimensions = DeterministicEmbedding.DefaultDimensions, bool fromModel = false) =>
    [
        CreateFlat(schema, dimensions, fromModel),
        CreateLangChain(schema, dimensions, fromModel),
        CreateTags(schema, dimensions, fromModel),
        CreateUnindexed(schema, dimensions, fromModel),
        CreateTwoVectors(schema, dimensions, fromModel),
        CreateHalfVector(schema, dimensions, fromModel),
        CreateUnspecifiedDimensions(schema, dimensions, fromModel),
        CreateOtherVectorSize(schema),
        CreateEmpty(schema, dimensions),
        CreateView(schema),
        CreateIvfFlatL2(schema, dimensions, fromModel),
        CreateInnerProduct(schema, dimensions, fromModel),
        CreateMixedCase(schema, dimensions, fromModel),
        CreateDottedColumn(schema, dimensions, fromModel),
        CreateUnmappable(schema, dimensions, fromModel),
    ];

    /// <summary>Flat columns, an HNSW index with cosine on the vector and B-tree indexes on the filter columns: the straightforward case.</summary>
    public static SampleRelation CreateFlat(string schema = "public", int dimensions = DeterministicEmbedding.DefaultDimensions, bool fromModel = false)
    {
        var table = Table(schema, Flat);
        return new(
            Flat,
            "Flat columns (id, text, document_id, title, page, source_url, department, year, published_on, confidential), HNSW with cosine on the vector and B-tree indexes on the filter columns and on document_id.",
            [
                $"DROP TABLE IF EXISTS {table} CASCADE",
                $"""
                CREATE TABLE {table} (
                    id bigint PRIMARY KEY, text text NOT NULL, document_id text, title text, page integer, source_url text,
                    department text, year integer, published_on date, confidential boolean, embedding vector({dimensions}))
                """,
                $"CREATE INDEX ON {table} USING hnsw (embedding vector_cosine_ops)",
                $"CREATE INDEX ON {table} (document_id)",
                $"CREATE INDEX ON {table} (department)",
                $"CREATE INDEX ON {table} (year)",
                $"CREATE INDEX ON {table} (published_on)",
                $"CREATE INDEX ON {table} (confidential)",
            ],
            SampleCollections.Corpus.Select(chunk => FlatRow(Flat, chunk, new SampleVectorValue(chunk.Text, dimensions, FromModel: fromModel))).ToList(),
            SampleSchemas.RelationFlat);
    }

    /// <summary>A table as LangChain's PGVector store leaves it: the text in <c>document</c> and every metadata value inside one <c>jsonb</c> column.</summary>
    public static SampleRelation CreateLangChain(string schema = "public", int dimensions = DeterministicEmbedding.DefaultDimensions, bool fromModel = false)
    {
        var table = Table(schema, LangChain);
        return new(
            LangChain,
            "A LangChain-style table: uuid, document, cmetadata (jsonb with document_id, title, page, source_url, department, year, published_on, confidential) and the vector, with HNSW and cosine.",
            [
                $"DROP TABLE IF EXISTS {table} CASCADE",
                $"CREATE TABLE {table} (uuid uuid PRIMARY KEY, document text NOT NULL, cmetadata jsonb, embedding vector({dimensions}))",
                $"CREATE INDEX ON {table} USING hnsw (embedding vector_cosine_ops)",
            ],
            SampleCollections.Corpus.Select(chunk => new SampleRow(
                LangChain,
                new Dictionary<string, object?>
                {
                    ["uuid"] = new Guid((int)chunk.Id, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]),
                    ["document"] = chunk.Text,
                    ["cmetadata"] = new JsonObject
                    {
                        ["document_id"] = chunk.Document,
                        ["title"] = chunk.Title,
                        ["page"] = chunk.Page,
                        ["source_url"] = $"{SampleCollections.BaseUrl}/{chunk.Document}",
                        ["department"] = chunk.Department,
                        ["year"] = chunk.Year,
                        ["published_on"] = chunk.PublishedOn,
                        ["confidential"] = chunk.Confidential,
                    },
                    ["embedding"] = new SampleVectorValue(chunk.Text, dimensions, FromModel: fromModel),
                })).ToList(),
            SampleSchemas.RelationLangChain);
    }

    /// <summary>A <c>text[]</c> of tags beside the text, with a GIN index.</summary>
    public static SampleRelation CreateTags(string schema = "public", int dimensions = DeterministicEmbedding.DefaultDimensions, bool fromModel = false)
    {
        var table = Table(schema, Tags);
        return new(
            Tags,
            "A tags column (text[]) beside content, title and year.",
            [
                $"DROP TABLE IF EXISTS {table} CASCADE",
                $"CREATE TABLE {table} (id bigint PRIMARY KEY, content text NOT NULL, title text, year integer, tags text[], embedding vector({dimensions}))",
                $"CREATE INDEX ON {table} USING hnsw (embedding vector_cosine_ops)",
                $"CREATE INDEX ON {table} USING gin (tags)",
            ],
            SampleCollections.Corpus.Select(chunk => new SampleRow(
                Tags,
                new Dictionary<string, object?>
                {
                    ["id"] = (long)chunk.Id,
                    ["content"] = chunk.Text,
                    ["title"] = chunk.Title,
                    ["year"] = chunk.Year,
                    ["tags"] = chunk.Tags,
                    ["embedding"] = new SampleVectorValue(chunk.Text, dimensions, FromModel: fromModel),
                })).ToList());
    }

    /// <summary>The flat table with no index at all: not on the vector (the search is exact) and not on the filter columns.</summary>
    public static SampleRelation CreateUnindexed(string schema = "public", int dimensions = DeterministicEmbedding.DefaultDimensions, bool fromModel = false)
    {
        var table = Table(schema, Unindexed);
        return new(
            Unindexed,
            "The flat columns with no index on the vector or on the filter columns (only the primary key).",
            [
                $"DROP TABLE IF EXISTS {table} CASCADE",
                $"""
                CREATE TABLE {table} (
                    id bigint PRIMARY KEY, text text NOT NULL, document_id text, title text, page integer, source_url text,
                    department text, year integer, published_on date, confidential boolean, embedding vector({dimensions}))
                """,
            ],
            SampleCollections.Corpus.Select(chunk => FlatRow(Unindexed, chunk, new SampleVectorValue(chunk.Text, dimensions, FromModel: fromModel))).ToList(),
            SampleSchemas.RelationFlat);
    }

    /// <summary>Two vector columns: the registration must ask which one to search.</summary>
    public static SampleRelation CreateTwoVectors(string schema = "public", int dimensions = DeterministicEmbedding.DefaultDimensions, bool fromModel = false)
    {
        var table = Table(schema, TwoVectors);
        return new(
            TwoVectors,
            $"Two vector columns, embedding ({dimensions} dimensions) and {SummaryColumn} (32): the column must be chosen.",
            [
                $"DROP TABLE IF EXISTS {table} CASCADE",
                $"CREATE TABLE {table} (id bigint PRIMARY KEY, text text NOT NULL, title text, embedding vector({dimensions}), {SummaryColumn} vector(32))",
                $"CREATE INDEX ON {table} USING hnsw (embedding vector_cosine_ops)",
            ],
            SampleCollections.Corpus.Select(chunk => new SampleRow(
                TwoVectors,
                new Dictionary<string, object?>
                {
                    ["id"] = (long)chunk.Id,
                    ["text"] = chunk.Text,
                    ["title"] = chunk.Title,
                    ["embedding"] = new SampleVectorValue(chunk.Text, dimensions, FromModel: fromModel),
                    [SummaryColumn] = new SampleVectorValue(chunk.Title, 32),
                })).ToList());
    }

    /// <summary>A <c>halfvec</c> column with an HNSW index on the half-precision cosine operator class.</summary>
    public static SampleRelation CreateHalfVector(string schema = "public", int dimensions = DeterministicEmbedding.DefaultDimensions, bool fromModel = false)
    {
        var table = Table(schema, HalfVector);
        return new(
            HalfVector,
            "A halfvec column with HNSW and cosine.",
            [
                $"DROP TABLE IF EXISTS {table} CASCADE",
                $"CREATE TABLE {table} (id bigint PRIMARY KEY, text text NOT NULL, title text, embedding halfvec({dimensions}))",
                $"CREATE INDEX ON {table} USING hnsw (embedding halfvec_cosine_ops)",
            ],
            SampleCollections.Corpus.Select(chunk => new SampleRow(
                HalfVector,
                new Dictionary<string, object?>
                {
                    ["id"] = (long)chunk.Id,
                    ["text"] = chunk.Text,
                    ["title"] = chunk.Title,
                    ["embedding"] = new SampleVectorValue(chunk.Text, dimensions, Half: true, FromModel: fromModel),
                })).ToList());
    }

    /// <summary>A <c>vector</c> declared without dimensions: they can only be measured on a row, and an index cannot be built on it.</summary>
    public static SampleRelation CreateUnspecifiedDimensions(string schema = "public", int dimensions = DeterministicEmbedding.DefaultDimensions, bool fromModel = false)
    {
        var table = Table(schema, UnspecifiedDimensions);
        return new(
            UnspecifiedDimensions,
            "A vector column declared without dimensions.",
            [
                $"DROP TABLE IF EXISTS {table} CASCADE",
                $"CREATE TABLE {table} (id bigint PRIMARY KEY, text text NOT NULL, title text, embedding vector)",
            ],
            SampleCollections.Corpus.Select(chunk => new SampleRow(
                UnspecifiedDimensions,
                new Dictionary<string, object?>
                {
                    ["id"] = (long)chunk.Id,
                    ["text"] = chunk.Text,
                    ["title"] = chunk.Title,
                    ["embedding"] = new SampleVectorValue(chunk.Text, dimensions, FromModel: fromModel),
                })).ToList());
    }

    /// <summary>Vectors of 128 dimensions, as if ingested with another model than the 64-dimension fake: verification warns.</summary>
    public static SampleRelation CreateOtherVectorSize(string schema = "public")
    {
        var table = Table(schema, OtherVectorSize);
        return new(
            OtherVectorSize,
            "128-dimension vectors, as if ingested with a different embedding model.",
            [
                $"DROP TABLE IF EXISTS {table} CASCADE",
                $"CREATE TABLE {table} (id bigint PRIMARY KEY, text text NOT NULL, title text, department text, embedding vector(128))",
                $"CREATE INDEX ON {table} USING hnsw (embedding vector_cosine_ops)",
            ],
            SampleCollections.Corpus.Select(chunk => new SampleRow(
                OtherVectorSize,
                new Dictionary<string, object?>
                {
                    ["id"] = (long)chunk.Id,
                    ["text"] = chunk.Text,
                    ["title"] = chunk.Title,
                    ["department"] = chunk.Department,
                    ["embedding"] = new SampleVectorValue(chunk.Text, 128),
                })).ToList());
    }

    /// <summary>Ready but not yet filled: registration works, a search finds nothing.</summary>
    public static SampleRelation CreateEmpty(string schema = "public", int dimensions = DeterministicEmbedding.DefaultDimensions)
    {
        var table = Table(schema, Empty);
        return new(
            Empty,
            "A table with no rows yet.",
            [
                $"DROP TABLE IF EXISTS {table} CASCADE",
                $"CREATE TABLE {table} (id bigint PRIMARY KEY, text text NOT NULL, title text, embedding vector({dimensions}))",
                $"CREATE INDEX ON {table} USING hnsw (embedding vector_cosine_ops)",
            ],
            []);
    }

    /// <summary>A view over the flat table that leaves out the confidential rows: how a data owner adapts a schema without touching the table.</summary>
    public static SampleRelation CreateView(string schema = "public")
    {
        var view = Table(schema, View);
        return new(
            View,
            "A view over sample_flat without the confidential rows.",
            [
                $"DROP VIEW IF EXISTS {view} CASCADE",
                $"""
                CREATE VIEW {view} AS
                SELECT id, text, document_id, title, page, department, year, embedding FROM {Table(schema, Flat)} WHERE NOT confidential
                """,
            ],
            []);
    }

    /// <summary>IVFFlat with the L2 operator class, so the distance proposed is <c>L2</c>.</summary>
    public static SampleRelation CreateIvfFlatL2(string schema = "public", int dimensions = DeterministicEmbedding.DefaultDimensions, bool fromModel = false)
    {
        var table = Table(schema, IvfFlatL2);
        return new(
            IvfFlatL2,
            "IVFFlat with L2 on the vector.",
            [
                $"DROP TABLE IF EXISTS {table} CASCADE",
                $"CREATE TABLE {table} (id bigint PRIMARY KEY, text text NOT NULL, title text, department text, embedding vector({dimensions}))",
            ],
            SampleCollections.Corpus.Select(chunk => SimpleRow(IvfFlatL2, chunk, dimensions, fromModel)).ToList())
        {
            // An IVFFlat index is built from the rows it finds, so it is created after the rows are in.
            AfterRows = [$"CREATE INDEX ON {table} USING ivfflat (embedding vector_l2_ops) WITH (lists = 1)"],
        };
    }

    /// <summary>HNSW with the inner product operator class, so the distance proposed is <c>InnerProduct</c>.</summary>
    public static SampleRelation CreateInnerProduct(string schema = "public", int dimensions = DeterministicEmbedding.DefaultDimensions, bool fromModel = false)
    {
        var table = Table(schema, InnerProduct);
        return new(
            InnerProduct,
            "HNSW with inner product on the vector.",
            [
                $"DROP TABLE IF EXISTS {table} CASCADE",
                $"CREATE TABLE {table} (id bigint PRIMARY KEY, text text NOT NULL, title text, department text, embedding vector({dimensions}))",
                $"CREATE INDEX ON {table} USING hnsw (embedding vector_ip_ops)",
            ],
            SampleCollections.Corpus.Select(chunk => SimpleRow(InnerProduct, chunk, dimensions, fromModel)).ToList());
    }

    /// <summary>Names with capitals, spaces and a reserved word: every identifier has to be quoted.</summary>
    public static SampleRelation CreateMixedCase(string schema = "public", int dimensions = DeterministicEmbedding.DefaultDimensions, bool fromModel = false)
    {
        var table = Table(schema, MixedCase);
        return new(
            MixedCase,
            "A table, columns and a vector column with capitals, spaces and a reserved word (\"Select\").",
            [
                $"DROP TABLE IF EXISTS {table} CASCADE",
                $"CREATE TABLE {table} (\"Row Id\" bigint PRIMARY KEY, \"Chunk Text\" text NOT NULL, \"Select\" text, \"Page\" integer, \"Embedding Vector\" vector({dimensions}))",
                $"CREATE INDEX ON {table} USING hnsw (\"Embedding Vector\" vector_cosine_ops)",
            ],
            SampleCollections.Corpus.Select(chunk => new SampleRow(
                MixedCase,
                new Dictionary<string, object?>
                {
                    ["Row Id"] = (long)chunk.Id,
                    ["Chunk Text"] = chunk.Text,
                    ["Select"] = chunk.Title,
                    ["Page"] = chunk.Page,
                    ["Embedding Vector"] = new SampleVectorValue(chunk.Text, dimensions, FromModel: fromModel),
                })).ToList());
    }

    /// <summary>A column whose name has a dot: it cannot be mapped (paths are split on dots) and is reported.</summary>
    public static SampleRelation CreateDottedColumn(string schema = "public", int dimensions = DeterministicEmbedding.DefaultDimensions, bool fromModel = false)
    {
        var table = Table(schema, DottedColumn);
        return new(
            DottedColumn,
            "A column named \"meta.year\".",
            [
                $"DROP TABLE IF EXISTS {table} CASCADE",
                $"CREATE TABLE {table} (id bigint PRIMARY KEY, text text NOT NULL, \"meta.year\" integer, embedding vector({dimensions}))",
            ],
            SampleCollections.Corpus.Select(chunk => new SampleRow(
                DottedColumn,
                new Dictionary<string, object?>
                {
                    ["id"] = (long)chunk.Id,
                    ["text"] = chunk.Text,
                    ["meta.year"] = chunk.Year,
                    ["embedding"] = new SampleVectorValue(chunk.Text, dimensions, FromModel: fromModel),
                })).ToList());
    }

    /// <summary>Columns of types that are not mapped (<c>bytea</c>, a range, an <c>interval</c>, an array of integers) beside the text.</summary>
    public static SampleRelation CreateUnmappable(string schema = "public", int dimensions = DeterministicEmbedding.DefaultDimensions, bool fromModel = false)
    {
        var table = Table(schema, Unmappable);
        return new(
            Unmappable,
            "Columns of unmapped types: bytea, int4range, interval and integer[].",
            [
                $"DROP TABLE IF EXISTS {table} CASCADE",
                $"""
                CREATE TABLE {table} (
                    id bigint PRIMARY KEY, text text NOT NULL, title text, raw bytea, years int4range, duration interval, scores integer[],
                    embedding vector({dimensions}))
                """,
            ],
            SampleCollections.Corpus.Select(chunk => new SampleRow(
                Unmappable,
                new Dictionary<string, object?>
                {
                    ["id"] = (long)chunk.Id,
                    ["text"] = chunk.Text,
                    ["title"] = chunk.Title,
                    ["raw"] = new byte[] { 1, 2, 3 },
                    ["scores"] = new[] { chunk.Page, chunk.Year },
                    ["embedding"] = new SampleVectorValue(chunk.Text, dimensions, FromModel: fromModel),
                })).ToList());
    }

    private static string Table(string schema, string name) => $"{Quote(schema)}.{Quote(name)}";

    private static SampleRow FlatRow(string table, SampleCollections.Chunk chunk, SampleVectorValue vector) => new(
        table,
        new Dictionary<string, object?>
        {
            ["id"] = (long)chunk.Id,
            ["text"] = chunk.Text,
            ["document_id"] = chunk.Document,
            ["title"] = chunk.Title,
            ["page"] = chunk.Page,
            ["source_url"] = $"{SampleCollections.BaseUrl}/{chunk.Document}",
            ["department"] = chunk.Department,
            ["year"] = chunk.Year,
            ["published_on"] = DateOnly.Parse(chunk.PublishedOn, System.Globalization.CultureInfo.InvariantCulture),
            ["confidential"] = chunk.Confidential,
            ["embedding"] = vector,
        });

    private static SampleRow SimpleRow(string table, SampleCollections.Chunk chunk, int dimensions, bool fromModel) => new(
        table,
        new Dictionary<string, object?>
        {
            ["id"] = (long)chunk.Id,
            ["text"] = chunk.Text,
            ["title"] = chunk.Title,
            ["department"] = chunk.Department,
            ["embedding"] = new SampleVectorValue(chunk.Text, dimensions, FromModel: fromModel),
        });
}
