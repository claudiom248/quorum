# Spec 008: pgvector Knowledge Bases

**Status:** Approved (2026-10-22). Amendment 1 (ready for lookups, spec 007) approved 2026-10-22.
**Date:** 2026-10-22

## Problem
Spec 003 lets an admin register existing knowledge bases (KBs) and lets assistants query them through the `rag` tool, but it implements one storage kind only: Qdrant. Many companies already keep their embeddings in PostgreSQL with the **pgvector** extension, often written by their own ETL or by a RAG framework (LangChain, LlamaIndex), on a database server of their own. Those teams should be able to point Quorum at a table they already have, without moving the data and without running a second store.

This spec adds the `PgVector` kind: an admin registers a **table or view** of a PostgreSQL database that holds a `vector` or `halfvec` column and the chunk text and metadata beside it. As in spec 003, Quorum only **consumes** the knowledge base (003 D-1): it never writes, creates indexes or installs extensions.

Pinecone is the next kind and has its own spec (009). This spec also completes the per-kind abstraction that 003 D-2 promised and that the code does not have yet: today the registration, inspection, verification and search code talks to the Qdrant connector directly.

## Scope
### In
- A KB kind **`PgVector`**: connection (separate fields or a connection string), choice of a relation (table, view, materialized view or partitioned table) and of its vector column, distance, mapping by column and by key inside `json`/`jsonb` columns (D-1 to D-9).
- **Relation discovery**: list of the relations the connection can read that have a `vector` or `halfvec` column, and sampling of 100 rows to propose the mapping (D-5, D-10).
- **Verification** with warnings, including index, pgvector version and write-privilege checks (D-13).
- **Search**: nearest rows with SQL filters, native scores, iterative index scans, read-only transaction (D-11, D-12).
- The **per-kind abstraction** in the KnowledgeBases module, with Qdrant moved behind it with no change of behaviour (D-1).
- API changes for kind-specific connections, and the admin UI in separate PRs after the backend (D-15, D-17).
- Sample tables in the dev setup and integration tests on a pgvector container (D-18).
- An optional **`pgvector` profile** in the base compose file, a convenient target for data owners (D-20).
- Documentation for data owners.

### Out
- **Pinecone** (spec 009) and the other kinds already deferred: raw documents, external SQL/MongoDB sources.
- Writing anything to the database: ingestion, `CREATE EXTENSION`, `CREATE INDEX`, `ANALYZE`, schema changes (D-2).
- A free SQL query as the source of a KB: the source is a relation; a view is how a data owner adapts their schema (D-3).
- `sparsevec` and `bit` columns, L1 distance, hybrid search (full text plus vectors), reranking.
- Authentication that is not user and password (IAM tokens, Kerberos, client certificates managed from the UI); certificates can still be named in a connection string, which is the admin's responsibility (D-4).
- A separate embedding endpoint per KB, per-user or per-document permissions, background monitoring (all as in spec 003).

## Decisions
Decisions of spec 003 not listed here stand for this kind too: dictionary, mapping roles, scopes, rag tool, filters, citations, audit, status handling and test search.

