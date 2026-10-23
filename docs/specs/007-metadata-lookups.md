# Spec 007: Metadata Lookups

**Status:** Approved (2026-10-22)
**Date:** 2026-10-22

## Problem
The `rag` tool of spec 003 does one thing: a semantic search that returns at most 20 chunks (003 D-13). Questions that ask for a **list** or a **count** cannot be answered with it: "list the suppliers", "list the departments", "which suppliers have the most contracts", "which policies are for managers", "which teams are Italian". The model sees a sample of chunks and presents it as if it were the whole list.

The answers are in the metadata of the knowledge bases (KBs) that data owners already maintain. This spec lets Quorum **list values and documents from that metadata** without searching by meaning:

- **Lookups**: lists of values (with the number of documents for each) computed ahead of time from the metadata fields an admin chooses, stored in the platform database and served from a cache. They answer "list the suppliers" and "which suppliers have the most contracts" at once, without touching the store while the user waits.
- **Live operations** with the filters of the dictionary: list the documents that match, count them, or list the values of a field under a filter. They answer "which policies are for managers" and "which teams are Italian".
- A **`document_ids`** parameter on the rag tools, so that the model can search *inside* the documents it just listed.

A benchmark on Qdrant 1.12 with 5 million points (appendix) shows that these queries are cheap **when the fields they touch have a payload index**, and slow, or refused by Qdrant, when they do not. The design is built around that fact.

## Scope
### In
- **Lookups** of three kinds: computed from a field of a KB, taken from a collection of entities kept by the data owner, or written by hand by the admin (D-1 to D-8).
- Storage in the platform database as source of truth, with `HybridCache` in front and a background service that fills, refreshes and warms it (D-9 to D-11).
- **Slices**: a lookup tool with fixed filters gets a list computed for its own slice of the data (D-6).
- The **lookup tool**, a tool of spec 004's system, with the operations `values`, `documents` and `count` (D-12, D-13).
- **`document_ids`** on the rag tool, optional per tool (D-14).
- The lookup operations as part of the per-kind contract of the KnowledgeBases module, implemented for Qdrant (D-15).
- Admin UI in separate PRs after the backend, guide for data owners and sample data (D-18, D-19).

### Out
- Lookups for the `PgVector` and `Pinecone` kinds (specs 008 and 009): the contract is ready, the implementations are later (D-15).
- An endpoint the data owner's ETL calls to refresh lookups, with a service token (`docs/deferred.md`).
- Normalising values (`IT`, `Italy`, `italiana`) or a synonym table for computed lookups (D-3). Manual lookups carry synonyms.
- Sums, averages, joins and free-form queries: they need a structured source (`docs/deferred.md`, external SQL).
- A cache shared between several Quorum instances.
- Per-user or per-document permissions (003 D-16).

## Decisions
Decisions of spec 003 not listed here stand: KBs, dictionary (with clusters and aliases), mapping roles, scopes, filters, status handling, audit and the rule that Quorum only reads the store.

