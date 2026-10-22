# Spec 009: Pinecone Knowledge Bases

**Status:** Approved (2026-10-22). Amendment 1 (ready for lookups, spec 007) approved 2026-10-22.
**Date:** 2026-10-22

## Problem
Spec 003 lets an admin register existing knowledge bases (KBs) and query them through the `rag` tool, with Qdrant as the only storage kind. Spec 008 adds PostgreSQL with pgvector and completes the per-kind abstraction. Many companies keep their embeddings in **Pinecone**, a managed vector database, populated by their own pipelines. Those teams should be able to register an index they already have, without moving the data.

This spec adds the `Pinecone` kind: an admin registers a **namespace of a vector index** of a Pinecone project. As in spec 003, Quorum only **consumes** the knowledge base (003 D-1): it never writes records, creates indexes or changes anything in the project.

It is the third implementation of the per-kind abstraction of spec 008 (D-1) and **depends on it**: the abstraction must exist before the Pinecone connector is written.

## Scope
### In
- A KB kind **`Pinecone`**: API key, index, namespace and optional endpoint overrides (D-1 to D-4).
- **Discovery**: list of the project's indexes and of an index's namespaces, and sampling of records to propose the mapping (D-5, D-8).
- **Mapping** by metadata key, with **dates stored as Unix timestamps** (D-7).
- **Verification** with warnings (D-11), **search** with metadata filters and a threshold applied by Quorum (D-9, D-10).
- A thin **REST client** of our own at the edge, with the API version pinned (D-12).
- API changes for the new kind, and the admin UI in separate PRs after the backend (D-13, D-14).
- Development support and tests (D-15) and documentation for data owners.