| # | Decision |
|---|---|
| D-1 | **Per-kind abstraction.** The KnowledgeBases module gets one internal contract per kind with the operations the module needs: *inspect* (reachability, source exists, vectors with size and distance, indexes, version, a sample of rows as JSON), *list sources* (kinds that can), and *search* (a query vector, filter conditions, limit, score threshold and the paths to read, returning scored rows as JSON). Registration, inspection, verification and search depend on the contract, never on a concrete connector. Qdrant moves behind it with **no change of behaviour**: same routes, same responses, same codes and messages. Pinecone (spec 009) is the third implementation. The connector of each kind owns its failure mapping to a few outcomes with messages that are safe to show. |
| D-2 | **Quorum only reads.** Every call to the database is a `SELECT`, a catalog read, or a `SET LOCAL`, inside a **`READ ONLY` transaction** with a `statement_timeout`. Quorum never writes, never creates or alters anything and does not use the connection's session for anything else. |
| D-3 | **One KB is one relation**: `schema.name` of a table, view, materialized view or partitioned table, plus one vector column. A data owner with several tables registers several KBs. There is no free SQL. A view is the way to adapt a schema (rename columns, join a documents table, filter rows). Identifiers (schema, relation, columns) come only from names confirmed in the database catalog and are always quoted; values are always parameters. |
| D-4 | **Connection, two ways.** Either **separate fields** (host, port default 5432, database, username, password, SSL mode) or a **connection string** in Npgsql format; not both. Either way the KB also holds the relation (`schema` default `public`, `table`), the `vectorColumn` and the `distance`. Secrets are encrypted at rest with the master key: with fields only the **password** is secret and the rest is shown on edit; with a connection string the **whole string** is secret, stored encrypted and never returned (only `hasConnectionString`), and editing it means entering it again. Quorum does not restrict addresses and does not inspect or filter the parameters of the connection string: what an administrator enters is their responsibility (decision of 2026-10-22; the same relaxation for Qdrant and MCP is a separate fix). Connections are made with the official PostgreSQL driver. |
| D-5 | **Discovery lists relations.** After the connection is set, the admin sees the relations that have a `vector` or `halfvec` column and that the user can `SELECT` from, with schema, name, kind (table, view, materialized view, partitioned table), their vector columns (type, dimensions when the column fixes them) and an approximate row count for tables. At most 200 are shown, with a text filter. Relations without a vector column or without `SELECT` privilege are not listed. The admin picks one; there is no free field for the name. |
| D-6 | **Vector column.** `vector(n)` or `halfvec(n)`, dense only. When the relation has several, the admin chooses (the equivalent of the Qdrant vector name); saving without choosing is refused. The dimensions come from the column type; a column declared without dimensions (`vector`) is measured with `vector_dims` on the first non-null row of the sample; if there is none, the dimensions are unknown and verification says it cannot compare them. |
| D-7 | **Distance** is `Cosine`, `L2` or `InnerProduct` and is **stored with the KB**, because pgvector does not keep a metric in the column: it is the operator of the query (`<=>`, `<->`, `<#>`) and the operator class of the index. The draft is taken from the operator class of an index on the vector column (`vector_cosine_ops`, `vector_l2_ops`, `vector_ip_ops` and their `halfvec_` forms). With no index, or indexes with different operator classes, the draft proposes `Cosine` and warns. The admin confirms or changes it. |
| D-8 | **Score and threshold are the native value of the metric**, as with Qdrant: `Cosine` returns the similarity (1 − distance), `InnerProduct` the inner product, `L2` the distance (lower is better). The threshold of the rag tool is applied in SQL on that same value (a minimum for the first two, a maximum for `L2`). Rows are always ordered best first by the metric. Scores of different KBs are never compared (003 D-12). |
| D-9 | **Mapping.** The mapping model of 003 D-9 stays: roles and metadata fields bound to dictionary fields, each with a **path**. A path is a **column name** (`title`) or, for a key inside a `json`/`jsonb` column, the column name followed by the key path in dot notation (`metadata.year`), like Qdrant payload paths. A column or key whose name contains a dot cannot be mapped and is reported. Column types map to dictionary types: `text`, `varchar`, `char`, `uuid`, enumerations → text; `smallint`, `integer`, `bigint`, `numeric`, `real`, `double precision` → number; `boolean` → boolean; `date`, `timestamp`, `timestamptz` → date (`timestamp` without time zone is read as UTC); `text[]`, `varchar[]` → list of texts. Keys inside `json`/`jsonb` are typed from the values sampled, as the Qdrant payload is. The `text` role needs a text column; `documentId`, `title`, `position` and `sourceUrl` accept any scalar type and are read as text. Other types (`bytea`, geometric types, ranges, `interval`, arrays of other types, nested arrays, arrays of objects) are not mapped and are reported with a warning, never dropped silently. The naming conventions that fill the draft, the type compatibility rule and the dictionary rules (clusters, aliases, scopes) are those of spec 003 and its amendment 1. |
| D-10 | **Sampling reads 100 rows** (not 20 as with Qdrant, a deliberate choice: a relation has many columns and a `jsonb` column may carry keys that appear in few rows), in physical order, without the vector column, and with every long value cut in the sample (only the type matters). A pasted JSON Schema works as in 003 D-9, describing the row: columns as properties, `json`/`jsonb` columns as nested objects. |
| D-11 | **Search.** One parameterised query per KB: the mapped columns and the score, `WHERE` the filter conditions (always combined with AND) and the optional threshold, `ORDER BY` the distance operator on the vector column, `LIMIT` top K. The query vector is sent as a parameter and cast to the column's type. Filters translate by dictionary type: text → equality to any of the values; number and date → range; boolean → equality; list of texts → overlap (`&&` on arrays, "any of" on a `jsonb` array). Over `json`/`jsonb` keys the comparison is made on the value extracted **only when the key has the expected JSON type**: a row whose key has another type simply does not match, and the query never fails for it. Rows with a null vector never match. Text of a `text` role that is null or blank makes the row unusable (counted, 003 edge cases). |
| D-12 | **Iterative scan.** An approximate index (HNSW, IVFFlat) applies the filter after reading the index and can return fewer than K rows. For every search, in the read-only transaction, Quorum sets the `hnsw.iterative_scan` and `ivfflat.iterative_scan` parameters (pgvector 0.8 or later) so the scan continues until K rows pass the filter or the index limits are reached; the order of the returned rows is still by the metric. On an older pgvector Quorum searches without it and verification warns that filtered searches may return fewer rows than asked. |
| D-13 | **Verification** (003 D-8 for this kind). It checks, in order: the server answers; the `vector` extension is installed and its version; the relation exists and can be read; the vector column exists and is a `vector` or `halfvec`; its dimensions equal those of an embedding of the configured model; the mapped columns and keys still exist with a compatible type. It then adds **warnings that never change the status**: no index on the vector column (the search is exact and slow on large tables); a mapped metadata column with no index (filters on it can be slow; keys inside `jsonb` are not checked); pgvector older than 0.8; and the connection's user holding `INSERT`, `UPDATE`, `DELETE` or `TRUNCATE` on the relation (Quorum never writes, but a read-only role is advised). A failed check saves the KB `Unavailable` with a warning that names the cause and the consequence, and does not block registration. |
| D-14 | **Failures** map to a few outcomes with messages safe to show, as with Qdrant: unreachable or timeout, unauthorized (invalid credentials or no privilege), source not found (relation or column), rejected (vector of a different size), unexpected. A message never carries the connection string, the host, the SQL or the text PostgreSQL returned, because those can name tables and values. The status rules of 003 D-14 apply: a failed search marks the KB `Unavailable`, the next successful search or verification brings it back. |
| D-15 | **API.** `connection` is an object whose members depend on `kind`. For `PgVector`: `host`, `port?`, `database`, `username`, `password?`, `sslMode?` **or** `connectionString`, plus `schema?`, `table`, `vectorColumn?`, `distance`. In responses: the same without secrets, with `hasPassword` or `hasConnectionString`. Listing the relations is a new call (API Contract). The feature is not released, so there is no compatibility shim for the Qdrant shape; it is unchanged anyway. Stored, the connection stays one JSON object per KB, read and written whole. |
| D-16 | **Updates keep secrets under a rule.** On an update an omitted password keeps the stored one only while host, port, database and username are unchanged, otherwise `knowledge_base.password_required` (the same carry-over as the Qdrant API key). An omitted connection string keeps the stored one; switching between fields and a connection string requires the new secret. |
| D-17 | **UI** in separate PRs after the backend, as 003 D-19: the registration wizard becomes kind-aware (connection fields or string, list of relations, vector column and distance, mapping review by columns and keys, verify, test search). The Qdrant flow does not change. |
| D-18 | **Development and tests.** `tools/Quorum.SampleKnowledgeBase` also creates sample relations in a separate database of the dev compose's PostgreSQL (the image already ships pgvector), covering: flat columns; a LangChain-style table with `jsonb` metadata; `text[]` tags; a filtered column without index; two vector columns; `halfvec`; a column with unspecified dimensions; a different dimension; an empty table; a view; HNSW with cosine, IVFFlat with L2 and a table with no vector index; names with capitals and spaces; a column whose name has a dot; and a pasteable schema. Integration tests reuse the generator with the deterministic fake embeddings of spec 003, on a Testcontainers PostgreSQL with pgvector (same image as the dev compose). |
| D-19 | **Unchanged from spec 003:** permissions (`knowledge_base.manage`), scopes and the rule between scopes, tenant isolation (a tenant never sees the connection of another tenant's KB), audit of rag calls, logs with ids, counts, durations and codes only (never the connection string, the password, SQL text, queries or chunks), lifecycle (deleting a KB deletes the registration only), and the test search. |
| D-20 | **Compose profile.** The base `docker-compose.yml` gets an optional profile `pgvector` with a PostgreSQL that has the extension (`pgvector/pgvector` image, pinned like the Qdrant one), **separate from the platform's database** and not started by default; it is a convenient default target for data owners and Quorum only reads from it. Inside the compose network a KB registers it by service name. Its published port stays on loopback (as Qdrant's) and its credentials are set from `.env`, never fixed in the file. The profile combines with `bundled-db` and `qdrant`; the platform database is never reused for knowledge bases (003 D-6). The integration tests read the image version from the compose file, as they do for Qdrant, and the header comment of the file lists the profile. |