| # | Decision |
|---|---|
| D-1 | **Lookup.** A lookup is a named list of values the model can read. Kinds: **`Computed`** (the distinct values of one metadata field of one KB, with the number of documents of each), **`Entity`** (the entities of a collection kept by the data owner, with their attributes) and **`Manual`** (values typed by the admin, each with an optional label and synonyms). A value has at most 200 characters. The lookup tool shows a lookup under a **name**: the dictionary field's name by default, or an alias, as in 003 A1-3. |
| D-2 | **What a lookup counts.** The unit is the **document**. The mapping gets an optional role **`countedPoint`**: a condition on one payload field, either a boolean that is `true` (for example `is_first_chunk`) or a field equal to a value (for example `chunk_index = 0`); when set, only the points that satisfy it are counted, and each document counts once (the data owner guarantees one such point per document). When it is not set but `documentId` is mapped, Quorum counts the **distinct document ids** (a heavier computation, acceptable in the background, D-10). When neither exists, it counts **chunks** and the lookup says so (`unit: "chunks"`), so the model does not present chunk counts as contract counts. The unit is stored with the lookup and shown to the model. |
| D-3 | **Which fields.** The admin marks a mapped field as **listable** in the mapping of the KB. Only fields of type `Text` and `TextList` can be listable. Values are stored **as they are** in the payload: no case folding, trimming or synonym table for computed lookups. Normalising is the job of the ingestion. The model sees the real values, so it can pass the right one to a filter. |
| D-4 | **Entity lookups.** A KB whose points are entities, not chunks (one point per supplier, department, team), gets a second optional role **`entityName`** in its mapping. A lookup of kind `Entity` lists the points of that KB: the name, and up to **10 attributes** the admin picks among the mapped dictionary fields (a precomputed `contract_count` is an attribute like any other). It can be sorted by an attribute of type number or date. The KB is registered with the existing flow (003 D-6, D-9): only the mapping gains a role. |
| D-5 | **Manual lookups.** A `Manual` lookup is configuration written by the admin, not content of a KB, and has its own **scope** (`Global` or `Tenant`, as dictionary fields) and no counts. Values can be typed, pasted (one per line, optional `label`, `synonyms` after a separator) or imported from a CSV. The model sees the value with its label and synonyms (`IT (Italy, Italia, italiana)`), which is how `italiana` finds `IT`. At most 5,000 values (D-9). |
| D-6 | **Slices.** A *slice* of a lookup is the set of fixed filters of the tool that uses it (003 D-11, D-16). A lookup tool with fixed filters sees a list **computed for its slice**; a tool with none sees the list of the whole KB. A slice is identified by a **hash of the canonical filters** (dictionary field ids, sorted, values normalised), so two tools with the same filters, written in any order, share the same list. Slices are created when the tool is saved (computed in the background, D-10) and removed when no tool uses them any more. At most **50 slices per KB**; saving a tool that would exceed it is refused. A KB that does not map a field used by the slice's filter has no such slice and is skipped, as in 003 D-12. A tool never sees values from documents its filters exclude. |
| D-7 | **Live operations.** Anything the model asks with filters of its own choice is computed **live** against the store, never from a stored list: `documents` (the documents that match, paginated), `count` (how many) and `values` with filters (the values of a computed field among the matching documents). They are **exact** (an approximate count without a payload index returns wrong numbers, see the appendix), bounded by the **15 s** timeout of 003 D-13, and combined with the fixed filters of the tool in AND. Filters use the dictionary fields the tool exposes, with the shapes and aliases of 003 D-11 and A1. |
| D-8 | **Payload indexes.** Computing a lookup needs a **payload index** on the listable field, on the counted-point field and on the fields of the slice's filters: without it Qdrant refuses a facet, so the lookup is saved `Failed` with a message naming the field and saying to create the index. The registration's inspection already lists unindexed fields (003 AC-6); the counted-point field and the listable fields are added to the warning. For **live operations** a filter on a field without index is **allowed** but can take a long time (12 to 15 s on 5 million points): the admin sees a warning when the tool is configured, the call ends at the timeout, and the result tells the model to try a narrower filter. |
| D-9 | **Storage.** Lookups live in the platform database, schema `knowledge_base`, and that is the **source of truth** (D-11 for the cache). This **amends 003 D-6** for one kind of data: the platform database now also holds the **values and counts** of the fields an admin marked as listable (and, for entities, the chosen attributes). It never holds chunk text, document titles, ids or any payload that is not a listable value or an entity attribute. Values are the customer's data: the guide for data owners says they end up in backups of the platform database. Values are deleted with their KB (003 D-17). The **ceiling of values per lookup** is an installation setting (`Quorum:Lookups:MaxValuesPerLookup`, default 5,000, maximum 50,000). Beyond it a computed lookup keeps the values with the most documents and is marked `truncated`. |
| D-10 | **Computation.** In the background, never in the request that triggers it. Triggers: saving a KB (or its mapping) for the lists of the whole KB; saving a lookup tool for its slice; a **scheduled refresh** per KB (default **every 24 hours**, from 1 hour to 7 days, set per KB); the admin's **Refresh now** button. A computation uses exact facets (`exact: true`) on the listable field, filtered by the slice and by the counted point when present; without a counted point it scrolls payloads (document id and field only) up to a cap of **5,000,000 points** and marks the list `truncated` if it stops there. It has a time budget, **10 minutes** by default (`Quorum:Lookups:ComputationTimeoutMinutes`); at most **2 computations** run at once in the installation and one per KB (fixed). The result is written as a **new generation** of the lookup's values, and a single transaction makes it the current one and deletes the previous; readers never see a half-written list. If a computation fails, the **last good generation stays**, the lookup is marked `Stale` with the error, and the next trigger retries. |
| D-11 | **Cache.** `HybridCache` with the **in-memory layer only** (no distributed cache is configured, so the one-command installation stays one command). The key holds the slice and its generation, entries are tagged by KB and lookup, and the tag is invalidated when a new generation becomes current. Reads go cache, then table, then (if neither has the list) an answer to the model saying that the lookup **is being prepared**. A background service **warms the cache at startup** by reading the current generations from the tables, without blocking startup and without recomputing: after a restart nothing is lost. The cache is only an accelerator; if it is empty or fails, the tables answer. |
| D-12 | **The lookup tool.** A tool of origin **`Lookup`**, named `lookup__<name>`, configured like a rag tool and living in the Agents module (pool, activation, scope, policy, audit as in spec 004). It binds up to **10 lookups** and, for the live operations, up to **10 KBs**, with a description for the model, the exposed dictionary fields (up to 10, with aliases) and fixed filters (003 D-11, A1). Default policy `Auto`. The model sees **one tool per binding**, with these parameters: `operation` (`values` by default, `documents`, `count`; the last two only when the binding has KBs), `lookup` (enum of the bound lookups, for `values`), `knowledge_base` (enum, only when several), the filter parameters of the exposed fields, `search` (substring of a value, case-insensitive), `sort` (`name` or `count`; for an entity lookup also one of its attributes), `limit` (default 50, maximum 200) and `cursor` (opaque, returned by the previous page). The generated definition is shown in the admin like a rag tool's, with its estimated size in tokens (spec 004 D-27). |
| D-13 | **What the model receives.** Always **untrusted data** in delimiters, as for rag (spec 004 D-31, 003 AC-32), bounded by the tool's result cap of 6,000 characters (whole rows only). `values`: the lookup name, the **unit** (`documents` or `chunks`), the number of values, **`as of <timestamp>`** and the `stale` and `truncated` flags, then the page of rows `value (label, synonyms) - count`. `documents`: for each document the id, title, URL and the values of the exposed fields, 50 per page, with a cursor. `count`: the exact number, the unit and the filters applied. A lookup that is `Failed` with no earlier generation, or still preparing, is an error result in plain words without internal details. |
| D-14 | **`document_ids` on rag tools.** Every rag tool gets an optional switch **`allowDocumentIds`**, **on** by default for new tools and off for tools saved before this spec. When on, its definition has `document_ids` (array of strings, at most **50**), translated into a "matches any" condition on the `documentId` role of each KB. It is combined in AND with the fixed filters and the model's filters and **can never widen them**, so a slice carved by fixed filters stays closed. A KB that has not mapped `documentId` is skipped for that search and named in the result (003 D-12). The inspection and the tool editor warn that `documentId` has no payload index. This changes the generated schema of 003 D-11; the definition hash includes the switch, so a changed tool goes to `PendingReview` (spec 004). |
| D-15 | **Per-kind contract.** The per-kind contract of spec 008 D-1 gains an optional capability, **lookups**, with four operations: *distinct values* of a field under a filter with counts (`exact`, limit), *list documents* under a filter (cursor, limit), *count* under a filter, and *scan* (enumerate entity points). Only **Qdrant** implements it here, with the facet API (needs Qdrant **1.12 or later**; registration warns if the server is older and lookups of that KB fail with a clear message), `scroll` with filter and `order_by`, and `count` with `exact: true`. A KB of another kind does not support lookups: its fields cannot be marked listable and a lookup tool ignores it, naming it. Pgvector (a `GROUP BY` away) and Pinecone (no facet) are decided in their own amendments. |
| D-16 | **Scopes and tenants.** A computed or entity lookup belongs to its **KB**: same `TenantId`, same scope, and the scope rule of 003 D-5 and 003's Domain Model (a tool's KBs must be visible to its whole scope). A tenant that sees a Global KB sees its lookups through the tool and never the connection. A manual lookup has the scope of D-5. All lookup tables carry `TenantId` and the global query filter. |
| D-17 | **Audit and logs.** Every lookup tool call goes to the audit log of spec 004 (user, assistant, tool, arguments, outcome, duration, number of values or documents returned). Background computations log ids, counts, durations and outcome codes; **never values, queries, titles or credentials** (003 D-21). |
| D-18 | **UI**, in separate PRs after the backend, as in 003 D-19: in the KB registration, a step to mark **listable** fields and pick the **counted point** and the **entity name**; a Lookups page (list with status, unit, values, last computation, error, **Refresh now**; a view of the values; manual lookups with import); the lookup tool editor (bound lookups and KBs, exposed fields with aliases, fixed filters, a warning for unindexed filter fields, preview of the generated definition); the `allowDocumentIds` switch in the rag tool editor. |
| D-19 | **Data owners and samples.** The guide for data owners (`docs/knowledge-bases.md`) gets a section: which fields to index (the listable ones, the counted point, the filter fields and `documentId`), the counted-point convention (the draft mapping proposes `is_first_chunk`, `first_chunk` and `is_primary` as true, and `chunk_index` equal to 0; the admin confirms), and when to keep a **collection of entities** instead. `tools/Quorum.SampleKnowledgeBase` gets a collection with suppliers, departments, nationalities, audiences, an `is_first_chunk` flag and a matching collection of entities. |

