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

`dotnet run --project tools/Quorum.SampleKnowledgeBase -- --schema flat` (or `nested`) prints the plain JSON Schema of the
payload, to paste in the registration.

## Embeddings without a model

`DeterministicEmbedding` turns a text into a 64-dimension vector with a hashed bag of words, normalised: texts that share words
point the same way, and the same text gives the same vector in every process. The vectors of the sample collections are made
with it, so tests can assert a real ranking with no model. To query these collections from a running Quorum, use an embedding
endpoint that returns the same vectors (the integration tests' fake model server does); a real embedding model gives vectors
of its own dimension and will not match them.