## Amendment 1: ready for lookups (spec 007)
**Status:** Approved (2026-10-22). Spec 007 (metadata lookups, approved 2026-10-22) builds on the per-kind contract of D-1: it adds an optional capability to it (007 D-15), a condition on the `documentId` role to the search (007 D-14) and new members to the mapping (007 D-2, D-3, D-4). This amendment makes the PgVector kind receive them and implements the part of 007 that applies to every kind. D-1 is changed as below; D-11 and D-13 gain the points below; everything else stands.

| # | Decision |
|---|---|
| A1-1 | **Contract by capabilities.** The contract of D-1 is a **required core** (inspect, search, and listing the sources for the kinds that can) plus **optional capabilities** that a kind declares and that consumers ask for, never assume. A consumer that needs a capability the KB's kind does not declare gets a clear "not supported" outcome, not an exception. The first optional capability is **lookups** (007 D-15: distinct values, list documents, count, scan); its operations are defined by spec 007, not here. PgVector declares **no** optional capability in this spec, so a PgVector KB reports lookups as not supported. |
| A1-2 | **Condition on `documentId`.** The search of D-11 accepts, besides the conditions on mapped fields, a **"document id is any of these"** condition (the `document_ids` of 007 D-14: strings, at most 50), always combined in AND with the others. It applies to the column or key mapped to the `documentId` role and follows **the type of that column**: text types → equality to any of the strings; integer types, `numeric` and `uuid` → the ids are converted to that type and compared with it; a `json`/`jsonb` key → compared as text. An id that cannot be converted to the column's type **matches nothing** and is not an error. The column is never cast to text, so an index on it can be used. A KB that has not mapped `documentId` is skipped by the caller (003 D-12), never searched without the condition. |
| A1-3 | **Verification warning.** D-13 adds one warning that never changes the status: the column mapped to `documentId` has no index, so a search with `document_ids` can be slow (007 D-14 asks for it). |
| A1-4 | **Mapping members of spec 007.** The mapping gains the roles `countedPoint` and `entityName` and the flag `listable` on a mapped field. For a PgVector KB they are **refused** while the kind has no lookups capability: a mapping that sets any of them returns 400 `knowledge_base.lookups_not_supported` (007 AC-3). They are additive in the stored mapping, so mappings saved before are unaffected. |
| A1-5 | **Order.** Spec 007 depends on the first step of this spec: Qdrant moved behind the contract with no change of behaviour (D-1, AC-1). The plan makes that its first PR and lands it before the lookups backend of spec 007; the PgVector connector can then proceed in parallel with it. |