## Domain Model
Every application entity carries `TenantId`. Lookup tables live in schema `knowledge_base`; the lookup tool lives in Agents (schema `agents`).

| Entity | Module | Main fields | Lifecycle |
|---|---|---|---|
| **Lookup** | KnowledgeBases | Id, TenantId, Kind (`Computed` / `Entity` / `Manual`), KnowledgeBaseId? (null for `Manual`), DictionaryFieldId? (`Computed`), Name (for `Manual`), Description?, Scope (`Manual` only: `Global` / `Tenant`, with ScopeTenantIds), Unit (`documents` / `chunks`), CreatedAt | Created when a field is marked listable, an entity role is mapped, or the admin creates a manual lookup; deleted when the mapping stops marking it, with its KB, or by the admin |
| **LookupSlice** | KnowledgeBases | Id, TenantId, LookupId, FilterHash, Filters (JSON, by dictionary field id), Status (`Pending` / `Ready` / `Failed`), Stale (bool), CurrentGeneration, ValueCount, Truncated, ComputedAt?, LastError?, NextRefreshAt | Created with the lookup (empty filters) or when a tool using it is saved; deleted when no tool uses it. Manual lookups have exactly one |
| **LookupValue** | KnowledgeBases | SliceId, Generation, Value, Label?, Synonyms (text array)?, DocumentCount?, Attributes (JSON)?, Position | Written as a whole generation; the previous one is deleted when the new one becomes current |
| **KnowledgeBase** (changed) | KnowledgeBases | Mapping gains roles `countedPoint?`, `entityName?`, and `listable` on each mapped field; `LookupRefreshHours` (default 24) | |
| **LookupTool** | Agents | Id, TenantId, Name (`[a-z0-9_-]`, unique per tenant), Description, LookupIds, KnowledgeBaseIds, ExposedFields (`{ fieldId, alias? }`), FixedFilters (JSON), Scope, State, Policy, CreatedAt, UpdatedAt | A tool of origin `Lookup` in spec 004's system; deleting it removes its activations |
| **RagTool** (changed) | Agents | `AllowDocumentIds` | |

