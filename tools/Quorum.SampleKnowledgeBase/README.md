# Quorum.SampleKnowledgeBase

Sample knowledge bases for developing and testing the knowledge base module (spec 003, D-20). It does what a data owner's
ingestion would do and what Quorum itself never does: it writes collections into Qdrant. Nothing in the product references it;
only the integration tests do.

## Seed a Qdrant

With the Qdrant of the base compose file (`--profile qdrant`), or the one of the dev compose file:

```bash
docker compose --profile qdrant up -d qdrant
dotnet run --project tools/Quorum.SampleKnowledgeBase
```

```bash
docker compose -f deploy/docker-compose.dev.yml up -d qdrant
dotnet run --project tools/Quorum.SampleKnowledgeBase
```

Options: `--url <qdrant REST address>` (default `http://localhost:6333`), `--api-key <key>`, `--prefix <text>` (prepended to the
collection names). Seeding again recreates the collections.

To query the collections from a running Quorum, embed them with a real model instead of the deterministic fake, on any
OpenAI-compatible endpoint (`--embedding-url`, `--embedding-model`, `--embedding-key`, `--document-prefix`):

```bash
dotnet run --project tools/Quorum.SampleKnowledgeBase -- --embedding-url http://localhost:11434/v1 --embedding-model nomic-embed-text --document-prefix "search_document: "
```

The vectors of the default size become the model's dimension; the ones that are different on purpose (`sample_other_vector_size`,
the `summary` vector) stay deterministic. The walk-through is in [docs/knowledge-bases.md](../../docs/knowledge-bases.md).

## The collections

| Collection | What it is there to exercise |
|---|---|
| `sample_flat` | Flat payload (`text`, `document_id`, `title`, `page`, `source_url`, `department`, `year`, `published_on`, `confidential`), payload indexes on the filter fields |
| `sample_nested` | Other role names (`chunk_text`, `doc_id`, `file_name`) and a nested `meta` object (`meta.year`, ...) |
| `sample_tags` | A list of texts (`tags`) |
| `sample_unindexed` | The flat payload with no payload index at all |
| `sample_named_vectors` | Two named vectors, `dense` (64) and `summary` (32): the vector name must be chosen |
| `sample_other_vector_size` | 128-dimension vectors, as if ingested with a different model: the verification warns |
| `sample_empty` | Ready but with no points |
| `sample_lookups` | Contracts with `supplier`, `department`, `nationality`, `audience` (list), `is_first_chunk` and `chunk_index`, all indexed: computed lookups (spec 007) |
| `sample_lookups_unindexed` | The same contracts with no payload index: a lookup fails and names the field that needs one |
| `sample_suppliers` | One point per supplier (`name`, `country`, `contract_count`, `supplier_since`): entity lookups, with `name` as the entity name (spec 007) |
| `sample_company` | About forty contracts of sixteen kinds across ten departments, from 2018 to 2025, in four states, for many audiences (a list), with `year`, `status`, `confidential`, `value_eur` and `signed_on`, all indexed: questions with more than one answer for the lookups and the lookup tool |
| `sample_vendors` | The 18 vendors of `sample_company` as entities (`name`, `country`, `category`, `contract_count`, `supplier_since`, `rating`, `preferred`), with contract counts that match the contracts |

## Seed a PostgreSQL with pgvector (spec 008)

The same tool writes sample **relations** into a PostgreSQL that has the pgvector extension, for the `PgVector` kind. Use a database
of your own, never the platform's: the dev compose's PostgreSQL has the extension, so a separate database on it works.

```bash
docker compose -f deploy/docker-compose.dev.yml up -d postgres
docker compose -f deploy/docker-compose.dev.yml exec postgres psql -U quorum -c "CREATE DATABASE samples"
dotnet run --project tools/Quorum.SampleKnowledgeBase -- --pgvector "Host=localhost;Database=samples;Username=quorum;Password=quorum-dev"
```