**Acceptance criteria** (added to the list of this spec):
- **AC-39** Given a consumer that asks a KB's kind for the lookups capability, When the kind does not declare it (PgVector), Then it gets a "not supported" outcome and nothing is sent to the database.
- **AC-40** Given a rag tool with `document_ids` over a PgVector KB whose `documentId` is a text column, an integer column, a `uuid` column or a `jsonb` key, When it is called, Then only rows of those documents are searched, in AND with the filters, and the comparison uses the column's own type.
- **AC-41** Given an id that cannot be converted to the type of the `documentId` column (for example `abc` for an integer column), When searching, Then it matches nothing and the search succeeds; given a KB without `documentId` mapped, Then it is skipped and named.
- **AC-42** Given a mapping of a PgVector KB that marks a field `listable` or sets `countedPoint` or `entityName`, When saving, Then it returns 400 `knowledge_base.lookups_not_supported`; given a mapping without them, Then it saves as before.
- **AC-43** Given a `documentId` column with no index, When verifying, Then a warning says so and the status is unchanged.

## Domain Model
Only the KB's kind-specific settings change; every other entity of spec 003 stands. The platform database gets no new table.

| Entity | Change |
|---|---|
| **KnowledgeBase** | `Kind` gains `PgVector`. `Connection` is the kind-specific JSON of D-15. For `PgVector`: `Host`, `Port`, `Database`, `Username`, `SslMode`, `ProtectedPassword` **or** `ProtectedConnectionString`, `Schema`, `Table`, `VectorColumn`, `Distance`. Dimensions are never stored: they are measured at verification and at search. |
| **Mapping** | Unchanged in shape: roles and fields carry **paths**, which for `PgVector` are columns or `column.key` as in D-9. |
| **Connection settings** | A contract per kind (D-1) replaces the Qdrant-only type in the entity; Qdrant's stored members stay as they are, so existing rows need no data migration. How the column is mapped in EF Core is a decision of the plan. |