- A mapped field marked listable keeps one **whole-KB lookup**; each lookup tool that binds it adds slices for its fixed filters (D-6). The same row serves every tool with the same hash.
- Indexes: `lookup_values (SliceId, Generation, DocumentCount desc)` and `(SliceId, Generation, Value)`. A `search` is a substring match over at most the ceiling of D-9 rows of one slice, with no extra index.
- **Module boundaries**: Agents reads lookups through a new `IKnowledgeBaseLookups` in `Quorum.Modules.KnowledgeBases.Contracts` (read a page of a lookup for a tenant and slice, run a live operation, list the lookups visible to a tenant for the tool editor, and report which slices a tool needs) and never touches KnowledgeBase tables. The background service and the cache are inside KnowledgeBases.

## API Contract
Errors use ProblemDetails with a stable `code`; routes are under `/api`, authenticated as in spec 001. Routes under `/knowledge-bases` need `knowledge_base.manage`; `/tools/lookup` needs `tools.manage`; activation `tools.attach`; use `tools.use`.

**Mapping of a KB** (the routes of 003 stay; the request and detail of `POST` and `PUT /knowledge-bases` gain): `mapping.countedPoint?` (`{ path, value }`: `true`, or a scalar that fits the field's type), `mapping.entityName?` (path), `mapping.fields[].listable` (bool), `lookupRefreshHours?`. New errors: 400 `knowledge_base.invalid_listable_field` (not Text or TextList), `knowledge_base.invalid_counted_point` (the value does not fit the field's type), `knowledge_base.lookups_not_supported` (kind without lookups).

**Lookups**

| Verb | Route | Request | Response | Errors |
|---|---|---|---|---|
| GET | `/knowledge-bases/{id}/lookups` | - | `[{ id, name, kind, field?, unit, slices: [{ filters, status, stale, valueCount, truncated, computedAt, error? }] }]` | 401; 403; 404 |
| POST | `/knowledge-bases/{id}/lookups/refresh` | - | 202 | 401; 403; 404; 409 `lookup.refresh_running` |
| POST | `/knowledge-bases/lookups/{lookupId}/refresh` | - | 202 | as above |
| GET | `/knowledge-bases/lookups/{lookupId}/values` | query: `slice?`, `search?`, `sort?`, `limit?`, `cursor?` | `{ unit, asOf, stale, truncated, values: [{ value, label?, synonyms?, count?, attributes? }], cursor? }` | 401; 403; 404 |
| GET | `/knowledge-bases/manual-lookups` | - | `[{ id, name, description, scope, valueCount }]` | 401; 403 |
| POST | `/knowledge-bases/manual-lookups` | `{ name, description?, scope, scopeTenantIds?, values: [{ value, label?, synonyms? }] }` | 201 `{ id }` | 400 `lookup.invalid_name` / `lookup.too_many_values` / `lookup.invalid_value`; 401; 403; 409 `lookup.name_taken` |
| PUT | `/knowledge-bases/manual-lookups/{id}` | as POST | 204 | as POST; 404 |
| DELETE | `/knowledge-bases/manual-lookups/{id}` | - | 204 | 401; 403; 404; 409 `lookup.in_use` (lists tools) |

**Lookup tools** (also listed by `GET /tools`, origin `Lookup`)