### Out
- **Document indexes** (the typed-schema indexes of Pinecone's Documents API, created since 2026) and everything that needs them: full-text search, `score_by`, typed fields. Recorded in `docs/deferred.md`.
- **Sparse and hybrid search**, integrated search by text (Pinecone embeds the query), Pinecone's hosted reranking, `scanFactor` and `maxCandidates` tuning.
- Writing anything: upsert, update, delete, creating indexes or namespaces, importing data.
- Using the record **id** as the source of a role (document id, title); only metadata keys can be mapped.
- Other kinds already deferred (raw documents, external SQL/MongoDB), a separate embedding endpoint per KB, per-user or per-document permissions, background monitoring (as in spec 003).

## Decisions
Decisions of spec 003 and of spec 008 not listed here stand for this kind too: dictionary, mapping roles, scopes, rag tool, filters, citations, audit, status handling, test search, the per-kind contract (008 D-1) and the absence of any address policy.

| # | Decision |
|---|---|
| D-1 | **One KB is one namespace of one vector index.** Pinecone queries a single namespace at a time, so a data owner with several namespaces registers several KBs (the default namespace, whose name is the empty string, is a namespace like the others). Only **dense vector indexes** are supported: a sparse index, or an index that Pinecone describes as holding documents with a schema, is listed with the reason and cannot be registered. Quorum calls the **Vectors API** only (D-12). |
| D-2 | **Connection.** The KB holds: the **API key** (secret), the **index name**, the **namespace** (default `""`), and two optional advanced fields: the **control plane URL** (default `https://api.pinecone.io`) and the **index host** (default: the `host` that the control plane returns for the index). The advanced fields are for Pinecone's own private endpoints and BYOC deployments and for emulators; either may be `http` or `https`. The control plane is always needed: it gives the index's dimension, metric, type and host. The API key is encrypted at rest with the master key and never returned (only `hasApiKey`). Quorum applies no address policy and does not filter the URLs (decision of 2026-10-22, 008 D-4); it does **not follow redirects**, so the key is never sent to a host the admin did not enter. |
| D-3 | **Update rule for the key.** On an update an omitted API key keeps the stored one only while the control plane URL, the index host and the index name are unchanged, otherwise 400 `knowledge_base.api_key_required` (the same carry-over rule as the Qdrant key). |
| D-4 | **Read only.** Quorum calls `GET /indexes`, `GET /indexes/{name}`, `describe_index_stats` and `query`, nothing else. It never writes. A key with a read-only role in Pinecone is advised in the guide for data owners. |
| D-5 | **Discovery lists indexes and namespaces.** With an API key, the admin sees the project's indexes (name, type, dimension, metric, status, whether supported and why not), picks one, and then sees its namespaces with the number of records (from `describe_index_stats`; the default namespace is shown as "(default)"). The index host comes from the index. A search by name is not offered: the list is the way. |
| D-6 | **Dimension and metric come from the index** and are fixed when the index is created, so there is no distance to choose. The KB shows them as the vector information of the inspection (`size`, `distance`). |
| D-7 | **Mapping.** The mapping model of 003 D-9 stays: roles and metadata fields bound to dictionary fields, with a **path**; for Pinecone the path is the metadata **key**. Pinecone metadata is flat (no nested objects, no nulls): a key is a string, a number, a boolean or a list of strings, so there is no dot notation, and a key that contains a dot cannot be mapped and is reported. Types: string → text; number → number; boolean → boolean; list of strings → list of texts. The `text` role needs a string key; `documentId`, `title`, `position` and `sourceUrl` accept strings and numbers, read as text. **Dates:** Pinecone has no date type and range filters only work on numbers, so a `date` dictionary field maps to a **numeric key holding a Unix timestamp**, and the mapping of that field records its **unit, seconds or milliseconds**, chosen by the admin. The draft proposes a date when a number key looks like one (a name such as `date`, `created_at`, `timestamp` or ending in `_at` or `_date`, and values in the plausible range of seconds or of milliseconds) and guesses the unit; the admin confirms. A string that holds an ISO date is a text field, never a date. The naming conventions that fill the draft, the type compatibility rule and the dictionary rules (clusters, aliases, scopes) are those of spec 003 and its amendment 1. |
| D-8 | **Sampling** reads **100 records** with one query that uses a fixed vector of the index's dimension (a seeded pseudo-random unit vector, so the draft is the same every time), `topK` 100, metadata included and vector values excluded. A query works on every index type and needs no listing of ids. The records are the nearest to that vector, not a random sample, which is enough to see the keys and their types; keys that appear in few records may be missed, and a pasted JSON Schema (flat, as in 003 D-9) covers them. If Pinecone refuses the response as too large (4 MB), the sample is retried once with 20 records. An empty namespace gives no sample and a warning. |
| D-9 | **Search.** One `query` per KB: the embedding as `vector`, `topK`, `namespace`, `filter`, `includeMetadata` true and `includeValues` false. Filters are one `$and` of clauses by dictionary type: text → `$in` the values; number → `$gte`/`$lte`; date → `$gte`/`$lte` with the bounds converted to Unix timestamps in the field's unit; boolean → `$eq`; list of texts → `$in` (Pinecone matches any element of a list). A record whose key has another type simply does not match; the query never fails for it. Pinecone returns the whole metadata of a record, and Quorum reads only the mapped keys. |
| D-10 | **Score and threshold.** The score is Pinecone's own, whose direction depends on the index metric (cosine and dot product: higher is more similar; Euclidean: lower is more similar, to be confirmed by the contract tests of D-15). As with the other kinds it is not compared across KBs (003 D-12). Pinecone's query has **no score threshold**, so Quorum applies the rag tool's threshold to the returned matches: below it (or above it, for Euclidean) the match is dropped, and the search can return fewer than K chunks without asking for more. |
| D-11 | **Verification** (003 D-8 for this kind), in order: the control plane answers and the key is accepted; the index exists, is ready and is a dense vector index; its dimension equals that of an embedding of the configured model; the namespace exists and has records; the mapped keys are present in a sample and have a compatible type. **Warnings that never change the status:** an index created with an integrated embedding model (`embed` present: the model of the installation's endpoint must be the same as the one Pinecone uses for ingestion, or the dimensions and the meaning of the vectors will not match); a pod-based index whose `metadata_config` indexes only some keys while a mapped key is not among them (filters on it fail or are slow); records indexed shortly before (Pinecone is eventually consistent: new records can take a short time to be queryable). A failed check saves the KB `Unavailable` with a warning that names the cause and the consequence, and does not block registration. |
| D-12 | **Client.** A thin **REST client** of ours over `IHttpClientFactory` (a named client), at the edge as CLAUDE.md allows. The official .NET SDK (`Pinecone.Client`) was **archived by Pinecone on 2026-07-03**, targets API 2025-04, speaks gRPC and has no Documents API, so it is not used; the community library is not either. The API version is **pinned** with the `X-Pinecone-Api-Version` header to `2026-07`, never left to the default (which moves when versions retire). Pinecone ships a version every quarter and supports each for at least 12 months, so moving to a newer version is a small change to the constant, the shapes and the contract tests. Failures map to a few outcomes with messages safe to show: unreachable or timeout, unauthorized (401 and 403), index or namespace not found (404), rejected (400: a vector of a different dimension, an invalid filter), rate limited (429), unexpected. A message never carries the key, the URLs or Pinecone's own text. |
| D-13 | **API.** `connection` for `Pinecone` is `{ apiKey?, indexName, namespace?, controlPlaneUrl?, indexHost? }`; in responses the same without the key, with `hasApiKey`. The `mapping` field of a date has `epochUnit` (`Seconds` or `Milliseconds`). Listing indexes and namespaces is a new call (API Contract). The inspection response fills `vectors` with the index's dimension and metric, and `collectionExists` reads "the collection, relation or index and namespace exist". |
| D-14 | **UI** in separate PRs after the backend, as 003 D-19 and 008 D-17: the wizard shows the Pinecone fields, lists indexes and namespaces, reviews the mapping with the unit of each date, verifies and runs a test search. The other kinds do not change. |
| D-15 | **Development and tests.** Pinecone Local, the official emulator, **reached end of life on 2026-10-07**; the replacement (a Prism mock of the OpenAPI specs) returns fixed example responses and stores nothing, so it cannot test ranking or filters. For the data plane the plan evaluates the official **`pinecone-index`** image (the single-index emulator that Pinecone Local used to start; no control plane of its own, so a small in-process stub serves the two control-plane calls); it must be pinned by digest, because `latest` belongs to a product that no longer evolves, and verified to support the calls of D-9 and `describe_index_stats` and to accept the pinned API version. If it does not, an emulator of our own in the repo (a small ASP.NET server with exact in-memory search) takes its place; this is a decision of the plan, with that fallback. The sample project seeds sample indexes with the deterministic embeddings of spec 003 (flat metadata, a list of tags, a date as a Unix timestamp in seconds and another in milliseconds, a numeric key and a string that look alike, a different dimension, an empty namespace, several namespaces, a pasteable schema). A **contract suite** that runs only by hand against a real Pinecone project (API key from an environment variable, never in CI) checks that the emulator and the real service agree on scores, filters and errors. |
| D-16 | **Unchanged:** permissions (`knowledge_base.manage`), scopes and the rule between scopes, tenant isolation (a tenant never sees the connection of another tenant's KB), audit of rag calls, logs with ids, counts, durations and codes only (never the API key, the queries or the chunks), lifecycle (deleting a KB deletes the registration only), and the test search. |

## Amendment 1: ready for lookups (spec 007)
**Status:** Approved (2026-10-22). Spec 007 (metadata lookups, approved 2026-10-22) extends the per-kind contract of spec 008 with an optional capability (007 D-15), adds a condition on the `documentId` role to the search (007 D-14) and new members to the mapping (007 D-2, D-3, D-4). Amendment 1 of spec 008 defines the contract side (capabilities, the condition, the order of work); this one says how the Pinecone kind receives them. D-9 and D-11 gain the points below; everything else stands.

| # | Decision |
|---|---|
| A1-1 | **No optional capability.** Pinecone declares no optional capability of the contract (008 A1-1), so a Pinecone KB reports lookups as not supported. The reason is the service itself: it has no facet or aggregation, and listing the distinct values of a key would mean listing and fetching every record of a serverless namespace (and pods cannot list). A design for that belongs to its own amendment, if a customer needs it. |
| A1-2 | **Condition on `documentId`.** The search of D-9 accepts the **"document id is any of these"** condition (008 A1-2; the `document_ids` of 007 D-14: strings, at most 50), combined in AND with the other clauses. It applies to the metadata key mapped to the `documentId` role. Pinecone's `$in` is typed and the mapping does not record whether the key holds strings or numbers, so the clause matches both: the ids as strings, and, for each id that is an integer or a decimal number, the same id as a number, in an `$or` of two `$in`. A record whose key has any other type, or a value that is not in the list, does not match; an id that matches no form matches nothing and is not an error. The 50 ids are far below the 10,000 values an `$in` accepts. A KB that has not mapped `documentId` is skipped by the caller (003 D-12), never searched without the condition. |
| A1-3 | **Verification warning.** The warning of D-11 about keys that a pod-based index does not filter on (`metadata_config`) also covers the key mapped to `documentId`, even though it is a role and not a dictionary field: a search with `document_ids` on it would fail or be slow. Serverless indexes filter on every key and give no warning. |
| A1-4 | **Mapping members of spec 007.** The roles `countedPoint` and `entityName` and the flag `listable` are **refused** for a Pinecone KB (400 `knowledge_base.lookups_not_supported`, 007 AC-3) while it has no lookups capability. They are additive in the stored mapping, alongside the `epochUnit` that D-7 adds to a mapped field, so mappings saved before are unaffected. |
| A1-5 | **Order.** This spec follows spec 008: the contract and its capabilities exist before the Pinecone connector is written. |

**Acceptance criteria** (added to the list of this spec):
- **AC-35** Given a consumer that asks a Pinecone KB for the lookups capability, Then it gets a "not supported" outcome and no call is made to Pinecone.
- **AC-36** Given a rag tool with `document_ids` over a Pinecone KB whose `documentId` key holds strings in some records and numbers in others, When it is called, Then the query matches the records of those documents of both kinds, in AND with the other clauses.
- **AC-37** Given an id that is not a number and a `documentId` key that holds numbers, When searching, Then that id matches nothing and the search succeeds; given a KB without `documentId` mapped, Then it is skipped and named.
- **AC-38** Given a mapping of a Pinecone KB that marks a field `listable` or sets `countedPoint` or `entityName`, When saving, Then it returns 400 `knowledge_base.lookups_not_supported`; given a mapping without them, Then it saves as before.
- **AC-39** Given a pod-based index whose `metadata_config` leaves out the key mapped to `documentId`, When verifying, Then a warning says so and the status is unchanged; given a serverless index, Then there is none.

## Domain Model
Only the KB's kind-specific settings and one attribute of the mapping change; the platform database gets no new table.

| Entity | Change |
|---|---|
| **KnowledgeBase** | `Kind` gains `Pinecone`. `Connection` is the kind-specific JSON of 008 D-15: for `Pinecone` `IndexName`, `Namespace`, `ControlPlaneUrl?`, `IndexHost?` and `ProtectedApiKey`. Dimension and metric are never stored: they are read at verification and at search. |
| **Mapping** | A mapped field gains an optional `EpochUnit` (`Seconds` or `Milliseconds`), present only for a date field of a kind that stores dates as numbers. Existing mappings have none and are read as before. |

## API Contract
Routes, permissions and error format as in specs 003 and 008. Only what changes is listed.

| Verb | Route | Change |
|---|---|---|
| POST | `/knowledge-bases/indexes` | **New.** Request `{ kind: "Pinecone", connection }` with `indexName` omitted: lists the project's indexes; with `indexName`: lists that index's namespaces. Response `{ reachable, indexes: [{ name, type, dimension, metric, ready, supported, reason? }] }` or `{ reachable, namespaces: [{ name, records }] }`. Another kind returns 400 `knowledge_base.unsupported_operation` (and `PgVector` keeps `/knowledge-bases/relations`). Errors: 400 `knowledge_base.invalid_connection`; 401; 403. |
| POST | `/knowledge-bases/inspect` | `connection` per kind. For `Pinecone`: `vectors` has one entry (`size`, `distance` from the index); the draft proposes dates with `epochUnit`; `unindexedFields` lists mapped-candidate keys not indexed (pod-based indexes). New warning codes are prefixed `pinecone_`. |
| POST / PUT | `/knowledge-bases`, `/knowledge-bases/{id}` | `connection` per kind; a date field's mapping carries `epochUnit`. New errors: 400 `knowledge_base.epoch_unit_required` (a date field mapped without a unit), 400 `knowledge_base.api_key_required` (D-3). `knowledge_base.unsupported_kind` lists the supported kinds. |
| GET | `/knowledge-bases/{id}` | `connection` per kind without the key (`hasApiKey`); the mapping returns each date's `epochUnit`. |
| POST | `/knowledge-bases/{id}/verify`, `/search` | Unchanged in shape; `Pinecone` verification adds the warnings of D-11. |

## Authorization
Unchanged from specs 003 and 008: `knowledge_base.manage` for every `/knowledge-bases` route, including the new listing; a tenant never sees the connection, index or key of another tenant's KB; content access remains at KB level (003 D-16). The permissions of the API key inside the Pinecone project are the data owner's to set.

## Edge Cases & Failure Modes
- **Wrong key, index not found, namespace not found or index not ready:** the KB is saved `Unavailable` with a warning that names the cause (003 D-8). A namespace that does not exist in Pinecone returns no records rather than an error, so the namespace check goes through the namespace list.
- **Dimension mismatch** between the index and the embedding model: warning at registration and verification; a search fails with a safe error and the KB becomes `Unavailable`.
- **Document or sparse indexes** are listed with the reason and cannot be registered.
- **Integrated-embedding indexes** can be registered with the D-11 warning.
- **Dates:** a date mapped to a key whose values are not numbers, or with no unit, is rejected on save (`knowledge_base.invalid_mapping`, `knowledge_base.epoch_unit_required`); a record with a string in that key does not match a date filter; seconds read as milliseconds would silently be wrong, which is why the unit is explicit and shown in the review.
- **Metadata with mixed types** across records (a number in some, a string in others): proposed as text with a warning, as in 003; a filter matches only the records whose value has the filter's type.
- **Keys with a dot** are not mappable (reported); records without metadata are skipped as unusable.
- **No score threshold in Pinecone:** the threshold is applied afterwards and fewer than K chunks may come back (D-10).
- **Filter limits:** at most 10,000 values for one `$in`, far above the 20 values per list the rag tool allows; a filter Pinecone rejects is an error result for that KB.
- **Response size:** a query result is limited to 4 MB; metadata is returned whole, so a namespace of very large records can exceed it. The search fails for that KB with a safe error and a hint to lower top K (keys are not selectable in the query).
- **Rate limits (429):** reported as a failed search for that KB, never retried in a loop; the answer continues with the other KBs (003 D-14).
- **Eventual consistency:** records upserted moments ago may not be found; verification does not treat that as an error.
- **Retired API version:** if Pinecone retires `2026-07`, searches fail with "unexpected" until the constant is moved; the quarterly cadence gives months of notice (D-12).
- **Partial failure**, **filter on a key a KB did not map**, **no result**, **result cap**, **invalid filter values**, **unusable chunks**: as in spec 003. A rag tool can mix Qdrant, PgVector and Pinecone KBs.

## Non-Functional Requirements
- No performance targets (as specs 001 to 004 and 008): search latency (embedding plus query) is bounded by the 15 s limit of 003 D-13, and inspection and verification by the 10 s connection limit.
- Sampling is bounded (100 records, 4 MB response) and calls are short-lived; one pooled `HttpClient` per named client, no state kept per KB.
- No outbound telemetry; the only added traffic goes to Pinecone (or the endpoints the admin enters).
- Logs carry ids, counts, durations and outcome codes only: never the API key, queries, chunks or URLs entered by the admin.
- REST over `IHttpClientFactory`, with no new dependency; the JSON shapes are our own records, covered by the contract suite.

## Integrations
- **Pinecone** control plane (`/indexes`) and data plane (`/describe_index_stats`, `/query`), API version `2026-07`.
- **Installation model endpoint** for query embeddings (`IEmbeddings`), unchanged.
- **Rag tool** (Agents): unchanged.
- **Dev setup:** the emulator image and stub of D-15 in the dev compose.
- No events published.

## Acceptance Criteria
**Connection and secrets**
- **AC-1** Given kind `Pinecone`, When inspecting or registering, Then it is accepted; given an unknown kind, Then 400 `knowledge_base.unsupported_kind` naming the supported kinds.
- **AC-2** Given an API key and no advanced fields, When inspecting, Then the control plane is `https://api.pinecone.io` and the index host is the one the control plane returns; given a control plane URL or an index host, Then they are used, over `http` or `https`.
- **AC-3** Given a registered `Pinecone` KB, When reading it through the API, Then the key is never returned, only `hasApiKey`, and it appears in no log, audit entry or error message.
- **AC-4** Given an update without key and with the same control plane URL, index host and index name, Then the stored key is kept; given any of them changed, Then 400 `knowledge_base.api_key_required`.
- **AC-5** Given a redirect from a configured host to another, When a call is made, Then it is not followed and the key is not sent to the other host.

**Discovery and mapping**
- **AC-6** Given a valid key, When listing indexes, Then each index shows name, type, dimension, metric and readiness; a sparse index or a document index is listed as not supported, with the reason, and cannot be registered.
- **AC-7** Given an index, When listing its namespaces, Then each is shown with its record count and the default one as "(default)".
- **AC-8** Given a namespace with records, When inspecting without a schema, Then 100 records at most are sampled with one query, a draft mapping, proposed dictionary fields and the warnings are returned, and the same sample gives the same draft; given an empty namespace, Then a warning says there is nothing to sample.
- **AC-9** Given metadata `chunk_text`, `doc_id`, `file_name`, `page`, `source_url` and `year`, When the draft is built, Then the roles are `text`, `documentId`, `title`, `position`, `sourceUrl` and `year` is a metadata field named `year`.
- **AC-10** Given keys that are strings, numbers, booleans and lists of strings, When the draft is built, Then their dictionary types are text, number, boolean and list of texts.
- **AC-11** Given a number key named `created_at` with values in seconds, and another named `published` with values in milliseconds, When the draft is built, Then both are proposed as dates, with the units seconds and milliseconds; given a string key holding ISO dates, Then it is text.
- **AC-12** Given a date field mapped without a unit, or to a key that is not numeric, When saving, Then 400 `knowledge_base.epoch_unit_required` or `knowledge_base.invalid_mapping`.
- **AC-13** Given a key with a dot or a record without metadata, When the draft is built, Then the key is reported as not mappable and the record is skipped.
- **AC-14** Given a pasted JSON Schema of the flat metadata, When inspecting, Then the draft is built from it by the same conventions; invalid, non-object or over 100 KB gives 400 `knowledge_base.invalid_schema`.

**Registration and verification**
- **AC-15** Given a ready dense index, an existing non-empty namespace and matching dimensions, When registering, Then the KB is `Available` and no warning is returned.
- **AC-16** Given an unreachable control plane, a refused key, a missing index, namespace without records, an index not ready or a dimension mismatch, When registering, Then the KB is saved `Unavailable` with a warning that names the cause and the consequence for RAG.
- **AC-17** Given an index with an integrated embedding model, or a pod-based index whose `metadata_config` leaves out a mapped key, When verifying, Then each produces a warning and the status stays `Available`.
- **AC-18** Given an `Unavailable` KB that became ready, When verifying, Then it becomes `Available`.

**Search**
- **AC-19** Given an activated rag tool over a `Pinecone` KB, When the model calls it with a query, Then the query is embedded with the KB's model and prefix, the namespace is queried and the model receives numbered sources, best first.
- **AC-20** Given filters on a text, a number range, a date range, a boolean and a list, When the model calls the tool, Then the query carries the matching conditions and only matching records come back; a date range is converted to timestamps in the field's unit; a fixed filter still applies against a conflicting model value (003 AC-25, AC-26).
- **AC-21** Given a record whose key holds another type than the filter's, When filtering on it, Then the search succeeds and that record is not returned.
- **AC-22** Given a score threshold, When searching, Then the matches on the wrong side of it for the index metric are dropped by Quorum and fewer than top K chunks may be returned; given no match, Then the model is told that nothing was found.
- **AC-23** Given an unreachable service, a refused key, a missing index, a dimension mismatch, a rate limit or a query over 15 s, When the model calls the tool, Then it receives an error result without the key, URLs or Pinecone's own text; the KB becomes `Unavailable`, and a later successful search or verification makes it `Available`.
- **AC-24** Given a rag tool with a Qdrant KB, a PgVector KB and a Pinecone KB, When called without `knowledge_base`, Then the results are interleaved, each source names its KB and scores are not compared; one failing KB gives the others' results with a note (003 AC-27, AC-34).
- **AC-25** Given records whose `text` is missing or blank, When searching, Then they are skipped and counted.
- **AC-26** Given the API version header, When any call is made, Then it carries `X-Pinecone-Api-Version: 2026-07`.

**Authorization, tenants and logs**
- **AC-27** Given a user without `knowledge_base.manage`, When calling the listing route or any other `/knowledge-bases` route, Then it returns 403; unauthenticated returns 401.
- **AC-28** Given a Global `Pinecone` KB, When another tenant reads it, Then it sees name, description and status only, never the index, namespace, URLs or key.
- **AC-29** Given any listing, inspection, registration, verification or search, When the logs are inspected, Then no API key, query, chunk text or document name appears.

**UI**
- **AC-30** Given the admin pages, When registering a `Pinecone` KB, Then everything can be done without touching files: key, choosing an index and a namespace from the lists, reviewing the mapping with the unit of each date, verifying and running a test search.
- **AC-31** Given the registration of a Qdrant or PgVector KB, Then the flow and its results are the same as before.

**Development support and documentation**
- **AC-32** Given the dev setup, When running the sample project, Then the sample indexes of D-15 exist and each case can be registered and queried; the integration tests use the same generator with deterministic embeddings.
- **AC-33** Given the contract suite and a real Pinecone project, When run by hand, Then it reports whether the emulator agrees with the service on scores, filters and errors; it is skipped when no key is set and never runs in CI.
- **AC-34** Given the documentation, Then the guide for data owners has a section for Pinecone (what Quorum reads, how to keep dates as Unix timestamps, a read-only key, namespaces, document indexes not supported), and `docs/deferred.md` and `docs/database-schema.md` (secret and mapping members of the connection) are up to date.

## Deferred Decisions
| Decision | Fallback chosen now | Revisit trigger |
|---|---|---|
| Document indexes (Documents API: typed schema, full-text search, `score_by`) | Not supported; listed with the reason (D-1) | A customer with document indexes |
| Sparse and hybrid search, integrated search by text, Pinecone reranking | Dense vectors, search by vector (D-9) | Poor retrieval quality on exact terms or codes |
| Record id as the source of a role (document id, title) | Only metadata keys can be mapped (D-7) | Customers whose ids carry the document name |
| Whether the official `pinecone-index` image is enough for the data plane of the tests | Verified by a spike in the plan; if not, an emulator of our own in the repo (D-15) | The spike in the plan |
| Moving to a newer API version | `2026-07` pinned (D-12) | Pinecone announces the retirement of 2026-07 (supported at least 12 months), or a feature we need |
| An official or maintained .NET SDK for the current API | Our thin REST client (D-12) | Pinecone ships a maintained SDK with the current API |
| Lookups (spec 007) for Pinecone: distinct values with counts, list documents, count | Not supported; the mapping members of 007 are refused (A1-4). There is no facet, so counts and values would need a full list and fetch of a serverless namespace | A customer on Pinecone that needs lists and counts; a decision of its own amendment |

## Open Questions
_None._
