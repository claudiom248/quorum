# Spec 003: Knowledge Bases

**Status:** Approved (2026-10-17)
**Date:** 2026-10-17

## Problem
Spec 002 lets a user attach a file to **one conversation**: the extracted text goes into the model context and a document longer than the context budget is rejected. Companies have far more material than that (contracts, procedures, manuals) and want every assistant, for every user, to answer from it and to say where the answer comes from. That material is already prepared by the people who own the data, often through their own ETL, and sits in a vector store.

This spec lets an admin **register existing knowledge bases (KBs)** and lets assistants **query them through a `rag` tool**, with metadata filters and citations. Quorum only **consumes** knowledge bases: it does not ingest, chunk or embed documents (D-1).

## Scope
### In
- A new module **KnowledgeBases** with: a **metadata dictionary**, **KB registration** (connection, embedding model, field mapping), discovery and verification, and search (D-3).
- **Qdrant** as the only storage kind implemented, behind a per-kind abstraction (D-2).
- A **mapping** of payload fields to roles and dictionary fields, proposed by sampling points or by a pasted JSON Schema, always reviewed by the admin (D-9).
- The **rag tool**: a tool of the spec 004 system that points to one or more KBs, with a parameter schema generated from their metadata (D-4, D-11).
- Visibility **scopes** (Global / Tenant / Assistant), explicit activation per assistant, permission names, audit log: spec 004's model (D-5).
- **Citations**: numbered sources in the tool result, shown under the answer and stored with the message (D-15).
- Admin UI (dictionary, guided KB registration, rag tool configuration) and sources in the chat (D-19).
- A dev project that seeds sample KBs and a test fixture generator (D-20).