| Verb | Route | Request | Response | Errors |
|---|---|---|---|---|
| GET | `/tools/lookup` | - | `[{ id, name, lookups[], knowledgeBases[], scope, state, policy }]` | 401; 403 |
| POST | `/tools/lookup` | `{ name, description, lookupIds[], knowledgeBaseIds[], exposedFields: [{ fieldId, alias? }], fixedFilters?, scope, scopeTenantIds?, scopeAssistantIds?, policy? }` | 201 `{ id, definition, warnings[] }` | 400 `lookup_tool.invalid_name` / `lookup_tool.too_many_lookups` / `lookup_tool.invalid_filter` / `lookup_tool.alias_conflict` / `lookup_tool.invalid_alias` / `lookup_tool.lookup_not_visible` / `lookup_tool.too_many_slices`; 401; 403; 409 `lookup_tool.name_taken` |
| GET | `/tools/lookup/{id}` | - | configuration and `definition: { name, description, inputSchema, estimatedTokens }` | 401; 403; 404 |
| PUT | `/tools/lookup/{id}` | as POST | 204 | as POST; 404 |
| DELETE | `/tools/lookup/{id}` | - | 204 | 401; 403; 404 |

**Rag tools:** `POST` and `PUT /tools/rag` gain `allowDocumentIds?` (default true on create) and the detail returns it; new error 400 `rag.document_ids_not_supported` when none of the tool's KBs maps `documentId`.

**Chat:** no new route. `tool_call` / `tool_result` as in spec 004; the result `content` is what was sent to the model.

## Authorization
- **Permissions**: `knowledge_base.manage` for everything under `/knowledge-bases` (marking fields, lookups, manual lookups, refresh); `tools.manage`, `tools.attach` and `tools.use` for the lookup tool, as for rag. Held today only by `Admin`; endpoints use named policies, never role names.
- **Tenant isolation**: lookups, slices and values carry `TenantId` with the global filter; visibility of a KB's lookups follows the KB's scope (D-16).
- **Content access**: a lookup tool only sees what its slice allows (D-6); live operations always add the fixed filters in AND. A model can never widen the slice, with `document_ids` either (D-14).
- **Secrets and logs**: unchanged from spec 003; nothing new holds a credential.

## Edge Cases & Failure Modes
- **A listable field has no payload index**: the lookup is `Failed` with a message naming the field (D-8). A live filter on an unindexed field runs, slowly, up to 15 s, then ends as an error result that suggests a narrower filter.
- **Qdrant older than 1.12**: the facet API does not exist; registration warns, computed lookups of that KB are `Failed` with that reason, and live operations that do not need facets (documents, count) still work.
- **A computation fails or is interrupted** (store down, restart, budget exceeded): the previous generation stays current, the slice is `Stale` with the error, and the next trigger retries (D-10). A restart during a computation leaves no half-written generation: nothing becomes current outside the swap transaction.
- **No generation yet** (just registered, new slice): the tool answers that the lookup is being prepared (D-11); it never blocks waiting.
- **The mapping stops marking a field as listable**, or the field is unmapped: its lookup and its values are deleted, and a lookup tool that bound it ignores it (a tool left with nothing to offer is not offered, as in 003).
- **A KB is deleted**: its lookups, slices and values go with it (D-9); tools drop the missing KB or lookup.
- **Too many values** (more than the installation ceiling): the list keeps the values with the most documents, is `truncated`, and the result says so; the model must not present it as complete.
- **A value is longer than 200 characters, empty, or the field is a list with repeated values** in one document: long values are skipped and counted in the slice; empty values are ignored; a document counts once per distinct value.
- **Counting without a counted point and without `documentId`**: the unit is `chunks` and says so (D-2). A KB that has a `countedPoint` mapped in one KB and not in another, in a tool with several KBs, keeps each KB's unit; lookups are never summed across KBs.
- **The same value in two KBs of one tool**: shown once per KB; the lookup tool does not merge them (counts of different units or stores are never added).
- **Live `documents` without a counted point**: points are scrolled and de-duplicated by document id within the page, so a page may hold fewer documents than the limit; the cursor still advances. This bounded scan reads at most 1,000 points per call.
- **A fixed filter that changes**: the new slice is created and computed in the background; the old one is removed when no tool uses it. Until the new one is ready the tool answers "being prepared".
- **Too many slices** (more than 50 per KB): saving the tool is refused (`lookup_tool.too_many_slices`).
- **A scope narrowed later**: tools ignore lookups and KBs the tenant can no longer see, without revealing them (as 003 AC-44).
- **Prompt injection**: values come from the payload and are untrusted data in delimiters (D-13); a value cannot close the block.
- **Concurrent refresh**: a second refresh of the same KB while one runs is refused with 409; the scheduled one skips.
- **Caching**: a failing cache never fails a read (D-11). The memory used is bounded by the ceiling of D-9 times the slices in use.
- **Installation with several instances**: every instance holds its own cache; the generations in the tables keep them consistent. Only one instance runs the scheduled computation of a KB (a row lock on the slice).