Options: `--pgvector <connection string>` (Npgsql format; Qdrant is then left alone), `--relation-schema <name>` (default `public`), and the same
`--embedding-*` options as above to embed with a real model. The extension and the schema are created if they are missing, and seeding again
recreates the relations.

| Relation | What it is there to exercise |
|---|---|
| `sample_flat` | Flat columns (`text`, `document_id`, `title`, `page`, `source_url`, `department`, `year`, `published_on`, `confidential`), HNSW with cosine and B-tree indexes on the filter columns |
| `sample_langchain` | A LangChain-style table: `uuid`, `document`, `cmetadata` (`jsonb` with every metadata key) and the vector |
| `sample_tags` | A `text[]` column (`tags`) with a GIN index |
| `sample_unindexed` | The flat columns with no index at all, not even on the vector |
| `sample_two_vectors` | Two vector columns, `embedding` (64) and `embedding_summary` (32): the column must be chosen |
| `sample_halfvec` | A `halfvec` column with HNSW and cosine |
| `sample_unspecified_dimensions` | A `vector` declared without dimensions: they are measured on a row |
| `sample_other_vector_size` | 128-dimension vectors, as if ingested with a different model: the verification warns |
| `sample_empty` | Ready but with no rows |
| `sample_view` | A view over `sample_flat` without the confidential rows |
| `sample_ivfflat_l2` | IVFFlat with L2: the distance proposed is `L2` |
| `sample_inner_product` | HNSW with inner product: the distance proposed is `InnerProduct` |
| `Sample Mixed Case` | Capitals, spaces and a reserved word (`"Select"`) in the names: every identifier is quoted |
| `sample_dotted_column` | A column named `meta.year`, which cannot be mapped and is reported |
| `sample_unmappable` | `bytea`, `int4range`, `interval` and `integer[]` columns, which cannot be mapped and are reported |

The pasteable schemas of the rows are `SampleSchemas.RelationFlat` and `SampleSchemas.RelationLangChain`.

## Trying the lookups (spec 007)

Seed with a real model (see above), then register the collections and create the tools in the admin:

1. Register `sample_company` (qdrant `http://qdrant:6334`). In the lookups step mark `supplier`, `department`, `nationality` and `audience` as **listable** and `is_first_chunk` as the **counted point** (the draft proposes it). The lists are computed in the background; open *Lookups* to see them become *Ready*.
2. Register `sample_vendors` with `name` as the **entity name** and `contract_count`, `country`, `category` and `rating` as attributes: the entity lookup lists the 18 vendors with the contracts each one has.
3. Create a lookup tool that binds the lists and both knowledge bases, exposing `department`, `nationality`, `audience` and `year` as filters, and a rag tool over `sample_company` with the `document_ids` switch on. Activate both for an assistant.
4. Ask: "list the suppliers", "which suppliers have the most contracts", "which policies are for managers", "which teams are Italian", and "which contracts mention termination, and what does the notice period say in them" (a `documents` call followed by a `rag` call with `document_ids`).

`sample_lookups_unindexed` is the failure case: marking its fields listable ends in *Failed* naming the field that needs a payload index. What a real model got right and wrong with these questions is recorded in the plan of the spec (`docs/plans/007-metadata-lookups.md`, "As built (PR 10)").

`dotnet run --project tools/Quorum.SampleKnowledgeBase -- --schema flat` (or `nested`) prints the plain JSON Schema of the
payload, to paste in the registration.

## Embeddings without a model

`DeterministicEmbedding` turns a text into a 64-dimension vector with a hashed bag of words, normalised: texts that share words
point the same way, and the same text gives the same vector in every process. The vectors of the sample collections are made
with it by default, so tests can assert a real ranking with no model. To query these collections from a running Quorum, either
seed them with a real model as above, or use an embedding endpoint that returns the same vectors (the integration tests' fake
model server does); a real embedding model gives vectors of its own dimension and will not match the deterministic ones.
