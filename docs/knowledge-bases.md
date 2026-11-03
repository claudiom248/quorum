# Knowledge bases: a guide for data owners

Quorum answers from your documents through **knowledge bases**: Qdrant collections, or relations of a PostgreSQL database with pgvector, that already hold them. Quorum **only reads**. It does not extract, chunk, embed or write anything; whoever owns the data runs that pipeline and keeps the collection up to date. This page says what that collection has to look like so that an administrator can register it in a few minutes, filters work and answers can cite where they come from.

Design decisions are in [spec 003](specs/003-knowledge-bases.md) and, for PostgreSQL, [spec 008](specs/008-pgvector-knowledge-bases.md); this is the practical summary. The text is written for Qdrant; where your documents are in PostgreSQL, [the pgvector section](#pgvector-a-postgresql-relation) says what changes.

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
| `text` (**required**, unless the knowledge base has an entity name) | The chunk the model reads | `text`, `chunk_text`, `content`, `chunk`, `body` |
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

The model reads them through a **lookup tool**. Besides `values` (the stored list, answered at once) a tool that asks a knowledge base live gets two **live** operations with the filters of the dictionary, exact and limited to 15 seconds. The editor has one selector for the tool's targets: its lookups, and the **document** knowledge bases it can ask live. A knowledge base is a document one when its mapping has the `text` or `documentId` role; an entity knowledge base is offered only as its entity lookup, and an entity list and the knowledge base it comes from cannot both be in the same tool. The live operations are `documents` (which documents match, paginated) and `count`; each call reads one knowledge base, the one it names with `knowledge_base` when the tool has several. Live operations need the filter fields to be indexed too. The model can then pass the ids it got from `documents` to the `document_ids` parameter of a rag tool to search *inside* those documents.

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

## pgvector: a PostgreSQL relation

A knowledge base can also be **one relation** (a table, a view, a materialized view or a partitioned table) of a PostgreSQL database that has the [pgvector](https://github.com/pgvector/pgvector) extension ([spec 008](specs/008-pgvector-knowledge-bases.md)). The mapping, the dictionary, citations, filters, [embeddings](#same-embedding-model-and-prefix-as-the-ingestion), [lookups](#lists-and-counts-lookups) and [who can read what](#who-can-read-what) work as for Qdrant, with columns where this page says payload fields. This section is what differs. Every call Quorum makes is a `SELECT`, a catalog read or a `SET LOCAL`, inside a `READ ONLY` transaction with a statement timeout; there is no free SQL.

### Checklist

- The `vector` extension is installed **in the database you fill** (`CREATE EXTENSION IF NOT EXISTS vector;`) and the server is reachable from the Quorum server. pgvector **0.8 or later** is advised: older versions work, but a filtered search may return fewer rows than asked.
- The relation has one dense **`vector(n)` or `halfvec(n)`** column. Sparse vectors are not used. With several vector columns the administrator picks one.
- One knowledge base is one relation. For several tables, register several knowledge bases; to adapt a schema (rename columns, join a documents table, hide rows) create a **view**.
- The chunk text is in a text column and the fields people filter on are plain columns, or keys of a `json` / `jsonb` column ([shape of the relation](#shape-of-the-relation)).
- The filter columns, the document id and the vector have indexes ([indexes](#recommended-indexes)).
- The server user Quorum connects with is **read-only** ([a read-only role](#a-read-only-role)).
- You can tell the administrator the **embedding model**, its query prefix and the **distance** it was trained for ([choosing the distance](#choosing-the-distance)).

### Shape of the relation

The roles are recognised by the same names as for a payload (`text`, `content`, `document_id`, `title`, `page`, `source_url`, ...), found as column names or as keys inside a `json` / `jsonb` column, which are written in dot notation (`metadata.year`; the dictionary name is the last segment). A column or key **whose own name contains a dot** cannot be mapped and is reported. The mapping is drafted from a sample of **100 rows** (a relation has many columns, and a `jsonb` column may carry keys that appear in few rows), read without the vector column and with long values cut.

| Column type | Becomes |
|---|---|
| `text`, `varchar`, `char`, `uuid`, enumerations | Text |
| `smallint`, `integer`, `bigint`, `numeric`, `real`, `double precision` | Number |
| `boolean` | Boolean |
| `date`, `timestamp`, `timestamptz` (`timestamp` without time zone is read as UTC) | Date |
| `text[]`, `varchar[]` | List of texts |
| `json`, `jsonb` | Its keys, typed from the values sampled, as a payload is |

- The `text` role needs a text column. `documentId`, `title`, `position` and `sourceUrl` accept any scalar type and are read as text.
- Other types (`bytea`, geometric types, ranges, `interval`, arrays of other types, nested arrays, arrays of objects) are **not mapped** and are reported with a warning, never dropped silently.
- A `jsonb` key that has another JSON type in some rows is not an error: those rows do not match a filter on it.
- A column declared `vector` **without dimensions** is measured on the first row that has a vector; with no such row the verification says it cannot compare the dimensions.
- Rows with a null vector never match a search; a row whose text is null or blank is unusable and the answer says so.

```sql
CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE policy_chunks (
    id           bigserial PRIMARY KEY,
    text         text NOT NULL,
    document_id  text NOT NULL,
    title        text,
    page         integer,
    source_url   text,
    department   text,
    year         integer,
    published_on date,
    confidential boolean,
    embedding    vector(768) NOT NULL
);
```

### Recommended indexes

Quorum creates nothing, so the indexes are yours to create. A missing one never fails a registration or a search; it makes them slow and the verification says so as a warning.

| Index on | Why |
|---|---|
| The **vector column**, with the operator class of your [distance](#choosing-the-distance) | Without it the search is exact and slow on a large table. `CREATE INDEX ON policy_chunks USING hnsw (embedding vector_cosine_ops);` (or `ivfflat`) |
| Every column people **filter on** | B-tree for text, numbers, dates and booleans; GIN for `text[]` lists. Keys inside a `jsonb` column are not checked, so index them yourself (an expression index) when they matter |
| The column mapped to the **document id** | A search with `document_ids` is a condition on it. It is not cast, so its index is used |
| The columns that **lookups** read: listable fields, the counted point, the entity name | A lookup reads the whole relation to compute a list; see [lookups](#lookups-on-a-relation) |

An approximate index (HNSW, IVFFlat) applies the filter after reading the index, so a selective filter can return fewer rows than asked. With **pgvector 0.8 or later** Quorum turns on the iterative scan for every search (`hnsw.iterative_scan` and `ivfflat.iterative_scan`, `relaxed_order`), which keeps scanning until enough rows pass the filter. The rows are still returned best first.

### A read-only role

Quorum never writes, but give it a role that cannot, so that it stays true whatever the connection is used for. The verification warns when the user holds `INSERT`, `UPDATE`, `DELETE` or `TRUNCATE` on the relation.

```sql
CREATE ROLE quorum_reader LOGIN PASSWORD '...';
GRANT CONNECT ON DATABASE knowledge TO quorum_reader;
GRANT USAGE ON SCHEMA public TO quorum_reader;
GRANT SELECT ON policy_chunks TO quorum_reader;
```

The administrator is offered only the relations that have a `vector` or `halfvec` column and that this user can `SELECT` from (at most 200, with a text filter); a relation without the privilege is simply not listed.

### Choosing the distance

pgvector does not keep a metric in the column: it is the operator of the query and the operator class of the index. So the **distance is stored with the knowledge base** and the administrator confirms it. The draft is read from the operator class of an index on the vector column; with no index, or indexes that disagree, it proposes `Cosine` and warns.

| Distance | Operator | Operator class (`halfvec_` forms for `halfvec`) | The score is |
|---|---|---|---|
| `Cosine` | `<=>` | `vector_cosine_ops` | The similarity, 1 minus the distance: higher is better |
| `InnerProduct` | `<#>` | `vector_ip_ops` | The inner product: higher is better |
| `L2` | `<->` | `vector_l2_ops` | The distance: **lower** is better |

Use the distance the embedding model was trained for, the one your index was built with. The minimum score of a rag tool is applied in SQL on the same value (a **maximum** for `L2`). As ever, scores of different knowledge bases are never compared.

### Connecting

The administrator registers the relation in two ways, not both:

- **Separate fields**: host, port (5432 by default), database, user, password and SSL mode. Only the password is secret.
- **A connection string** in the Npgsql format. The **whole string** is secret: it is stored encrypted and never shown again, and editing it means entering it again.

Either way the knowledge base also holds the relation (`schema`, default `public`, and the table), the vector column and the distance. On an update an omitted password is kept only while host, port, database and user stay the same; otherwise Quorum asks for it again. As with Qdrant, Quorum does not filter addresses or connection parameters: you are responsible for what you enter.

| Where PostgreSQL runs | Address to register |
|---|---|
| The `pgvector` profile of Quorum's compose file | host `pgvector`, port `5432`, database `knowledge`, user `knowledge`, the password set as `PGVECTOR_PASSWORD` in `.env` |
| On the host that runs Quorum's containers | `host.docker.internal` and the port it publishes |
| Another server | `<host>`, with its own port, database and user |

The compose profile starts a PostgreSQL with pgvector that is **separate from the platform's database**, which is never used for knowledge bases:

```bash
docker compose --profile bundled-db --profile pgvector up --build -d
```

Set `PGVECTOR_PASSWORD` in `.env` first (the container refuses to start without it), then enable the extension once in the database you fill. Its port is published on loopback only (5433 by default, `PGVECTOR_PORT`), which is where ingestion from the host connects.

### What the verification checks

In order: the server answers; the `vector` extension is installed (and its version); the relation exists and can be read; the vector column exists and is a `vector` or `halfvec`; its dimensions equal those of an embedding of the configured model; the mapped columns and keys still exist with a compatible type. A failed check saves the knowledge base **Unavailable** with the cause and its consequence; it never blocks the registration.

These are **warnings that never change the status**:

- no index on the vector column (the search is exact and slow on a large table);
- a mapped metadata column with no index;
- the column mapped to the document id has no index, so a search with `document_ids` can be slow;
- a column that lookups read has no index ([lookups](#lookups-on-a-relation));
- pgvector older than 0.8 (filtered searches may return fewer rows);
- the user can write to the relation.

A failure is shown as one of a few outcomes: unreachable or timed out, unauthorized (credentials or privilege), relation or column not found, a vector of a different size than the model produces, or an unexpected error. The message never carries the connection string, the host, the SQL or the text PostgreSQL returned, because those can name tables and values. As with Qdrant, a search that fails marks the knowledge base unavailable and the next successful search or verification marks it available again.

### Lookups on a relation

The three kinds of [lookups](#lists-and-counts-lookups) work on a relation, with the same lookup tool and the same pages. What differs is the cost, because PostgreSQL has no facet API:

- **No index is required and no minimum pgvector version.** A `GROUP BY` or a count answers without an index, only slower, so a missing index is **never a failure** here; it is the advice `'x' (role) has no index: lookups read the whole relation to compute it`. Index the columns that lookups read if the relation is large.
- **Listable fields** are text, `varchar`, `uuid` or enumerated columns, `text[]` columns (each element counts as a value) and `jsonb` keys that hold a string or an array of strings. A `jsonb` key of another JSON type contributes nothing. Values are kept as they are, as in [values are stored as they are](#values-are-stored-as-they-are).
- **What a count counts.** With a **document id** mapped, a value counts the **distinct documents** that carry it, computed exactly by the database and not capped: the list is truncated only when there are more values than the ceiling. Without one, it counts rows. A **counted point** is a condition on a column or key (`is_first_chunk = true`, `chunk_index = 0`), and you still guarantee "exactly one per document".
- **An entity relation** (one row per supplier, team, person) names the **entity name** column and up to 10 attribute columns, as for a collection of entities. It is read in a stable order (the primary key, otherwise the first unique index without nulls, otherwise the physical address), so a cursor can resume; rows changed while a scan is in progress may be missed or seen twice.
- **Time.** A background computation may run for up to 10 minutes; a live operation is cancelled on the server after 15 seconds.

The listed values are stored in the platform database as described in [what Quorum stores about your data](#what-quorum-stores-about-your-data).

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

Register `sample_flat` as `http://qdrant:6334` (or `http://host.docker.internal:6334` when Qdrant is not in the compose network), embedding model `nomic-embed-text`, query prefix `search_query: `. The inspection proposes the mapping and the metadata fields `confidential`, `department`, `published_on` and `year`, with no warnings. The others show the cases: `sample_nested` dot notation (`meta.year`), `sample_tags` a list of texts, `sample_unindexed` the index warning on every field, `sample_named_vectors` the choice of the vector, `sample_other_vector_size` the dimension warning and `sample_empty` the advice to paste a schema. The lookup cases are `sample_company` (six hundred and twenty agreements, each one a family of framework, work orders, amendments, extensions and renewals: mark `supplier`, `department`, `nationality`, `audience`, `kind`, `contract_role` and `status` as listable, `is_first_chunk` as the counted point), `sample_vendors` (the 42 vendors of those agreements as entities: `name` as the entity name, `contract_count`, `rating` and `total_value_eur` as attributes), `sample_lookups` and `sample_lookups_unindexed` (the second one makes the computation fail and name the field) and `sample_suppliers`; the [README](../tools/Quorum.SampleKnowledgeBase/README.md) lists them all. Add `--api-key <key>` for a Qdrant with a key, `--prefix <text>` to name the collections differently, `--only <names>` to reseed just a few of them, and `--embedding-key <key>` for an endpoint that needs one.

**Without a model**, the default: vectors are a deterministic 64-dimension fake, enough to register every collection and to see each warning, but a real model cannot search them. The integration tests use this mode with a fake embedding endpoint that returns the same vectors.

```bash
dotnet run --project tools/Quorum.SampleKnowledgeBase
dotnet run --project tools/Quorum.SampleKnowledgeBase -- --schema flat   # prints the pasteable schema
```

Seeding again recreates the collections.

**For PostgreSQL**, the same tool writes sample **relations** (flat columns, a LangChain-style `jsonb` table, `text[]` tags, `halfvec`, two vector columns, a view, HNSW and IVFFlat with each distance, names that need quoting, and the handbook and employees used by the lookup scenarios) into a pgvector database of your own, never the platform's:

```bash
dotnet run --project tools/Quorum.SampleKnowledgeBase -- --pgvector "Host=localhost;Port=5433;Database=knowledge;Username=knowledge;Password=<PGVECTOR_PASSWORD>"
```

Add the same `--embedding-*` options to embed with a real model. The list of relations and the lookup walk-through are in the [README](../tools/Quorum.SampleKnowledgeBase/README.md).