## Non-Functional Requirements
- **Evidence** (appendix, Qdrant 1.12.0, 5 million points, payload on disk): with payload indexes, filtered scrolls, `order_by` with a range index and selective counts stay under 3 ms; a facet on a field takes 4 ms approximate and 172 ms exact; an exact facet by `supplier` under `is_first_chunk` takes 135 ms; a facet by `document_id` takes 3.4 s exact (6 s worst). Without an index on a field of the filter: 4 to 6 s for a facet and 12 to 15 s for an exact count; the facet on an unindexed key is refused by Qdrant.
- **Reading a lookup** (cache or table) is bounded by the page size (200) and has no target beyond "no store call": the benchmark is the evidence for the background cost, not a latency promise.
- **Computation** is bounded by a time budget (10 minutes by default, configurable), 5 million points scanned, 2 concurrent computations in the installation and one per KB (the last two fixed).
- **Memory**: the cache holds at most the ceiling of values per slice in use; no target beyond that.
- **No outbound telemetry**; the only new outbound traffic is to the Qdrant servers already registered.
- Libraries: `HybridCache` (Microsoft.Extensions.Caching.Hybrid) and the existing Qdrant client; the plan checks that the pinned Qdrant client exposes the facet API.

## Integrations
- **Qdrant**: facet, scroll with filter and `order_by`, count with `exact: true` (1.12 or later for facet).
- **Agents**: the lookup tool as origin `Lookup` in spec 004's pool, activation, policy and audit; the `allowDocumentIds` switch on the rag tool.
- **PostgreSQL (platform)**: tables `lookups`, `lookup_slices`, `lookup_values` in `knowledge_base`; tables `lookup_tools` in `agents`; a column on `agents.rag_tools`; new columns inside the `Mapping` JSON of `knowledge_bases`. Migrations are reversible. `docs/database-schema.md` is updated in the same PRs.
- **Spec 008 (pgvector) and 009 (Pinecone)**: this spec **depends on the per-kind contract of 008 D-1**. Its first PR (Qdrant moved behind the contract, no change of behaviour) lands before the backend PRs of this spec, which add the optional *lookups* capability to the contract (decided 2026-10-22). Pinecone follows 008.
- No events published.

## Acceptance Criteria
**Mapping and lookups**
- **AC-1** Given a KB whose mapping marks `supplier` as listable, When it is saved, Then a computed lookup `supplier` exists with a whole-KB slice in `Pending`, and the computation starts in the background without delaying the response.
- **AC-2** Given a field of type Number, Boolean or Date marked as listable, When saving, Then it returns 400 `knowledge_base.invalid_listable_field`; given a `countedPoint` whose value does not fit the field's type, 400 `knowledge_base.invalid_counted_point`.
- **AC-3** Given a KB of a kind without lookups, When a field is marked listable, Then it returns 400 `knowledge_base.lookups_not_supported`.
- **AC-4** Given a `countedPoint` mapped, When a lookup is computed, Then only the points that satisfy it are counted and the unit is `documents`; given no counted point but a `documentId`, Then distinct document ids are counted; given neither, Then chunks are counted and the unit is `chunks`.
- **AC-5** Given a computed lookup, When it is read, Then the values are exactly those of the payload, with no case folding or trimming, ordered by count then value.
- **AC-6** Given more values than the installation ceiling, When computed, Then the values with the most documents are kept and the slice is `truncated`; the ceiling is read from `Quorum:Lookups:MaxValuesPerLookup`.
- **AC-7** Given a listable field with no payload index, When computed, Then the slice is `Failed` with a message naming the field; given the index is created and the lookup refreshed, Then it becomes `Ready`.
- **AC-8** Given a Qdrant older than 1.12, When the KB is registered, Then a warning says that lookups need 1.12; computed lookups fail with that reason.

**Entity and manual lookups**
- **AC-9** Given a KB with `entityName` mapped and a lookup of kind `Entity` with three attributes, When read, Then it lists the entities with those attributes and can be sorted by an attribute of type number; more than 10 attributes returns 400.
- **AC-10** Given a manual lookup with `IT` and the synonyms `Italy` and `italiana`, When a tool serves it, Then the model sees `IT` with its label and synonyms, and a `search` for `italiana` finds it.
- **AC-11** Given a manual lookup of scope `Tenant` for tenant A, When tenant B lists, Then it does not see it; a name already used returns 409 `lookup.name_taken`; more than 5,000 values returns 400 `lookup.too_many_values`.
- **AC-12** Given a manual lookup used by a tool, When deleting it, Then it returns 409 `lookup.in_use` listing the tools.

**Computation, storage and cache**
- **AC-13** Given a successful computation, When the new generation becomes current, Then readers see either the old or the new list entirely, never a mix, and the old generation is deleted.
- **AC-14** Given a computation that fails, When it ends, Then the previous generation is still served, the slice is `Stale` with the error, and the next trigger retries.
- **AC-15** Given a KB with a refresh interval of 24 hours, When the interval passes, Then a computation starts without any request; the interval can be set between 1 hour and 7 days, and out-of-range values return 400.
- **AC-16** Given the admin presses Refresh now, When a computation of that KB is running, Then it returns 409 `lookup.refresh_running`; otherwise 202.
- **AC-17** Given an installation restart, When it comes back, Then lookups with a current generation are served from the tables at once, the cache warms in the background, and nothing is recomputed before its scheduled time.
- **AC-18** Given the cache fails or is empty, When a lookup is read, Then the tables answer and the read does not fail.
- **AC-19** Given a lookup with no generation yet, When the tool is called, Then the model gets a result saying it is being prepared and the call does not wait for the computation.
- **AC-20** Given a KB is deleted, When the deletion completes, Then its lookups, slices and values are gone, and a tool that bound them no longer offers them.

