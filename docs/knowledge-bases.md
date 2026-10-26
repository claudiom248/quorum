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
- If assistants should **list values or count** ("list the suppliers", "which teams are Italian"): indexed listable fields, a counted point or a document id, Qdrant 1.12 or later, and optionally a collection of entities ([lookups](#lists-and-counts-lookups)).

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
- **It cannot detect two different models with the same dimension.** The symptom is silent: answers come back with plausible scores and the wrong passages. After registering, run the administrator's **test search** (Knowledge → Knowledge bases → open the knowledge base → Test search) with a question whose answer you know and check that the right chunk comes first with a high score.

## Lists and counts: lookups

A semantic search returns a handful of chunks, so it cannot answer "list the suppliers", "which suppliers have the most contracts" or "which teams are Italian": the model would present a sample as the whole list. **Lookups** answer those questions from the metadata you already keep ([spec 007](specs/007-metadata-lookups.md)). Quorum only reads, as before; what changes is that it also **keeps a copy of the values** it lists (see [what is stored](#what-quorum-stores-about-your-data)).

There are three kinds, and the administrator picks them while registering the collection (the step after the mapping).

| Kind | What it lists | You provide |
|---|---|---|
| **Computed** | The distinct values of one **listable** field, with the number of documents for each | A text or list-of-texts field, indexed |
| **Entity** | The points of a collection whose points are *entities* (one per supplier, department, team), with up to 10 attributes | A collection of entities, with a name field |
| **Manual** | Values the administrator types, pastes or imports from a CSV, with labels and synonyms | Nothing |

The model reads them through a **lookup tool**. Besides `values` (the stored list, answered at once) the tool can run two **live** operations with the filters of the dictionary, exact and limited to 15 seconds: `documents` (which documents match, paginated) and `count`. Live operations need the filter fields to be indexed too. The model can then pass the ids it got from `documents` to the `document_ids` parameter of a rag tool to search *inside* those documents.

### Which fields to index

Computing a list uses Qdrant's facet API, which **refuses a field without a payload index** (and needs Qdrant **1.12 or later**; older servers still answer `documents` and `count`). A lookup on an unindexed field is saved as *Failed* and the message names the field. Create an index for:

- every **listable** field (`keyword` for text and lists of texts);
- the **counted point** field, if you use one ([below](#the-counted-point-what-a-count-counts));
- every field that a lookup tool uses as a **fixed filter** (a slice of the list is computed for those filters) or exposes as a filter to the model;
- the **document id**, if rag tools should accept `document_ids`.

A live filter on an unindexed field is allowed but can take 12 to 15 seconds on millions of points; the call then ends and tells the model to try a narrower filter. The tool editor warns about such fields.

### The counted point: what a count counts

The "number of documents" next to a value only means something if Quorum knows how to count documents rather than chunks. It takes the first of these that applies:

1. **A counted point**: a payload field that is `true` (for example `is_first_chunk`) or equal to a value (for example `chunk_index = 0`) on **exactly one point of each document**. Quorum counts only those points. This is the cheapest and the most exact, and **you guarantee** the "exactly one": a document with two such points counts twice, and one with none is missing. The draft mapping proposes `is_first_chunk`, `first_chunk` and `is_primary` as `true`, and `chunk_index` equal to `0`; the administrator confirms.
2. **The document id**, when there is no counted point: Quorum counts the distinct ids by scanning the payloads. This is heavy (up to 5 million points, minutes) and runs only in the background; past the cap the list is marked *truncated*.
3. **Neither**: Quorum counts **chunks** and says so (`unit: chunks`), so the model does not report chunk counts as contract counts.

Counts of different knowledge bases are never added.

### Values are stored as they are

- **No normalisation.** `IT`, `Italy` and `italiana` are three values. Normalise at ingestion; use a *manual* lookup when you need synonyms (`IT (Italy, Italia, italiana)`).
- Only **text** and **list of texts** fields can be listable. A value is at most 200 characters (longer ones are skipped and counted), empty values are ignored and a document counts once per distinct value.
- A list holds at most **5,000 values** by default (`Quorum:Lookups:MaxValuesPerLookup`, up to 50,000). Beyond it the values with the most documents are kept and the list is marked *truncated*; the model is told not to present it as complete. Fields with thousands of distinct values (ids, free text) are not good lookups.
- Lists are recomputed in the background every **24 hours** (1 hour to 7 days per knowledge base), when the mapping changes, and on **Refresh now**. A failed computation keeps the last good list and marks it *stale*. The data your ETL writes therefore reaches the model within the interval, not at once.

### A collection of entities

When the questions are about *things* (suppliers, teams, policies) rather than the chunks of their documents, keep **one point per entity** in a collection of its own and register it as an entity lookup:

- the payload has the entity's **name** in one field (the entity name) and up to **10 attributes** the model should see, each mapped as a metadata field;
- put precomputed figures in attributes (`contract_count`, `supplier_since`, `rating`): they are what "the suppliers with the most contracts" is sorted on. A number or date attribute can be used to sort;
- the list is read with a scan, so it needs **no payload index**, up to the ceiling of values;
- one entity lookup per knowledge base.

Prefer this to a computed lookup when the entity has attributes beyond its name, when the same value is spelled differently across chunks, or when counts must come from your own system of record. Keep the attributes in step with the documents: Quorum reads both and does not reconcile them.

### What Quorum stores about your data

Until lookups, the platform database held only the registration. Now it also holds, for each lookup, **the listed values, their counts and the chosen entity attributes**, and never chunk text, titles, urls or document ids. These values are **your data in the platform database** and in its backups: mark as listable only fields whose values every user of the assistants may see (the rule of [who can read what](#who-can-read-what) applies to them), and expect deletion to follow the knowledge base (they go with it). The tables are described in [database-schema.md](database-schema.md).

### When a lookup fails

| Status says | Cause |
|---|---|
| *Failed*, naming a field | The field has no payload index |
| *Failed*, "needs Qdrant 1.12" | The server is older than 1.12 |
| *Stale* with an error | The last refresh failed; the previous list is still served |
| "being prepared" | Registered or changed minutes ago; the first computation has not ended |
| *Truncated* | More distinct values than the ceiling, or the scan stopped at its cap |

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

Quorum does not filter addresses: you and your colleagues are responsible for what you enter, so register only addresses you trust. Redirects to another host are not followed, and the API key is stored encrypted and never shown again.

A registration that cannot be verified is still saved, marked **Unavailable**, with the reason. The usual ones:

| Message says | Cause |
|---|---|
| The server could not be reached over gRPC | Wrong host, firewall, or the REST port (6333) instead of 6334 |
| The server answered, but not as the Qdrant gRPC API does | The address is not Qdrant's gRPC port |
| The collection does not exist on that server | Wrong collection name, or ingestion has not run yet |
| The server refused the API key (or needs one) | Missing or wrong API key |
| The collection's vectors have N dimensions but the embedding model produces M | Different embedding model than the ingestion's, or the wrong named vector |
| The server runs Qdrant X; searching needs 1.10 or later | Qdrant too old for the query API |

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

Register `sample_flat` as `http://qdrant:6334` (or `http://host.docker.internal:6334` when Qdrant is not in the compose network), embedding model `nomic-embed-text`, query prefix `search_query: `. The inspection proposes the mapping and the metadata fields `confidential`, `department`, `published_on` and `year`, with no warnings. The others show the cases: `sample_nested` dot notation (`meta.year`), `sample_tags` a list of texts, `sample_unindexed` the index warning on every field, `sample_named_vectors` the choice of the vector, `sample_other_vector_size` the dimension warning and `sample_empty` the advice to paste a schema. The lookup cases are `sample_company` (about forty contracts: mark `supplier`, `department`, `nationality` and `audience` as listable, `is_first_chunk` as the counted point), `sample_vendors` (the same suppliers as entities: `name` as the entity name, `contract_count` and `rating` as attributes), `sample_lookups` and `sample_lookups_unindexed` (the second one makes the computation fail and name the field) and `sample_suppliers`; the [README](../tools/Quorum.SampleKnowledgeBase/README.md) lists them all. Add `--api-key <key>` for a Qdrant with a key, `--prefix <text>` to name the collections differently, and `--embedding-key <key>` for an endpoint that needs one.

**Without a model**, the default: vectors are a deterministic 64-dimension fake, enough to register every collection and to see each warning, but a real model cannot search them. The integration tests use this mode with a fake embedding endpoint that returns the same vectors.

```bash
dotnet run --project tools/Quorum.SampleKnowledgeBase
dotnet run --project tools/Quorum.SampleKnowledgeBase -- --schema flat   # prints the pasteable schema
```

Seeding again recreates the collections.