## API Contract
Routes, permissions and error format as in spec 003 (`knowledge_base.manage`, ProblemDetails with a stable `code`). Only what changes is listed.

| Verb | Route | Change |
|---|---|---|
| POST | `/knowledge-bases/relations` | **New.** Request `{ kind, connection }` (connection without `table`). Response `{ reachable, relations: [{ schema, name, type, approximateRows?, vectorColumns: [{ name, type, dimensions? }] }], truncated, warnings[] }`. Only for kinds that can list their sources (`PgVector`); another kind returns 400 `knowledge_base.unsupported_operation`. Errors: 400 `knowledge_base.invalid_connection`; 401; 403. |
| POST | `/knowledge-bases/inspect` | `connection` per kind. For `PgVector` the response fills `vectors` with one entry per vector column (`name` = column, `size`, `distance` = the draft of D-7), `collectionExists` reads "the collection or relation exists", `unindexedFields` lists mapped-candidate columns with no index, and the draft mapping uses column and `column.key` paths. New warning codes are prefixed `pgvector_`. |
| POST / PUT | `/knowledge-bases`, `/knowledge-bases/{id}` | `connection` per kind. New errors: 400 `knowledge_base.vector_column_required` (several vector columns, none chosen), 400 `knowledge_base.password_required` (D-16). `knowledge_base.unsupported_kind` lists the supported kinds. |
| GET | `/knowledge-bases/{id}` | `connection` per kind, without secrets: `hasPassword` or `hasConnectionString`. |
| POST | `/knowledge-bases/{id}/verify`, `/search` | Unchanged in shape; `PgVector` verification adds the warnings of D-13. |

Dimension mismatch, unreachable store, unauthorized, source not found and timeouts reach the model and the admin through the same search and verification results as with Qdrant, with `pgvector_*` codes.

## Authorization
Unchanged from spec 003: `knowledge_base.manage` for every `/knowledge-bases` route, including the new relations call; rag tools use `tools.manage`, `tools.attach` and `tools.use`; a tenant never sees the connection, relation or secrets of another tenant's KB. Row-level security configured in the external database applies to the user Quorum connects with and is not worked around. Content access remains at KB level (003 D-16).