**Slices**
- **AC-21** Given two tools with the same fixed filters in different order, When both are saved, Then they share one slice.
- **AC-22** Given a tool with the fixed filter `department = hr`, When it serves `supplier`, Then the list holds only the suppliers of documents of that department, with counts of those documents; a tool with no fixed filters sees the list of the whole KB.
- **AC-23** Given a tool is saved with a new fixed filter, When it is saved, Then the slice is computed in the background, and the tool answers "being prepared" until it is ready.
- **AC-24** Given no tool uses a slice any more, When the tool is changed or deleted, Then the slice and its values are deleted.
- **AC-25** Given a KB already has 50 slices, When a tool would add one more, Then saving returns 400 `lookup_tool.too_many_slices`.
- **AC-26** Given a KB that does not map the field of a slice's filter, When the tool is called, Then that KB is skipped and named, never listed without the filter.

**The lookup tool and the live operations**
- **AC-27** Given a lookup tool with two lookups and one KB, When its definition is generated, Then it has `operation`, `lookup` (enum of the two names), `search`, `sort`, `limit`, `cursor` and the filter parameters of the exposed fields; with no KB, `documents` and `count` are not offered.
- **AC-28** Given `operation = values`, When called, Then the result carries the lookup name, the unit, the number of values, the `as of` timestamp, `stale` and `truncated`, then the rows, and fits the result cap with whole rows only.
- **AC-29** Given `operation = documents` with the filter `audience = managers`, When called, Then it returns the matching documents (id, title, URL and exposed fields) in pages of 50 with a cursor, each document once.
- **AC-30** Given `operation = count`, When called, Then the number is exact, the unit is stated, and the fixed filters of the tool are included.
- **AC-31** Given `operation = values` with a filter, When called, Then the values are computed live among the matching documents, exact, within the 15 s timeout; it works for a computed lookup only and returns an error result for a manual or entity one.
- **AC-32** Given a live filter on a field without payload index, When the tool is configured, Then a warning says it may be slow; When called, Then it runs until the timeout and the result suggests a narrower filter.
- **AC-33** Given an invalid filter value or an unknown lookup, When called, Then the model receives an error result and the round counts (spec 004 D-11).
- **AC-34** Given a value that contains the delimiter or instructions, When it is sent to the model, Then the block cannot be closed from inside and it is labelled as data.
- **AC-35** Given a lookup tool call, When it runs, Then an audit entry is written before it (fail closed, spec 004 D-28) with the number of values or documents returned.

**`document_ids` on rag tools**
- **AC-36** Given a new rag tool, Then `allowDocumentIds` is on and its definition has `document_ids`; given a rag tool saved before this spec, Then it is off and its definition is unchanged.
- **AC-37** Given `document_ids` with a list, When the model calls the tool, Then only chunks of those documents are searched, in AND with the fixed filters; more than 50 ids is an error result.
- **AC-38** Given a fixed filter `department = hr` and `document_ids` of a finance document, When called, Then nothing from that document is returned.
- **AC-39** Given a KB that does not map `documentId`, When called with `document_ids`, Then it is skipped and named in the result; Given none of a tool's KBs maps it, When saving with `allowDocumentIds`, Then it returns 400 `rag.document_ids_not_supported`.

**Authorization, tenants and logs**
- **AC-40** Given a user without `knowledge_base.manage`, When calling any `/knowledge-bases` lookup route, Then it returns 403; likewise `tools.manage` for `/tools/lookup`; unauthenticated returns 401.
- **AC-41** Given tenant A's KB and lookups, When tenant B reads them, Then B sees nothing unless the scope includes B; for a Global KB it sees the values through a tool and never the connection.
- **AC-42** Given any computation or call, When the logs are inspected, Then no value, query, title or credential appears.

**UI and documentation**
- **AC-43** Given the admin pages, When registering a KB, marking listable fields, the counted point and the entity name, managing manual lookups, refreshing, and configuring a lookup tool with a preview of its definition, Then everything can be done without touching files; the rag tool editor has the `allowDocumentIds` switch.
- **AC-44** Given the guide for data owners and the sample project, Then they cover indexes, the counted point and the entity collection, and the sample collections let each case be registered and queried; `docs/deferred.md` and `docs/database-schema.md` are up to date.

