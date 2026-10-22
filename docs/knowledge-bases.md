# Knowledge bases: a guide for data owners

Quorum answers from your documents through **knowledge bases**: Qdrant collections that already hold them. Quorum **only reads**. It does not extract, chunk, embed or write anything; whoever owns the data runs that pipeline and keeps the collection up to date. This page says what that collection has to look like so that an administrator can register it in a few minutes, filters work and answers can cite where they come from.

Design decisions are in [spec 003](specs/003-knowledge-bases.md); this is the practical summary.

## Checklist

Before handing a collection to the administrator, check that:

- Qdrant is **1.10 or later** (Quorum uses the query API) and reachable from the Quorum server on its **gRPC port, 6334**. The REST port, 6333, does not work for registration.
- The collection has **one dense vector** per point (unnamed, or one of several named vectors; the administrator picks the name). Sparse vectors, hybrid search and reranking are not used.
- Every point's payload has the **text of the chunk** in one field, and ideally a document id, a title, a position and a URL ([payload conventions](#payload-conventions)).
- The fields people will want to filter on are **scalar values or lists of texts** with a **payload index** ([payload indexes](#payload-indexes)).
- You can tell the administrator **which embedding model** (and which prefix, if the model needs one) made the vectors, and that model is served by the model endpoint of the Quorum installation ([embeddings](#same-embedding-model-and-prefix-as-the-ingestion)).
- Everything in the collection may be read by **every user of the assistants** that will use it ([access](#who-can-read-what)).
- Optionally, the **JSON Schema** of the payload ([the schema to hand over](#the-schema-to-hand-to-the-administrator)).

## Payload conventions

The administrator registers a collection by reviewing a **mapping** that Quorum drafts from a sample of 20 points (or from a pasted schema). The draft is filled by naming conventions: use these names and the draft is right the first time; use others and the administrator maps them by hand. Nothing is saved without their confirmation.

### What the model reads and cites

| Role | Meaning | Names recognised (case-insensitive, `-` and `_` ignored) |
|---|---|---|
| `text` (**required**) | The chunk the model reads | `text`, `chunk_text`, `content`, `chunk`, `body` |
| `documentId` | Stable id of the source document | `document_id`, `doc_id`, `source_id` |
| `title` | What the reader sees as the source name | `title`, `file_name`, `filename`, `document_name`, `name` |
| `position` | Page or section of the chunk | `page`, `page_number`, `position`, `section` |
| `sourceUrl` | Where the document can be opened | `source_url`, `url`, `uri`, `link` |

- Names are found at the root of the payload or inside any nested object.
- A point without a text (missing, or not a string) is skipped by the search and the answer says so.
- **Citations use these four references only.** The chat lists the sources under the answer, grouped by document, with the title, the position (`Page: 3`, `Section: Meals`) and a link. A document with no `title` is shown by its id; with neither, as "Source n". A link is made **only for an absolute `http` or `https` address**; a relative path, `file:` or anything else is shown as plain text. Give every chunk of one document the **same** id, title and URL, so they are grouped.
- The sources are stored with the message as references (knowledge base name, id, title, position, URL). The chunk text is never stored.
- **Chunk size**: a search returns at most the configured number of chunks (5 by default) within a **6,000-character** result cap by default (the administrator can raise it to 50,000). Whole chunks are dropped, never cut, so a chunk longer than the cap is never returned. Chunks of a few hundred to a thousand characters work well.

### Metadata fields (the ones models filter on)

Every other scalar or list-of-scalars property can become a **metadata field**: a typed field of the installation's dictionary that rag tools expose to the model as filter parameters.

| Type | Payload values | The model filters with |
|---|---|---|
| Text | strings | `<field>`: a list of values, matches any |
| Number | integers or decimals | `<field>_min`, `<field>_max` |
| Boolean | `true` / `false` | `<field>` |
| Date | **ISO 8601 strings**, `2024-01-15` or `2024-01-15T10:00:00Z` | `<field>_from`, `<field>_to` |
| List of texts | arrays of strings (tags) | `<field>`: a list of values, matches any |

- **Nested objects** use dot notation (`meta.year`); the dictionary name is the **last segment** (`year`). Objects inside lists and lists of lists are not mapped.
- **Dictionary names** are `[a-z][a-z0-9_]{0,39}`, may not be `query` or `knowledge_base`, and may not end in `_min`, `_max`, `_from` or `_to` (they would collide with the generated parameters). A payload property with such a name is flagged and the administrator renames it.
- **Dates must be strings**, not epoch numbers; a date stored as a number is a Number field.
- **Keep the type of a field stable.** A property that is a number in some points and a text in others is proposed as text with a warning. The same field in two collections with different types is allowed through clusters (`hr/year` and `finance/year`), but it is a decision for the administrator, not something to rely on.
- Optional fields are fine, but a sample of 20 points may miss them; the administrator can add a mapping by hand.

### Example payload

```json
{
  "text": "Employees accrue twenty-five days of paid annual leave per year.",
  "document_id": "leave",
  "title": "Annual leave policy",
  "page": 1,
  "source_url": "https://intranet.example.test/policies/leave",
  "department": "hr",
  "year": 2024,
  "published_on": "2024-01-15",
  "confidential": false
}
```

## The schema to hand to the administrator

If you can, give the administrator a plain **JSON Schema** of the payload. It saves them the sampling and it documents the intent, including fields that are empty in the first 20 points. No Quorum-specific keywords are needed: write it as you would for anyone.

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "title": "Policy chunk",
  "type": "object",
  "properties": {
    "text": { "type": "string" },
    "document_id": { "type": "string" },
    "title": { "type": "string" },
    "page": { "type": "integer" },
    "source_url": { "type": "string", "format": "uri" },
    "department": { "type": "string" },
    "year": { "type": "integer" },
    "published_on": { "type": "string", "format": "date" },
    "confidential": { "type": "boolean" }
  },
  "required": ["text"]
}
```

What Quorum reads from it:

- An **object** schema, draft 07 or 2020-12, up to **100 KB**, with `properties` written inline.
- `type`, nested `properties`, `items` of scalars, and `format: date` / `date-time` (which make a Date field).
- Everything else is ignored with a warning, and **`$ref` is not resolved**: inline the definitions.
- The schema is checked against the meta-schema of its draft first; an invalid one is refused with the reason.

## Payload indexes

A filter on a field **without a payload index** still works but Qdrant scans the payloads, which gets slow on large collections. **Create an index for every field you expect people to filter on.** Quorum never writes to your store, so it cannot do this for you: the inspection step lists the metadata fields it found without an index (`unindexedFields`) as a warning.

| Payload value | Index schema |
|---|---|
| text | `keyword` |
| list of texts | `keyword` |
| integer | `integer` |
| decimal | `float` |
| boolean | `bool` |
| ISO 8601 date string | `datetime` |

```bash
curl -X PUT "http://localhost:6333/collections/policies/index?wait=true" \
  -H "content-type: application/json" \
  -d '{ "field_name": "department", "field_schema": "keyword" }'
```

Nested fields use dot notation in `field_name` (`meta.year`). Date filters work on ISO strings with or without a `datetime` index; the index only makes them faster.

## Same embedding model and prefix as the ingestion

A question is turned into a vector with the model named in the knowledge base registration, on the **model endpoint of the Quorum installation** (an OpenAI-compatible `/embeddings`). For the ranking to mean anything the vector must live in the **same space** as the ones in the collection:

- Tell the administrator the **exact model name** as the endpoint serves it (for example `nomic-embed-text`).
- Tell them the **prefix** the model expects on queries. Many models want one on documents and a different one on questions (`search_document: ` while ingesting and `search_query: ` while searching for nomic, e5 and bge families). Quorum adds the query prefix; the document prefix is yours, at ingestion.
- Use the **same distance** the model was trained for. Quorum reads it from the collection and lets Qdrant apply it, including the score threshold.
- The registration **checks the dimension**: it embeds a probe text and compares its size with the vector of the collection. A mismatch is reported as a warning, the knowledge base is saved as *unavailable*, and searches on it would fail.
- **It cannot detect two different models with the same dimension.** The symptom is silent: answers come back with plausible scores and the wrong passages. After registering, run the administrator's **test search** (Knowledge → Test search) with a question whose answer you know and check that the right chunk comes first with a high score.

## Who can read what

Access control is **at the knowledge base level**. Anyone who can use an assistant that has the rag tool can retrieve **any chunk** of the knowledge bases that tool points to. There are no per-user or per-document permissions yet.

- Put in a collection only what every user of the assistants using it may read; use separate collections for separate audiences.
- The administrator can carve slices of one collection with **fixed filters** on a rag tool (for example `department = hr`). A fixed filter is combined with the model's filters, never visible to the model and never overridable. Metadata fields are therefore also the way to make one collection serve several tools.
- Retrieved text is handed to the model as **data**, inside per-request delimiters, with an instruction never to follow it. That limits prompt injection through documents but does not remove it: do not ingest content from untrusted authors into a collection that assistants with powerful tools read.

## Connecting and registering

The administrator registers the collection under **Knowledge** (guided: connection, inspection, mapping, who can use it, save), then creates a rag tool under **Tools** and activates it for an assistant. The address they need from you:

| Where Qdrant runs | Address to register |
|---|---|
| The `qdrant` profile of Quorum's compose file | `http://qdrant:6334` |
| On the host that runs Quorum's containers | `http://host.docker.internal:6334` |
| Another server | `http://<host>:6334`, with the API key if the server needs one |

Internal addresses are allowed. Cloud metadata and link-local addresses are blocked at registration and on every connection, redirects to another host are not followed, and the API key is stored encrypted and never shown again.

A registration that cannot be verified is still saved, marked **Unavailable**, with the reason. The usual ones:

| Message says | Cause |
|---|---|
| The server could not be reached over gRPC | Wrong host, firewall, or the REST port (6333) instead of 6334 |
| The server answered, but not as the Qdrant gRPC API does | The address is not Qdrant's gRPC port |
| The collection does not exist on that server | Wrong collection name, or ingestion has not run yet |
| The server refused the API key (or needs one) | Missing or wrong API key |
| The collection's vectors have N dimensions but the embedding model produces M | Different embedding model than the ingestion's, or the wrong named vector |
| The server runs Qdrant X; searching needs 1.10 or later | Qdrant too old for the query API |
| The address is not allowed | A link-local or cloud-metadata address |

A search that fails marks the knowledge base unavailable; the next successful search or verification marks it available again.

## Trying it with the sample project

`tools/Quorum.SampleKnowledgeBase` plays the data owner: it writes ten policy chunks into seven collections that cover the cases above (flat and nested payload, a list of tags, no payload indexes, two named vectors, a different vector size, an empty collection) and prints the matching JSON Schema. Nothing in the product references it. Details of each collection are in its [README](../tools/Quorum.SampleKnowledgeBase/README.md).

Start Qdrant next to Quorum, from the repository root:

```bash
docker compose --profile qdrant up -d qdrant
```

(`docker compose -f deploy/docker-compose.dev.yml up -d qdrant` works too.) Then seed it, in one of two ways.

**With a real embedding model**, to try Quorum end to end (a question in the chat, filters, sources). Use the model you will register the knowledge base with, on any OpenAI-compatible endpoint:

```bash
dotnet run --project tools/Quorum.SampleKnowledgeBase -- \
  --embedding-url http://localhost:11434/v1 \
  --embedding-model nomic-embed-text \
  --document-prefix "search_document: "
```

Register `sample_flat` as `http://qdrant:6334` (or `http://host.docker.internal:6334` when Qdrant is not in the compose network), embedding model `nomic-embed-text`, query prefix `search_query: `. The inspection proposes the mapping and the metadata fields `confidential`, `department`, `published_on` and `year`, with no warnings. The others show the cases: `sample_nested` dot notation (`meta.year`), `sample_tags` a list of texts, `sample_unindexed` the index warning on every field, `sample_named_vectors` the choice of the vector, `sample_other_vector_size` the dimension warning and `sample_empty` the advice to paste a schema. Add `--api-key <key>` for a Qdrant with a key, `--prefix <text>` to name the collections differently, and `--embedding-key <key>` for an endpoint that needs one.

**Without a model**, the default: vectors are a deterministic 64-dimension fake, enough to register every collection and to see each warning, but a real model cannot search them. The integration tests use this mode with a fake embedding endpoint that returns the same vectors.

```bash
dotnet run --project tools/Quorum.SampleKnowledgeBase
dotnet run --project tools/Quorum.SampleKnowledgeBase -- --schema flat   # prints the pasteable schema
```

Seeding again recreates the collections.