## Edge Cases & Failure Modes
- **Extension missing** or relation not readable by the user: the KB is saved `Unavailable` with a warning (003 D-8); the relation does not appear in the list.
- **Dimension mismatch** between the vector column and the embedding model: warning at registration and verification; a search fails with a safe error and the KB becomes `Unavailable`. An unspecified-dimension column with mixed sizes makes the query fail the same way.
- **Several vector columns, none chosen**: refused at save with `knowledge_base.vector_column_required`.
- **Filter over a `jsonb` key** whose value has another JSON type in some rows (a number stored as a string, an object): those rows do not match; the search succeeds (D-11).
- **Filter on a column or key the KB did not map**, **partial failure** among several KBs, **no result above the threshold**, **result cap**, **invalid filter values**, **unusable chunks**: as in spec 003.
- **Selective filter over an approximate index** returning fewer than K rows: reduced by iterative scan (D-12); on older pgvector the verification warning is the only mitigation.
- **Relation dropped, column renamed or retyped** after registration: the search fails with "source not found" or "rejected"; verification reports which mapped column no longer fits.
- **Names with capitals, spaces or quotes**, or reserved words: always quoted identifiers from the catalog (D-3). A name with a dot is reported as not mappable.
- **Partitioned tables** work as tables; **foreign tables** are not listed.
- **Values with quotes or SQL**: always parameters, never text in the statement.
- **Read replicas and poolers**: `SET LOCAL` inside a transaction works on a standby and behind a transaction-mode pooler; Quorum uses no server-side prepared statements, so it does not depend on session state.
- **Huge rows**: only the mapped columns are read, never `SELECT *`, and the vector column is not returned.
- **Connection string with unusual parameters** (several hosts, files as certificates, options): passed to the driver as written (D-4); a string that does not parse is `knowledge_base.invalid_connection`.
- **Credentials**: a wrong password after a rotation is an `Unauthorized` outcome; editing the KB with the new password fixes it.
- **Concurrency**: connections are short-lived and pooled with a small maximum per KB; an edit of the connection takes effect on the next call.

## Non-Functional Requirements
- No performance targets (as specs 001 to 004): search latency (embedding plus query) is bounded by the 15 s limit of 003 D-13, and inspection and verification by the 10 s connection limit. A statement timeout inside the transaction keeps a slow query from outliving the limit.
- Sampling is bounded in rows (100) and in bytes (long values cut), so a wide table cannot flood the review screen or the memory.
- Connection pooling never grows without bound: a small pool per registered KB, idle connections evicted, no pool left behind by a deleted or edited KB.
- No outbound telemetry; the only added outbound traffic goes to the PostgreSQL servers the admin registers.
- Logs carry ids, counts, durations and outcome codes only (003 D-21).
- Libraries must be permissively licensed: the PostgreSQL driver (Npgsql) and pgvector are under the PostgreSQL license. Whether the pgvector helper package for the driver is worth using, or the query vector is passed as an array and cast, is decided in the plan (CLAUDE.md: established libraries, own code only at the edge). `Microsoft.Extensions.VectorData` is not used for the reason given in spec 003.