### Out
- Ingestion of any kind: extraction, chunking, embedding and writing into the store belong to the data owners (D-1).
- Other storage kinds: pgvector, raw documents (folder, S3-compatible, remote file systems, blob patterns), external SQL/MongoDB sources (D-2). See `docs/deferred.md`.
- A separate embedding endpoint per KB (the installation's model endpoint is used, D-7).
- Hybrid search, sparse vectors, reranking (D-18).
- Per-user or per-document permissions (D-16).
- Background monitoring of KBs and automatic detection of payload changes (D-17).
- Users and roles beyond `Admin`.
- Knowledge base use by agents of a topology (spec 005).

## Decisions
| # | Decision |
|---|---|
| D-1 | **Quorum only consumes knowledge bases.** The KB is populated and kept up to date by whoever owns the data, possibly with heavy external ETL. Quorum registers an already-populated KB and queries it. |
| D-2 | A KB has a **kind**, which decides its connection settings, discovery, mapping concepts and search. Only `Qdrant` is implemented. The abstraction must not assume Qdrant: the other kinds are recorded in `docs/deferred.md` (pgvector with tables and columns; raw documents from folders, S3-compatible or remote storage with a blob pattern; external SQL/MongoDB). |
| D-3 | A new module **KnowledgeBases** (schema `knowledge_base`, own DbContext, `Quorum.Modules.KnowledgeBases.Contracts`) owns dictionary, KBs, storage connectors, discovery and search. It needs query embeddings from Agents and Agents needs search from it: both go through the Contracts projects only, never through the other module's data. |
| D-4 | Retrieval reaches the assistant **as a tool** of spec 004's system, origin `Rag`, named `rag__<name>` (D-16 of spec 004). A rag tool is a **configured binding** to one or more KBs (up to 10), with its own description for the model, retrieval parameters, fixed filters, scope and policy. It lives in the Agents module with the other tools (pool, activation, policy, audit); its KB list holds KnowledgeBase ids. Default policy `Auto` (read-only), changeable by the admin. |
| D-5 | KBs and rag tools use **spec 004's model**: registration by API and admin page; visibility scope `Global` (all tenants), `Tenant` (listed tenants) or `Assistant` (listed assistants), set by the installation admin for Global/multi-tenant (D-30 of spec 004); explicit activation of the rag tool per assistant (`tools.attach`). Global KBs are how knowledge is shared across tenants: one registration, one index, never duplicated. |
| D-6 | The **connection is per KB** and its settings are specific to the kind (for Qdrant: URL, API key, collection, optional vector name). Secrets are encrypted at rest with the master key, never returned (only "set / not set"). The **content of a KB never lives in, or is read from, the platform database**: the platform PostgreSQL holds only the registrations (dictionary, KB configuration, mapping). A KB can sit on any server; a future pgvector kind has its own connection (host, database, credentials) and never reuses the platform's connection string. This replaces the product.md idea of one vector store chosen in the wizard: the bundled `qdrant` service in the compose file stays only as a convenient default target for data owners. URL policy as spec 004 D-18: internal network allowed; cloud metadata and link-local addresses blocked, at registration and at every connection (DNS included); redirects to another host not followed. |
| D-7 | Query embeddings use the **installation's model endpoint** (OpenAI-compatible `/embeddings`), with the **embedding model name stored per KB** and an optional **query prefix** (e.g. `search_query: ` for nomic/e5/bge models) prepended before embedding. A dedicated embedding endpoint per KB is a later spec. |
| D-8 | Registration runs a **verification**: reachability, collection exists, vector size (of the chosen vector) equals the size of an embedding probe from the configured model. A failed verification **does not block** registration (the KB may not be ready yet): it is saved `Unavailable` with a clear warning that RAG on it will malfunction (unreachable, or ingestion and retrieval models with different dimensions). Verification can be repeated on demand; it also checks that the mapped fields still exist in sampled points. |
| D-9 | **Mapping.** A *role* is the meaning a payload field has for Quorum: `text` (required: the chunk text the model reads), `documentId`, `title`, `position` (page or section), `sourceUrl`. Every other mapped field is a **metadata field** bound to a dictionary field. A draft mapping is produced by **sampling points** of the collection or by a **pasted JSON Schema** of the payload (plain JSON Schema, no Quorum keywords: whoever runs the ingestion knows nothing about Quorum). The draft is filled by **naming conventions** (see Domain Model); the admin reviews and may change everything; nothing is saved without the admin's confirmation. |
| D-10 | **Metadata dictionary.** Typed fields defined once and reused by KBs and rag tools. Types: **text, number, boolean, date, list of texts**. Scope `Global` or `Tenant` as KBs; a Global KB can use only Global fields, a Tenant KB Global and its own; names are unique among the fields visible to a tenant. Name and type are fixed once created, the description is editable, and a field in use cannot be deleted. A field named in a pasted schema that does not exist yet is **proposed for creation** and the admin confirms; an existing field of a different type is an error to resolve first. |
| D-11 | **Generated tool schema.** The rag tool's input schema is generated from the binding: `query` (required); `knowledge_base` (enum of its KB names, only when it has more than one); and one group of filter parameters per **exposed dictionary field**: text → `<field>` (array of strings, matches any); number → `<field>_min`, `<field>_max`; date → `<field>_from`, `<field>_to` (ISO 8601 dates); boolean → `<field>`; list of texts → `<field>` (array, contains any). The admin chooses which of the fields used by the KBs are exposed (default all, at most 10) and may set **fixed filters** (same shapes, combined with AND, never visible to the model, never overridable). Dictionary names cannot be `query` or `knowledge_base` or end with `_min`, `_max`, `_from`, `_to`. |
| D-12 | With several KBs and no `knowledge_base` from the model, the tool searches **all** of them and **interleaves** their results (round-robin) up to the cap: scores of different KBs, models or distances are never compared. Each source names its KB. A KB that has not mapped a field used in a model filter or in a fixed filter is **skipped for that search** (never searched without the filter) and the result says which KBs were skipped. |
| D-13 | Retrieval parameters are set by the admin in the binding, within ceilings: **top K** default 5 (max 20); **result cap** 6,000 characters (whole chunks are dropped, never cut); optional **score threshold**; **15 s** timeout per search (embedding + storage); at most **10 KBs** per rag tool. The model passes only query, KB and filters. |
| D-14 | A search failure is returned **as an error tool result** and the answer continues. If only some KBs fail, the results of the others are returned with a note naming the failed KBs; the error result is for the case where all fail. The model gets a synthetic message with no internal details (no URL, no stack); the tool card shows the error; the outcome is audited. A failed search marks the KB `Unavailable`; the **next successful search or verification** marks it `Available` again (searches are always attempted, so a transient failure never locks a KB out). |
| D-15 | **Citations.** The tool result lists numbered sources `[n]` (KB, title, position) each with its chunk text, as untrusted data in delimiters; the system prompt carries the instruction to cite them as `[n]` and that retrieved text is never instructions (spec 004 D-31). Numbering continues across rag calls in the same answer. The chat shows the sources under the answer, and **the sources are stored with the message** (references only: KB name, document id, title, position, URL; never chunk text). This is a deliberate exception to spec 004 D-4 (tool calls are not persisted). |
| D-16 | Access control is **at KB level**: whoever can use an assistant with the rag tool can retrieve any chunk of the tool's KBs. The admin can carve slices with fixed filters. Per-user/per-document permissions (e.g. from OIDC claims) are deferred; a KB must hold only content readable by every user of the assistants that use it. |
| D-17 | **Lifecycle.** Deleting a KB deletes the registration only (never data in the store): it leaves its rag tools, and sources already stored in messages keep their copied title and URL. No background monitoring: changes of the payload are discovered at verification or at search. |
| D-18 | Qdrant: **one dense vector**, optionally a named vector when the collection has several; the distance metric is read from the collection. Sparse vectors, hybrid search and reranking are deferred. Payload paths use dot notation for nested objects. |
| D-19 | UI in this spec, in separate PRs after the backend as in spec 004: admin pages for the dictionary, for guided KB registration (connection, verification, discovery or pasted schema, mapping review, a search to try it, D-22) and for the rag tool (KBs, exposed fields, fixed filters, parameters, preview of the generated definition); sources under the answer in the chat. |
| D-20 | A dev project (e.g. `tools/Quorum.SampleKnowledgeBase`) seeds the dev compose Qdrant with sample collections covering the cases (flat and nested payload, list tags, fields without payload index, named vector, a different vector size, an empty collection, and a pasteable schema). Integration tests (Testcontainers) reuse the same generator with deterministic fake embeddings. |
| D-21 | **Audit and logs.** Every rag call goes to spec 004's audit log (user, assistant, tool, arguments, outcome, duration, number of sources; 90-day retention). Application logs never carry queries, chunk text, document names or credentials: only ids, counts, durations and outcome codes. |
| D-22 | The admin can run a **test search** on a KB (`knowledge_base.manage`): query and filters in, scored results out, to check a KB before exposing it. It is not audited as a tool call and is not available to ordinary users. |

## Domain Model
Every application entity carries `TenantId` (the owner). KnowledgeBase entities live in schema `knowledge_base`; the rag tool lives in Agents (schema `agents`).

| Entity | Module | Main fields | Lifecycle |
|---|---|---|---|
| **DictionaryField** | KnowledgeBases | Id, TenantId, Name (`[a-z][a-z0-9_]{0,39}`), Type (`Text` / `Number` / `Boolean` / `Date` / `TextList`), Description, Scope (`Global` / `Tenant`), CreatedAt | Created by the admin or from a pasted schema; description editable; hard delete only when unused |
| **KnowledgeBase** | KnowledgeBases | Id, TenantId, Name (unique per tenant, `[a-z0-9_-]`, up to 40), Description (shown to the model), Kind (`Qdrant`), Connection (kind-specific JSON; Qdrant: Url, ApiKey encrypted, Collection, VectorName?), EmbeddingModel, QueryPrefix?, Mapping, Scope (`Global` / `Tenant` / `Assistant`), ScopeTenantIds, ScopeAssistantIds, Status (`Available` / `Unavailable`), LastError, LastVerifiedAt, CreatedAt, UpdatedAt | Created by registration (even if unreachable); editable; hard delete of the registration only |
| **Mapping** (owned JSON of KnowledgeBase) | KnowledgeBases | Roles: `text`, `documentId?`, `title?`, `position?`, `sourceUrl?` (payload paths); Fields: list of `{ path, dictionaryFieldId }` | Edited with the KB |
| **RagTool** | Agents | Id, TenantId, Name (unique per tenant, `[a-z0-9_-]`), Description (for the model), KnowledgeBaseIds (1-10), ExposedFieldIds, FixedFilters (JSON), TopK, ScoreThreshold?, MaxResultCharacters, Scope, State, Policy | A tool of origin `Rag` in the pool/activation/audit system of spec 004; hard delete removes its activations |
| **MessageSource** | Conversations | MessageId, N, KnowledgeBase (name snapshot), DocumentId?, Title?, Position?, Url? | Stored with the assistant message; deleted with it; replaced on regeneration |

- **Conventions that fill the draft mapping** (case-insensitive, `-`/`_` ignored, at the root or inside any nested object): `text` ← `text`, `chunk_text`, `content`, `chunk`, `body`; `documentId` ← `document_id`, `doc_id`, `source_id`; `title` ← `title`, `file_name`, `filename`, `document_name`, `name`; `position` ← `page`, `page_number`, `position`, `section`; `sourceUrl` ← `source_url`, `url`, `uri`, `link`. Every other scalar or list-of-scalars property becomes a metadata field whose dictionary name is the **last segment of its path** (`meta.year` → `year`); a collision between two paths, a name not valid for the dictionary or a reserved name is flagged for the admin. Objects, nested lists and arrays of objects are not mapped.
- **Discovery by sampling**: up to 20 points are read (payload only, no vectors) from the collection; a property's type is inferred from its values; mixed types are proposed as text with a warning. The collection information also gives vector names, size, distance and which payload fields have an index; a metadata field without index gets a warning (filters on it can be slow on large collections).
- **Pasted schema**: an object JSON Schema (draft 07 or 2020-12) with inline `properties`; `type`, nested `properties`, `items` of scalars and `format: date` / `date-time` are read, other keywords ignored; `$ref` is not resolved; up to 100 KB.
- **Type compatibility**: a metadata field's payload values must fit the dictionary type (date values must be ISO 8601 strings); incompatible mappings are rejected on save.
- **Rule between scopes** (D-5, D-10): a rag tool's KBs must be visible to its whole scope; a KB's dictionary fields must be visible to its whole scope. Violations are rejected on save; if a scope is narrowed later, a tenant that can no longer see a KB or field never gets it from a tool, and the tool ignores it.
- **Module boundaries**: Agents calls KnowledgeBases through `IKnowledgeBaseSearch` (search KB ids with query, filters and limits for a tenant and assistant; returns scored results with their source references) and `IKnowledgeBaseCatalog` (KB names, descriptions and visible dictionary fields, to generate the tool definition and validate bindings). KnowledgeBases calls Agents through a new `IEmbeddings` in `Quorum.Modules.Agents.Contracts` (embed texts with a named model on the installation's endpoint; no Microsoft.Extensions.AI type crosses the Contracts). Conversations stores sources through `IAssistantChat` (a new chunk kind `Sources`) and never reads KnowledgeBase.

## API Contract
Errors use ProblemDetails with a stable `code`. All routes are under `/api`; authentication as in spec 001. Routes under `/knowledge-bases` require `knowledge_base.manage`; rag tools are tools: `/tools/rag` requires `tools.manage`; activation `tools.attach`; chat `tools.use`.

**Dictionary**

| Verb | Route | Request | Response | Errors |
|---|---|---|---|---|
| GET | `/knowledge-bases/dictionary` | - | `[{ id, name, type, description, scope, usedByCount }]` | 401; 403 |
| POST | `/knowledge-bases/dictionary` | `{ name, type, description?, scope }` | 201 `{ id }` | 400 `knowledge_base.invalid_field_name` / `knowledge_base.reserved_field_name`; 401; 403; 409 `knowledge_base.field_name_taken` |
| PUT | `/knowledge-bases/dictionary/{id}` | `{ description }` | 204 | 400; 401; 403; 404 |
| DELETE | `/knowledge-bases/dictionary/{id}` | - | 204 | 401; 403; 404; 409 `knowledge_base.field_in_use` (lists KBs and rag tools) |

**Knowledge bases**

| Verb | Route | Request | Response | Errors |
|---|---|---|---|---|
| POST | `/knowledge-bases/inspect` | `{ kind, connection, embeddingModel, queryPrefix?, schema? }` (`schema`: pasted JSON Schema, optional; omitted = sampling) | `{ reachable, collectionExists, vectors: [{ name?, size, distance }], embeddingSize, dimensionMatch, draftMapping, proposedFields[], unindexedFields[], warnings[] }` | 400 `knowledge_base.invalid_schema`; 401; 403; 422 `knowledge_base.url_blocked` |
| GET | `/knowledge-bases` | - | `[{ id, name, kind, status, scope, lastVerifiedAt, warning? }]` | 401; 403 |
| POST | `/knowledge-bases` | `{ name, description, kind, connection, embeddingModel, queryPrefix?, mapping, scope, scopeTenantIds?, scopeAssistantIds?, createFields?[] }` | 201 `{ id, status, warnings[] }` (saved even if verification fails, D-8) | 400 `knowledge_base.invalid_name` / `knowledge_base.invalid_mapping` / `knowledge_base.field_not_visible` / `knowledge_base.scope_not_supported`; 401; 403; 409 `knowledge_base.name_taken` / `knowledge_base.field_type_conflict` / `knowledge_base.field_removed` (a dictionary field of the mapping was deleted while saving); 422 `knowledge_base.url_blocked` |
| GET | `/knowledge-bases/{id}` | - | detail (`hasApiKey` instead of the key) | 401; 403; 404 |
| PUT | `/knowledge-bases/{id}` | as POST (omitted `apiKey` keeps the stored one, only while the server address is unchanged) | 200 `{ status, warnings[] }` (saved even if verification fails) | as POST; 400 `knowledge_base.api_key_required` (the address changed, or the stored key cannot be read); 404 |
| DELETE | `/knowledge-bases/{id}` | - | 204 | 401; 403; 404 |
| POST | `/knowledge-bases/{id}/verify` | - | `{ status, warnings[] }` | 401; 403; 404 |
| POST | `/knowledge-bases/{id}/search` | `{ query, filters?, limit? }` (D-22) | `{ results: [{ score, text, source }] }` | 400; 401; 403; 404; 422 `knowledge_base.unavailable` |

**Rag tools** (listed also by `GET /tools`, with origin `Rag`)

| Verb | Route | Request | Response | Errors |
|---|---|---|---|---|
| GET | `/tools/rag` | - | `[{ id, name, knowledgeBases[], scope, state, policy }]` | 401; 403 |
| POST | `/tools/rag` | `{ name, description, knowledgeBaseIds[], exposedFieldIds[], fixedFilters?, topK?, scoreThreshold?, maxResultCharacters?, scope, scopeTenantIds?, scopeAssistantIds?, policy? }` | 201 `{ id, definition }` | 400 `rag.invalid_name` / `rag.too_many_knowledge_bases` / `rag.invalid_filter` / `rag.limit_exceeded` / `rag.knowledge_base_not_visible`; 401; 403; 409 `rag.name_taken` |
| GET | `/tools/rag/{id}` | - | the configuration and `definition: { name, description, inputSchema, estimatedTokens }` | 401; 403; 404 |
| PUT | `/tools/rag/{id}` | as POST | 204 | as POST; 404 |
| DELETE | `/tools/rag/{id}` | - | 204 | 401; 403; 404 |

**Chat (spec 004 routes)**
- The SSE stream gains `sources` `{ sources: [{ n, knowledgeBase, documentId?, title?, position?, url? }] }`, sent when a rag call returns; `tool_call` / `tool_result` work as in spec 004 (the result `content` shows the text sent to the model).
- `GET /conversations/{id}` returns `sources[]` on each assistant message.
- Assistant pool and activation routes (`/assistants/{id}/tools`) are unchanged: rag tools appear in them.

## Authorization
- **Permissions**: `knowledge_base.manage` (dictionary, KBs, discovery, verification, test search). The rag tool configuration, activation and use reuse `tools.manage`, `tools.attach` and `tools.use` of spec 004. Held today only by `Admin`; endpoints use named policies, never role names.
- **Tenant isolation**: dictionary fields, KBs and rag tools carry `TenantId` with the global filter. A Global or multi-tenant KB or field is visible to other tenants through the scope rule, never writable or editable by them. A tenant never sees the connection details (URL, collection, key) of another tenant's KB; for a Global KB it sees name, description and status only.
- **Secrets**: the API key is encrypted with the master key, never returned and never in logs, audit entries or error messages.
- **Outbound requests**: URL policy of D-6 applies to inspect, registration, verification, test search and every search.
- **Content access**: KB level only (D-16).

## Edge Cases & Failure Modes
- **KB not ready or unreachable at registration**: saved `Unavailable` with a warning (D-8). Searching it returns the error of D-14.
- **Dimension mismatch** between the collection's vectors and the embedding model: warning at registration/verification; a search fails with a safe error and the KB becomes `Unavailable`.
- **Collection with several vectors and none chosen**: `knowledge_base.invalid_mapping`-class error at registration asking to choose the vector name.
- **Embedding endpoint down or without `/embeddings`**: verification warns; searches fail as in D-14.
- **Empty collection or no result above the threshold**: success with zero sources, and a message telling the model that nothing was found.
- **Partial failure** with several KBs: results of the working ones with a note on the failed ones (D-14).
- **Filter on a field a KB did not map** (model or fixed filter): that KB is skipped for the search and named in the result (D-12); if every KB is skipped the result is an error saying no KB supports the filter.
- **Filter values that do not fit the type** (a date that does not parse, a text for a number): returned to the model as an error result (counts as a round, spec 004 D-11), nothing is sent to the store.
- **Payload values that do not fit the mapping** at search time (a missing `text`, a wrong type): the chunk is skipped and counted; if all chunks are unusable the result tells so.
- **Result cap** (D-13): chunks are dropped whole from the end of the interleaved list; at least one chunk is always returned, and if the first alone exceeds the cap it is dropped and the result says the chunk was too large for the cap.
- **Scope narrowed after configuration**: tools ignore KBs and fields not visible to the tenant (never an error that reveals them); a tool with no visible KB is not offered.
- **KB deleted** while referenced (D-17): removed from rag tools; a rag tool left with no KB is not offered to the model; sources stored in messages stay.
- **Dictionary field in use**: delete returns 409 listing KBs and tools (D-10).
- **Pasted schema**: invalid JSON, not an object or larger than 100 KB → `knowledge_base.invalid_schema`; unsupported keywords are ignored with a warning.
- **Collisions and reserved names** in the draft mapping are flagged and block saving until resolved.
- **Source URLs** are rendered as links only for `http` and `https`, with `rel="noopener noreferrer"`; titles and positions are rendered as text, never as HTML.
- **Prompt injection**: retrieved text is untrusted. It is delimited and labelled as data, delimiters inside the text cannot close the block (as spec 002 AC-21), and a chunk that asks for a tool call cannot bypass the policy of that tool.
- **Context budget**: the rag tool's definition counts in the tools segment of the context gauge (spec 004 D-27); the preview shows its estimated size. A result is bounded by D-13 so it never fills the budget alone.
- **Concurrent searches** in one answer follow spec 004 (one call at a time); KBs inside one call are searched in parallel within the 15 s limit.
- **Regeneration** runs the search again and replaces the stored sources of that message.
- **Deleting a conversation or message** deletes its stored sources.
- **Tenant removal or KB owner change**: not supported in this spec.

## Non-Functional Requirements
- No performance targets (consistent with specs 001-004): search latency (embedding + store) is measured and bounded by the 15 s timeout.
- No outbound telemetry; the only added outbound traffic goes to the Qdrant servers the admin registers and to the installation's model endpoint (embeddings).
- Logs carry ids, counts, durations and codes only (D-21).
- Libraries for Qdrant access must be permissively licensed (no AGPL/SSPL). Microsoft.Extensions.VectorData is evaluated in the plan, but the access must work with **schemaless payloads** chosen by the admin, which typed records may not allow; own adapters are acceptable at this edge (see CLAUDE.md).
- Discovery reads at most 20 points and no vectors; verification and inspection respect a 10 s connection timeout.

## Integrations
- **Qdrant**: collection information (vectors, distance, payload indexes), scroll for sampling, filtered vector search, with the API key when set. Client library (gRPC or REST) chosen in the plan.
- **Installation model endpoint**: OpenAI-compatible `/embeddings`, through a new `IEmbeddings` contract in Agents.
- **Agents**: rag tool as origin `Rag` in the tool system of spec 004 (pool, activation, policy, audit, loop).
- **PostgreSQL (platform)**: new schema `knowledge_base` with registrations only (never KB content, D-6); a new table in `agents` for rag tools; message sources in the Conversations schema.
- No events published.

## Acceptance Criteria
**Dictionary**
- **AC-1** Given an admin, When creating a field with a valid name and type, Then it is listed with `usedByCount` 0.
- **AC-2** Given a name that is `query`, `knowledge_base`, or ends with `_min`, `_max`, `_from` or `_to`, When creating a field, Then it returns 400 `knowledge_base.reserved_field_name`.
- **AC-3** Given a visible field with the same name, When creating another, Then it returns 409 `knowledge_base.field_name_taken`.
- **AC-4** Given a field used by a KB or a rag tool, When deleting it, Then it returns 409 `knowledge_base.field_in_use` listing them; its name and type cannot be changed, its description can.
- **AC-5** Given a Global KB, When its mapping uses a Tenant field, Then saving returns 400 `knowledge_base.field_not_visible`; a Tenant KB may use Global and its own fields.

**Inspect and mapping**
- **AC-6** Given a reachable Qdrant collection, When inspecting without a schema, Then 20 points at most are sampled and a draft mapping, proposed dictionary fields and the unindexed metadata fields are returned.
- **AC-7** Given a payload with `chunk_text`, `doc_id`, `file_name`, `page`, `source_url` and a nested `meta.year`, When the draft is built, Then the roles are `text`, `documentId`, `title`, `position`, `sourceUrl` and `year` is a metadata field named `year`.
- **AC-8** Given a pasted plain JSON Schema, When inspecting, Then the draft is built from it by the same conventions; given invalid JSON, a non-object or more than 100 KB, Then it returns 400 `knowledge_base.invalid_schema`.
- **AC-9** Given two paths with the same last segment or a name that is not a valid dictionary name, When the draft is built, Then they are flagged and saving is blocked until the admin resolves them.
- **AC-10** Given an existing dictionary field of another type than the proposed one, When saving, Then it returns 409 `knowledge_base.field_type_conflict`; given a missing field, Then it is created only if the admin confirmed it (`createFields`).
- **AC-11** Given a collection with several vectors and none chosen, When saving, Then the admin is asked to choose the vector name.
- **AC-12** Given a mapping without the `text` role, or a date field whose values are not ISO 8601, When saving, Then it returns 400 `knowledge_base.invalid_mapping`.

**Registration and verification**
- **AC-13** Given a reachable, ready collection with matching dimensions, When registering, Then the KB is `Available` and no warning is returned.
- **AC-14** Given an unreachable Qdrant, a missing collection or a dimension mismatch, When registering, Then the KB is saved `Unavailable` with a warning naming the cause and the consequence for RAG.
- **AC-15** Given a URL pointing to a blocked address (e.g. `169.254.169.254`), When inspecting or registering, Then it returns 422 `knowledge_base.url_blocked`; a hostname that resolves to a blocked address is blocked at connection time too.
- **AC-16** Given a registered KB, When reading it through the API, Then the API key is never returned, only `hasApiKey`.
- **AC-17** Given an `Unavailable` KB that became ready, When verifying, Then it becomes `Available`.
- **AC-18** Given a KB name already used in the tenant, When registering, Then it returns 409 `knowledge_base.name_taken`.

**Rag tool configuration**
- **AC-19** Given KBs and exposed fields, When a rag tool is saved, Then its definition has `query`, `knowledge_base` only if it has several KBs, and the filter parameters of D-11 for exactly the exposed fields.
- **AC-20** Given a text, a number, a date, a boolean and a list field, When the definition is generated, Then the parameters are `<field>` (array), `<field>_min`/`_max`, `<field>_from`/`_to`, `<field>` (boolean) and `<field>` (array).
- **AC-21** Given more than 10 KBs or more than 10 exposed fields, a top K above 20 or an invalid fixed filter, When saving, Then it returns 400 with `rag.too_many_knowledge_bases`, `rag.limit_exceeded` or `rag.invalid_filter`.
- **AC-22** Given a rag tool of scope `Global` including a Tenant KB, When saving, Then it returns 400 `rag.knowledge_base_not_visible`.
- **AC-23** Given a new rag tool, Then its policy is `Auto` and it appears in `GET /tools` with origin `Rag`; it is in an assistant's pool only if its scope covers the assistant and it has at least one visible KB, and it reaches the model only when activated (`tools.attach`).

**Search**
- **AC-24** Given an activated rag tool, When the model calls it with a query, Then the query is embedded with the KB's model and prefix, the KB is searched and the model receives numbered sources with delimited chunk text.
- **AC-25** Given filters on a text, a number range, a date range, a boolean and a list field, When the model calls the tool, Then the store is queried with the matching conditions and only matching chunks come back.
- **AC-26** Given a fixed filter in the binding, When the model calls the tool with a conflicting filter, Then the fixed filter still applies and the model's value cannot override it.
- **AC-27** Given a tool with several KBs and no `knowledge_base`, When called, Then results are interleaved per KB, each source names its KB, and scores are not compared across KBs; with `knowledge_base` given only that KB is searched.
- **AC-28** Given a filter on a field one of the KBs did not map, When called, Then that KB is skipped, named in the result, and not searched without the filter; if all are skipped the result is an error.
- **AC-29** Given more matching chunks than top K or than the character cap, When called, Then at most top K chunks come back, whole chunks only, within the cap.
- **AC-30** Given a score threshold, When called, Then chunks below it are not returned; given no result, Then the model is told that nothing was found.
- **AC-31** Given an invalid filter value, When called, Then the model receives an error result, nothing is sent to the store and the round counts.
- **AC-32** Given retrieved text containing the delimiter or instructions, When it is sent to the model, Then the block cannot be closed from inside and it is labelled as data.

**Failures**
- **AC-33** Given an unreachable store, a down embedding endpoint or mismatched dimensions, When the model calls the tool, Then it receives an error result without internal details, the answer continues, the tool card shows the error and the audit records the failure.
- **AC-34** Given several KBs with one failing, When called, Then the others' results are returned with a note naming the failed one.
- **AC-35** Given a KB marked `Unavailable` by a failure, When a later search succeeds, Then it becomes `Available` again.
- **AC-36** Given a search above 15 seconds, When called, Then it ends as a failed result for that KB.

**Citations**
- **AC-37** Given a rag call, When it returns, Then the SSE stream carries a `sources` event with numbered references (KB, document id, title, position, URL) and numbering continues across rag calls in the same answer.
- **AC-38** Given a finished answer, When the conversation is reopened, Then each assistant message returns its sources (references only, no chunk text).
- **AC-39** Given a regeneration, When it completes, Then the sources of that message are replaced by the new ones; given a deleted conversation or message, Then its sources are deleted.
- **AC-40** Given a source URL that is not `http` or `https`, When shown, Then it is not rendered as a link.
- **AC-41** Given a KB deleted after the answer, When the conversation is reopened, Then the stored sources still show their title and URL.

**Authorization and tenants**
- **AC-42** Given a user without `knowledge_base.manage`, When calling any `/knowledge-bases` route, Then it returns 403; unauthenticated returns 401; likewise `tools.manage` for `/tools/rag`.
- **AC-43** Given tenant A's KB or field, When tenant B reads KBs, fields or tools, Then it sees nothing of A's unless the scope includes B; for a Global KB it sees name, description and status but never URL, collection or key.
- **AC-44** Given a scope narrowed after configuration, When a tenant that no longer sees a KB uses the tool, Then the KB is ignored and none of its data is returned.

**Audit and logs**
- **AC-45** Given a rag call, When it runs, Then an audit entry is written before it (fail closed, spec 004 D-28) with user, assistant, tool, arguments, outcome, duration and number of sources.
- **AC-46** Given any registration, inspect, verification or search, When the logs are inspected, Then no query, chunk text, document name or credential appears.

**Admin test search and lifecycle**
- **AC-47** Given an admin, When running a test search on a KB, Then scored results are returned and no audit tool-call entry is written; a user without `knowledge_base.manage` gets 403.
- **AC-48** Given a KB used by a rag tool, When it is deleted, Then the registration goes, the tool no longer includes it, and the store is untouched; a tool left with no KB is not offered to the model.

**UI**
- **AC-49** Given the admin pages, When managing the dictionary, registering a KB (connection, verification, discovery or pasted schema, mapping review, test search) and configuring a rag tool (with the generated definition preview), Then everything can be done without touching files.
- **AC-50** Given an answer with citations, When shown in the chat, Then the sources appear under it, `[n]` markers in the text match them, and sources not cited in the text are shown collapsed.

**Development support**
- **AC-51** Given the dev compose, When running the sample project, Then the sample collections of D-20 exist in Qdrant and each case can be registered and queried; the integration tests use the same generator with deterministic embeddings.

## Deferred Decisions
| Decision | Fallback chosen now | Revisit trigger |
|---|---|---|
| Ingestion in Quorum | None: data owners populate the store (D-1) | Customers without ETL capability |
| Other KB kinds: pgvector, raw documents with connectors (folder, S3-compatible, remote, blob pattern), external SQL/MongoDB | Only Qdrant; the abstraction by kind exists (D-2) | A customer needs them; each is a dedicated spec |
| Embedding endpoint per KB (different servers/models) | Installation endpoint, model name per KB (D-7) | A KB indexed with a model the installation endpoint does not serve |
| Hybrid search, sparse vectors, reranking | Dense vector only (D-18) | Poor retrieval quality on exact terms or codes |
| Per-user / per-document permissions (e.g. from OIDC claims) | KB-level access, fixed filters to carve slices (D-16) | Users and roles spec, shared KBs with mixed audiences |
| Background verification and drift detection | Manual verification (D-17) | Many KBs or frequent payload changes |
| Quorum-side embedding cache | None | Latency of embedding calls |

See also `docs/deferred.md`, to be updated when this spec is approved.

## Open Questions
_None._ The eight points raised while drafting (rag tool in Agents, default policy `Auto`, exposed fields chosen by the admin, skipping KBs that lack a filtered field, admin test search, status recovery after a failed search, partial results, storing all sources) were confirmed in review on 2026-10-17.