## Deferred Decisions
| Decision | Fallback chosen now | Revisit trigger |
|---|---|---|
| An endpoint for the data owner's ETL to refresh lookups, with a service token per KB | A refresh every 24 hours and the Refresh now button (D-10) | Data that changes faster than the interval, or ingestion that ends at known times |
| Lookups for `PgVector` and `Pinecone` KBs | Qdrant only; those kinds report no support (D-15) | A customer on those kinds; pgvector is a `GROUP BY` |
| Normalising values or a synonym table for computed lookups | Values as stored; synonyms only in manual lookups (D-3) | Data with many spellings of one value and no ingestion fix |
| A cache shared between several instances | In-memory `HybridCache`; the tables keep instances consistent (D-11) | A deployment with several instances and a cold-start cost that matters |
| Sums, averages, joins and free queries | Out of scope | External SQL sources, with named parametrised queries rather than free text-to-SQL |
| Renaming a lookup or moving it between KBs | A lookup follows its field and KB | A concrete need |

## Open Questions
Answered in review on 2026-10-22: the order with spec 008 (008's abstraction first, see Integrations), the ceiling of values (a configuration key, D-9), the computation limits (the time budget is configurable, the scanned points and concurrency are fixed, D-10), manual lookups (standalone, D-5) and the counted point (a boolean or a field equal to a value, D-2, D-19).

The last one was `values` with filters (AC-31): confirmed, computed live among the matching documents, for computed lookups only. A model that listed the HR contracts with `documents` would see about 45 per page and at most 8 pages per answer (spec 004 D-8), so it could not derive the suppliers of 600 contracts.

_None._

## Amendments

### A1 - One function for each thing a lookup tool does (2026-10-23)
Context: asked real questions through a model, the single `lookup__<name>` tool made the model choose between `values`, `documents` and `count` with an `operation` argument, and every definition carried the arguments of all three.

Decision: a lookup tool is still **one configuration, one tool row, one activation, one policy and one audit identity** (`ToolId`), but the model is offered up to three functions, named `lookup_values__<name>`, `lookup_documents__<name>` and `lookup_count__<name>`. They are made when the answer starts, like the definition itself (D-12):
- **`lookup_values`** is always offered. Arguments: `lookup`, `search`, `sort`, `limit`, `cursor`, and the filters when the tool queries knowledge bases (a computed list is then worked out among the matching documents, D-7). It lists the lists with what each is.
- **`lookup_documents`** and **`lookup_count`** are offered only when the tool queries at least one knowledge base. Arguments: `knowledge_base` (with several), the filters, and for `documents` also `limit` and `cursor`. They list the knowledge bases, not the lists.
- A filter on a field that one of the lists holds says, in the description of the function, that its exact values are in that list and which function reads it; a `documents` or `count` that matches nothing points to the list.
- The `operation` argument is gone. The audit log records the tool as `<name>.<operation>` (server `lookup`). The context a tool takes is the sum of its functions.
- The tool API keeps `definition` (the `values` function) and adds `definitions` (all of them, each with its size). The tool pool lists the tool once, as `lookup__<name>`, and its activation covers its functions.

Alternatives not taken: three tool rows (three activations and policies to keep in step for one configuration), and a policy per function (deferred: no case yet).

Also, from the same questions: a list of entities can be ordered by its number and date attributes (offered in `sort`); a list of entities cannot be filtered, so the fields only its knowledge base maps are not offered as filters; and each list says what it is (values that occur, entities, typed by hand).

## Appendix: cost evidence
Benchmark run on 2026-10-22 with `tools/Quorum.QdrantMetadataBench` (a throwaway tool of the investigation, not part of the product): Qdrant 1.12.0, 5,000,000 points of 8-dimension vectors, payload on disk, about 10 chunks per document, `supplier` with about 5,000 values. Medians in milliseconds.

| Scenario | No index | Facet key only | All fields indexed |
|---|---|---|---|
| Facet `supplier`, approximate / exact | refused | 3.3 / 188 | 3.8 / 172 |
| Facet `supplier` where `is_first_chunk`, approximate / exact | refused | 4,375 / 6,509 | 18 / 135 |
| Facet `department` where nationality = IT, approximate / exact | refused | 4,487 / 5,513 | 26 / 139 |
| Facet `document_id` where `is_first_chunk`, approximate / exact | refused | refused | 280 / 3,379 (worst 6,068) |
| Scroll with a filter, any page | 2 to 3 | 2 to 3 | 1 to 3 |
| Count, approximate, any filter | 0.7 | 0.8 | 0.5 |
| Count, exact, no filter | 474 | 441 | 419 |
| Count, exact, nationality = IT | 11,776 | 11,905 | 143 |
| Count, exact, nationality = IT and `is_first_chunk` | 15,102 | 14,430 | 51 |
| Scroll with `order_by` on a number | refused | refused | 2.1 |

Findings used in this spec: an index on every field the query touches is the whole difference (D-8); an approximate count without an index returned 500,000 where the real number was 1,333, so live counts are always exact (D-7); `is_first_chunk` makes counts per document and about three times cheaper, but only with its own index (D-2); grouping by `document_id` is the one expensive pattern, acceptable only in the background (D-10); exact and approximate facets returned the same top values on this data.