## Integrations
- **PostgreSQL with pgvector** (the customer's, separate from the platform database), through the official driver. Catalog reads (`pg_catalog`) for discovery, indexes, types, version and privileges.
- **Installation model endpoint** for query embeddings (`IEmbeddings`), unchanged.
- **Rag tool** (Agents): unchanged; it can mix Qdrant and PgVector KBs.
- **Dev compose** PostgreSQL (pgvector image) for the sample relations.
- No events published.

## Acceptance Criteria
**Abstraction**
- **AC-1** Given the Qdrant behaviour of spec 003, When the per-kind abstraction is in place, Then every route returns the same responses, codes and messages as before and the existing Qdrant tests pass with unchanged expectations.
- **AC-2** Given kind `PgVector`, When inspecting or registering, Then it is accepted; given an unknown kind, Then it returns 400 `knowledge_base.unsupported_kind` naming the supported kinds.

**Connection and secrets**
- **AC-3** Given host, database, username and password, When inspecting, Then the database is reached with them; given a connection string, Then with it; given both or neither, Then 400 `knowledge_base.invalid_connection`.
- **AC-4** Given a registered `PgVector` KB, When reading it through the API, Then neither the password nor the connection string is returned, only `hasPassword` or `hasConnectionString`; and neither appears in logs, audit entries or error messages.
- **AC-5** Given an update without password and with the same host, port, database and username, When saving, Then the stored password is kept; given a changed host, port, database or username, Then 400 `knowledge_base.password_required`; given an update without connection string, Then the stored one is kept; given a switch between fields and a connection string, Then the new secret is required.
- **AC-6** Given an address that the Qdrant and MCP policy would have blocked, When inspecting or registering a `PgVector` KB, Then no address policy refuses it.

**Discovery and mapping**
- **AC-7** Given a reachable database, When listing relations, Then every table, view, materialized view and partitioned table with a `vector` or `halfvec` column that the user can read is listed with schema, name, kind, vector columns and approximate rows; relations without a vector column or without `SELECT` are not; at most 200 are returned, with a text filter and `truncated` set when there are more.
- **AC-8** Given a relation with several vector columns and none chosen, When saving, Then it returns 400 `knowledge_base.vector_column_required`; inspecting warns.
- **AC-9** Given a relation, When inspecting without a schema, Then at most 100 rows are sampled, the vector column is not read, and a draft mapping, proposed dictionary fields and the candidate columns without index are returned.
- **AC-10** Given columns `chunk_text`, `doc_id`, `file_name`, `page`, `source_url` and a `jsonb` column `metadata` with a key `year`, When the draft is built, Then the roles are `text`, `documentId`, `title`, `position`, `sourceUrl`, and `metadata.year` is a metadata field named `year`.
- **AC-11** Given columns of types text, integer, numeric, boolean, date, timestamptz and `text[]`, When the draft is built, Then their dictionary types are text, number, number, boolean, date, date and list of texts; a `jsonb` key is typed from its sampled values.
- **AC-12** Given columns of types that cannot be mapped (`bytea`, ranges, `interval`, arrays of other types) or a name containing a dot, When the draft is built, Then each is reported with a warning and none is dropped silently.
- **AC-13** Given a pasted JSON Schema describing the row, When inspecting, Then the draft is built from it by the same conventions; invalid, non-object or over 100 KB gives 400 `knowledge_base.invalid_schema`.
- **AC-14** Given a vector column declared without dimensions and a sample with a vector, When inspecting, Then the dimensions are measured and compared with the embedding's; given no vector in the sample, Then the dimensions are reported unknown.
- **AC-15** Given an index on the vector column with a cosine, L2 or inner product operator class (vector or halfvec), When the draft is built, Then the distance is the matching one; given no index or indexes with different classes, Then `Cosine` is proposed with a warning; the admin's choice is saved with the KB.

**Registration and verification**
- **AC-16** Given a reachable database with the extension, a readable relation and matching dimensions, When registering, Then the KB is `Available` and no warning is returned.
- **AC-17** Given an unreachable server, wrong credentials, a missing extension, relation or column, or a dimension mismatch, When registering, Then the KB is saved `Unavailable` with a warning that names the cause and the consequence for RAG.
- **AC-18** Given a vector column with no index, a mapped metadata column with no index, or pgvector older than 0.8, When verifying, Then each produces a warning and the status stays `Available`.
- **AC-19** Given a user that can write to the relation, When verifying, Then a warning recommends a read-only role and the status is unchanged.
- **AC-20** Given a mapped column dropped or retyped after registration, When verifying, Then the problem is reported by name of column and the KB becomes `Unavailable` if the `text` role is affected.

**Search**
- **AC-21** Given an activated rag tool over a `PgVector` KB, When the model calls it with a query, Then the query is embedded with the KB's model and prefix, the relation is searched with the KB's distance, and the model receives numbered sources, best first.
- **AC-22** Given the three distances, When searching, Then the score is the cosine similarity, the inner product or the L2 distance, and a score threshold keeps only rows on the right side of it for that metric.
- **AC-23** Given filters on a text, a number range, a date range, a boolean and a list, over plain columns and over `jsonb` keys, When the model calls the tool, Then only matching rows come back; a fixed filter still applies against a conflicting model value (003 AC-25, AC-26).
- **AC-24** Given a `jsonb` key that holds another JSON type in some rows, When filtering on it, Then the search succeeds and those rows are not returned.
- **AC-25** Given values with quotes or SQL, and a relation or columns with capitals, spaces or reserved words, When searching, Then the values are treated as data and the identifiers work.
- **AC-26** Given an HNSW or IVFFlat index and a selective filter on pgvector 0.8 or later, When searching, Then up to top K matching rows are returned and ordered by the metric.
- **AC-27** Given any search, Then it runs in a read-only transaction with a statement timeout and issues no statement that writes.
- **AC-28** Given an unreachable server, wrong credentials, a dropped relation, a dimension mismatch or a query over 15 s, When the model calls the tool, Then it receives an error result without the connection string, host, SQL or database messages; the KB becomes `Unavailable`, and a later successful search or verification makes it `Available`.
- **AC-29** Given a rag tool with a Qdrant KB and a `PgVector` KB, When called without `knowledge_base`, Then the results are interleaved, each source names its KB, and scores are not compared; one failing KB gives the others' results with a note (003 AC-27, AC-34).
- **AC-30** Given rows whose `text` is null or blank, When searching, Then they are skipped and counted, and if all are unusable the result says so.

**Authorization, tenants and logs**
- **AC-31** Given a user without `knowledge_base.manage`, When calling the relations route or any other `/knowledge-bases` route, Then it returns 403; unauthenticated returns 401.
- **AC-32** Given a Global `PgVector` KB, When another tenant reads it, Then it sees name, description and status only, never the host, relation or secrets.
- **AC-33** Given any inspection, listing, registration, verification or search, When the logs are inspected, Then no connection string, password, SQL text, query, chunk text or document name appears.

**UI**
- **AC-34** Given the admin pages, When registering a `PgVector` KB, Then everything can be done without touching files: connection (fields or string), choosing a relation from the list, the vector column and the distance, reviewing the mapping, verifying and running a test search.
- **AC-35** Given the registration of a Qdrant KB, Then the flow and its results are the same as before.

**Development support and documentation**
- **AC-36** Given the dev compose, When running the sample project, Then the sample relations of D-18 exist and each case can be registered and queried; the integration tests use the same generator with deterministic embeddings on a pgvector container.
- **AC-37** Given the documentation, Then the guide for data owners has a section for pgvector (shape of the relation, recommended indexes, a read-only role, how to choose the distance and what Quorum reads), `docs/deferred.md` and `docs/database-schema.md` (secret members of the connection) are up to date.

**Compose**
- **AC-38** Given the base compose file, When started with `--profile pgvector`, Then a separate PostgreSQL with the extension runs beside Quorum, a KB can be registered against it by service name, and without the profile nothing of it starts; the platform database is untouched.

## Deferred Decisions
| Decision | Fallback chosen now | Revisit trigger |
|---|---|---|
| Authentication other than user and password (cloud IAM tokens, Kerberos), certificates managed from the UI | A connection string; files named in it are the admin's responsibility (D-4) | A customer on a managed service that requires it |
| `sparsevec`, `bit`, L1 distance, hybrid search with full text | Dense `vector` and `halfvec` only; three distances | Poor retrieval quality on exact terms or codes |
| A free SQL query as the source | A relation; views adapt the schema (D-3) | Data owners that cannot create a view |
| Foreign tables and other relation kinds in the list | Not listed (a view over them works) | A concrete need |
| Tuning the index search (`ef_search`, `probes`, scan limits) per KB | Server defaults plus iterative scan (D-12) | Recall or latency problems on large tables |
| Writing a sampling strategy better than physical order (random sample) | First 100 rows (D-10) | Tables ordered so that the first rows are not representative |
| Lookups (spec 007) for PgVector: distinct values with counts (a `GROUP BY`), list documents, count, scan | Not supported; the mapping members of 007 are refused (A1-4) | A customer on PgVector that needs lists and counts; a decision of its own amendment |

## Open Questions
_None._ The one question raised while drafting (an optional pgvector profile in the base compose file) was answered in review on 2026-10-22: yes, in this spec (D-20, AC-38).
