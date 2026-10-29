# Spec 012: Lookup Consolidation and Lean Manifest

**Status:** Approved (2026-10-29); Amendment 1 Approved (2026-10-29)
**Date:** 2026-10-29

## Problem
Spec 007 gave the model lists, counts and document lists from metadata. Used with real models, it shows three problems:

1. **The manifest is large.** A lookup tool offers up to three functions (007 A1), each with the filters of every exposed field, the enum of the lookups, `sort`, `limit` and `cursor`. The size is paid in every request and grows with every tool the assistant has. Small models lose context to it and choose badly among many parameters.
2. **The kinds of lookup do not fit together.** An entity lookup (a vendor or an employee, with attributes) is filtered live against the store by scanning (007 D-7, A1), although its rows are already in the platform database. A manual lookup is a list of values with no attributes, although a CSV of vendors with a rating is the same thing as an entity lookup without a store. Only one of the two can be filtered, and the filter changes with the store.
3. **Roles are blurred.** Entity KBs are asked to behave like documents for RAG, when what the model needs from them is a table it can filter and read ("the rating of ACME", "the vendors of Italy"), and a canonical value to pass as a filter to a RAG search.

This spec settles the roles, unifies the entity and manual lookups on one model with one query engine, lets the admin choose where an entity lookup is read (the snapshot or the store), and adds an optional **lean** manifest for lookup tools, chosen per assistant, in which the model asks for the details of a list or a KB only when it needs them.

## Scope
### In
- **Roles** of RAG, lookups and the document operations (D-1).
- **One lookup model**: name and description on every lookup, typed attributes on the lookup, one table of rows for entity and manual lookups (D-2, D-3).
- **Storage mode** `Snapshot` or `Live` per lookup, and filtering of entity and manual rows in the platform database (D-4, D-5, D-6).
- **Fixed filters and exposure** rule for lookup tools (D-7).
- **Lean manifest** for lookup tools: a setting of the assistant-tool activation, `describe` and `query` (D-8, D-9).
- Migrations of existing lookups and tools (D-10).
- Admin UI in separate PRs after the backend: one page for search tools with a RAG tab and a Lookup tab, one table of lookups, the manifest toggle (D-12).

### Out
- Lookups for new kinds of store. Every kind that declares the lookups capability (`ILookupsCapability`) gets everything here: Qdrant and PgVector (008 A2) today. Pinecone does not declare it and stays as it is (D-11).
- A lean manifest for rag, web and MCP tools, and a generic mechanism in the tool pool of spec 004.
- Joins between sources, sums and averages (`docs/deferred.md`).
- A description of a lookup that differs from tool to tool (deferred decisions).
- Free-text search on attributes other than the name of the row.

## Decisions
Decisions of specs 003, 004, 007 and 008 not listed here stand. This spec amends 007 D-1, D-4, D-7, D-12, A1 and A2 where it says so.

| # | Decision |
|---|---|
| D-1 | **Roles.** RAG is a semantic search over the chunks of document KBs, with metadata filters. A **list** gives the canonical values to pass as a filter to a RAG search. An **entity lookup** is a reference table (vendors, employees) with typed attributes: it resolves a name to the value to use as a filter and answers questions about attributes ("the rating of ACME", "the vendors of Italy"). `documents` and `count` stay live operations over the documents of a KB ("how many contracts with ACME"). A KB is not declared "of documents" or "of entities": the roles its mapping sets decide what can be done with it (`text` lets a rag tool search it, `entityName` lets an entity lookup be made). One KB can be both. |
| D-2 | **One lookup model.** Every lookup has a **name** (unique per tenant across the three kinds; the default of a computed list is `<kb>.<field>` and the default of an entity lookup is `<kb>`, so that two KBs never collide, and a rename that collides returns 409) and a **description** for the model. The description is always present: for a computed list and for an entity lookup the system writes a default when the lookup is created (for instance "Distinct values of *country* in *Contracts*", "Entities of *Vendors*") and the admin can edit it; for a manual lookup it is asked on creation. The description belongs to the lookup, so a tool that binds it never rewrites it. The alias per tool of 007 stays. |
| D-3 | **Attributes.** The **attributes** of an entity or manual lookup are defined on the lookup: `{ key, label?, type, dictionaryFieldId? }`, at most **10**, with `type` one of `text`, `number`, `date`, `textList`. For a lookup of entities the attribute is a mapped dictionary field (007 D-4) and `dictionaryFieldId` is set; for a manual lookup it is declared by the admin. A computed list has no attributes: value and count only. A manual lookup imported from a CSV treats the columns after the value (and the optional label and synonyms) as attributes: the import step proposes a type for each column from the data and the admin confirms or corrects it; a row with a value that does not fit its type fails the whole import and the answer names the row and the column. The definitions that today live inside the `Mapping` JSON of the KB for entity lookups move to the lookup (D-10). |
| D-4 | **Storage mode.** An entity lookup has `Storage = Snapshot` (default) or `Live`. **Snapshot**: the rows are read from the platform database as 007 D-9 (the same generations, cache and refresh). **Live**: nothing is kept; the rows are read from the store with the `scan` of the lookups capability, within the 15 s timeout of 007 D-7. A manual lookup is always `Snapshot` (it is the source), a computed list is always `Snapshot` for its slices (007 D-6) and keeps computing live the values asked with filters of the model (007 D-7). Switching an entity lookup to `Live` **deletes its rows and generations** at once, so that no copy of the data remains; switching back computes the snapshot again in the background. |
| D-5 | **Reading the rows of an entity or manual lookup.** The same operations whatever the mode: the rows (key, label, attributes), a **count** of the rows that match, `search` (substring of the name, case-insensitive), `sort` (the name, or an attribute of type `number` or `date`; `count` for a computed list), `limit` (default 50, maximum 200), `cursor`, and **filters on attributes** with these operators: `eq` and `in` (all types; on `textList` they mean "contains this value" and "contains any of"), `min` and `max` (`number` and `date`). In `Snapshot` mode they run in SQL on the platform database; in `Live` mode the store evaluates them, `sort` is offered only if the store can order (the description of the lookup says so) and a request that cannot be served is an error result that suggests `Snapshot`. This amends 007 D-7 and A1: a filter of the model on an entity lookup is no longer always computed against the store. |
| D-6 | **Ceiling of rows.** The same as 007 D-9: `Quorum:Lookups:MaxValuesPerLookup` (default 5,000, maximum 50,000). Beyond it the snapshot is `truncated` as today. |
| D-7 | **Fixed filters and exposure.** A fixed filter of a tool on a field (007 D-6; or, on an attribute of an entity or manual lookup, a condition on its rows) fixes that field for the tool. If the fixed filter has a **single value**, the field is **not offered** to the model: saving a tool that exposes it is refused (`lookup_tool.field_fixed`), and a tool saved before this spec that does so is served without that parameter and the editor warns. If the fixed filter has **several values**, the field is offered with an enum limited to those values, so the model can narrow it but never widen it. A fixed filter on an entity or manual lookup is a condition on the rows, with no slice to compute. The fixed filters on dictionary fields (`fixedFilters`) stay as in 007 for KBs and computed lists; for entity and manual lookups a tool gains `lookupFilters: { "<lookupId>": { "<attributeKey>": value } }`, because a manual attribute is not a dictionary field. The values of `lookupFilters` follow the operators of D-5 (`eq`, `in`, `min`, `max`). |
| D-8 | **Lean manifest.** Every activation of a lookup tool on an assistant has a `manifest` setting, `full` (default, the functions of 007 A1 unchanged) or `lean`. In `lean` the model is offered: (a) the **description of the tool**, which lists the lookups and KBs it binds, each with its name and its description; (b) **`describe(targets[])`**, which takes one or more names and returns JSON with, for each: its kind (list, entities, manual, KB), the operations it supports, the filterable fields with type and operators, the sortable fields, the limits, the restriction of the tool's fixed filters (D-7) and, for a field with allowed values, **the name of the list that holds them, never the values** (a field with three values costs one more call); (c) **`query(target, operation, filters?, search?, sort?, limit?, cursor?)`**, with a small fixed schema. The operations are those `describe` names for the target: `values` (the rows of a list or lookup) and `count` (the rows that match) for a lookup, `documents` and `count` (the documents that match) for a KB. Filters are an object keyed by field or attribute, validated by the server against the target; an invalid call returns an error result with the part of the schema of that target that applies, so a model that skipped `describe` can correct itself. Results keep the format and the cap of 007 D-13 (6,000 characters, whole rows only). |
| D-9 | **Rounds and audit in lean.** A round of `describe` counts in the limit of rounds of the answer (spec 004 D-11); `describe` takes several targets so that one round is enough. The audit log records `<name>.describe` and `<name>.<operation>` (server `lookup`), as in `full`. The two modes share the tool row, the policy and the result format; only what the model is offered changes. |
| D-10 | **Compatibility.** All schema changes are additive and reversible. A tool saved before this spec stays `full`. The entity lookups configured before this spec keep working: a migration moves the chosen attributes from the `Mapping` JSON to the lookup, sets `Storage = Snapshot`, writes the default description and name, and the existing generations stay valid. `docs/database-schema.md` is updated in the same PRs. |
| D-11 | **Stores.** Everything here goes through `ILookupsCapability`. A KB whose kind declares it gets snapshots, `Live`, attributes and filters with no work for the kind. The `scan` of a kind must return the attributes with their type (number, date, list of texts); if it does not, the plan fixes the adapter (an amendment to the spec of that kind if it changes behaviour). A kind without the capability keeps the refusal `knowledge_base.lookups_not_supported`. Pinecone is not changed. |
| D-12 | **Admin UI.** One page for search tools with two tabs, **RAG** and **Lookup**; the editor of the lookup tool moves there with the same layout. The data of the lookups (the lists, the entity and manual lookups) stay in the knowledge base pages. One table lists the three kinds with badges for the origin, the storage mode and the freshness (`Ready`, `Stale`, `Preparing`, `Failed`). `describe` has no page of its own: the activation of the tool on the assistant has a Full / Lean switch, and the editor of the tool shows the estimated tokens of both manifests side by side. The design follows spec 010. |

## Domain Model
All entities carry `TenantId`. Lookups live in schema `knowledge_base`, tools and activations in `agents`.

| Entity | Module | Changes | Lifecycle |
|---|---|---|---|
| **Lookup** | KnowledgeBases | `Name` for every kind (was `Manual` only); `Description` always present; `Storage` (`Snapshot` / `Live`, entity only); `Attributes` (jsonb, the definitions of D-3) | A computed or entity lookup keeps its default name and description until edited; deleting a manual lookup is refused while a tool uses it (`lookup.in_use`) |
| **LookupValue** | KnowledgeBases | `Attributes` (typed values) allowed for `Manual` too; indexes for filters and sort on attributes are decided in the plan | Written in generations as today; removed at once when an entity lookup becomes `Live` |
| **AssistantTool** | Agents | `Manifest` (`Full` / `Lean`), default `Full`; meaningful only for origin `Lookup` | Removed with the tool or the assistant |

Lookup slices (007) are unchanged and apply to computed lists only; an entity or manual lookup has exactly one.

## API Contract
Errors use ProblemDetails with a stable `code`; routes are under `/api`. Permissions as in 007: lookups under `/knowledge-bases` need `knowledge_base.manage`, `/tools/lookup` needs `tools.manage`, activation `tools.attach`.

**Lookups** (changes to the routes of 007)

| Verb | Route | Change | Errors |
|---|---|---|---|
| GET | `/knowledge-bases/{id}/lookups`, `/knowledge-bases/manual-lookups` | Rows gain `name`, `description`, `storage`, `attributes[]` | - |
| PUT | `/knowledge-bases/lookups/{lookupId}` (new) | `{ name?, description?, storage?, attributes? }` | 400 `lookup.invalid_name` / `lookup.invalid_attribute` / `lookup.storage_not_supported`; 404; 409 `lookup.name_taken` / `lookup.attribute_in_use` (a tool uses it in a filter or an exposed field) |
| GET | `/knowledge-bases/lookups/{lookupId}/values` | New query parameters `filters` (JSON: `{ "<key>": { "eq": v } \| { "in": [v] } \| { "min": v, "max": v } }`); `sort` accepts an attribute key; values return `attributes` typed | 400 `lookup.invalid_filter` |
| POST | `/knowledge-bases/manual-lookups` and `PUT .../{id}` | Body gains `attributes[]` and `values[].attributes` | 400 `lookup.invalid_attribute` / `lookup.invalid_value` |
| POST | `/knowledge-bases/manual-lookups/import-preview` (new) | `multipart` CSV, returns the detected columns with a proposed type and the first rows | 400 `lookup.invalid_csv` |

**Lookup tools:** `POST` and `PUT /tools/lookup` gain `lookupFilters?` (D-7), with 400 `lookup_tool.invalid_filter` for an unknown attribute or a value that does not fit its type. `GET /tools/lookup/{id}` returns `definitions: [{ manifest, functions, estimatedTokens }]` for `full` and `lean`; `POST` and `PUT` reject an exposed field that a single-valued fixed filter fixes with 400 `lookup_tool.field_fixed`.

**Activation:** `PUT /assistants/{id}/tools` accepts `{ toolIds[], manifests?: { "<toolId>": "full" | "lean" } }`; a tool not in the map keeps `full` (or its current value). `GET /assistants/{id}/tools` returns `manifest` for each tool of origin `Lookup`. Error: 400 `tool.manifest_not_supported` for a tool that is not a lookup tool.

**Chat:** no new route. In `lean` the model calls `lookup_describe__<name>` and `lookup_query__<name>`; `tool_call` and `tool_result` as in spec 004.

## Authorization
- Lookups, their storage mode and their attributes: `knowledge_base.manage`. Scope and tenant visibility are those of 007 D-16, unchanged.
- The `manifest` of an activation: `tools.attach`, as the activation itself.
- `describe` and `query` run with `tools.use` and see only what the tool's scope and fixed filters allow; a target outside the tool is an error result that names the valid targets.

## Edge Cases & Failure Modes
- **`describe` or `query` on a target that does not exist or is not bound**: error result with the list of valid targets; the round counts (spec 004 D-11).
- **A snapshot still preparing or `Failed`**: "being prepared" in plain words (007 D-11, D-13); `describe` says the target is not ready.
- **A filter on a field that does not exist, an operator not valid for its type, or a value that does not fit its type**: error result with the part of the schema of that target that applies.
- **A live filter that goes over 15 s**: error result that suggests a narrower filter or, for an entity lookup, `Snapshot`.
- **An attribute removed or retyped while a tool filters on it or exposes it**: refused with 409 `lookup.attribute_in_use`, listing the tools.
- **A lookup that changes from `Snapshot` to `Live` while a tool is reading it**: the next read goes to the store; an answer in progress finishes with what it already read.
- **A CSV with more than 10 attribute columns, a duplicate key or too many rows**: refused as in 007 for manual lookups, naming the cause.
- **Rows above the ceiling**: `truncated`, in the result and in `describe`.
- **A fixed filter that hides a field that the model already used in earlier turns**: the call is rejected with the schema of the target; there is no error for the conversation.
- **A deleted KB**: its lookups, rows and attribute definitions go with it (007 D-17); a tool that bound them is served without them and the editor warns.

## Non-Functional Requirements
- Volumes are those of 007: 5,000 rows by default, 50,000 at most. A snapshot query on attributes is served by the platform database with the indexes the plan chooses; no new latency target is set here, the plan measures it on the sample project.
- `describe` and the error schemas are bounded by the result cap of 007 D-13.
- The lean manifest has to be **smaller than the full one** on the sample project: the plan measures `estimatedTokens` of both with the sample lookups (AC-24).
- The audit log never holds values, as in 007 D-17.

## Integrations
- Nothing new outside Quorum. The KnowledgeBases module exposes the new reads through `IKnowledgeBaseLookups` in its Contracts project (007); the Agents module builds the lean functions from them and never reads lookup tables.
- No event is published.

## Acceptance Criteria
**Model and storage**
- **AC-1** Given a computed list or an entity lookup created without a description, When it is read, Then it has the default description of D-2; given a manual lookup created without one, Then it returns 400.
- **AC-2** Given two lookups of different kinds with the same name in a tenant, When the second is created, Then it returns 409 `lookup.name_taken`.
- **AC-3** Given an entity lookup with three attributes (`country` text, `rating` number, `since` date), When the rows are read with `filters = { country: { in: [IT, DE] }, rating: { min: 4 } }` in `Snapshot` mode, Then only the matching rows return, sorted as asked, in pages, and the query runs in the platform database.
- **AC-4** Given the same lookup in `Live` mode, When the same filter is read, Then the store evaluates it and the rows match; given a `sort` the store cannot serve, Then the result is an error that suggests `Snapshot`.
- **AC-5** Given an entity lookup in `Snapshot` mode with generations, When the admin switches it to `Live`, Then its rows and generations are deleted at once and a read goes to the store; switching back starts a background computation.
- **AC-6** Given a manual lookup, When `storage = Live` is requested, Then it returns 400 `lookup.storage_not_supported`.
- **AC-7** Given a CSV with a value, a label and three attribute columns, When imported, Then the preview proposes a type per column and the lookup holds the rows with typed attributes; given a value that does not fit its column's type, Then the import fails naming the row and the column and nothing is saved.
- **AC-8** Given more than 10 attributes, When saving, Then it returns 400 `lookup.invalid_attribute`.
- **AC-9** Given rows above `MaxValuesPerLookup`, When the lookup is read, Then it is `truncated`.

**Fixed filters**
- **AC-10** Given a tool with a fixed filter `department = hr`, When it is saved with `department` exposed, Then it returns 400 `lookup_tool.field_fixed`; given a tool saved before this spec in that state, Then its definition has no `department` parameter and the editor warns.
- **AC-11** Given a fixed filter `department in [hr, finance]`, When the definition is built, Then `department` is offered with an enum of those two values and a call with `legal` returns an error result; given a call with `hr`, Then the result is limited to `hr`.
- **AC-12** Given `lookupFilters = { <vendorsId>: { country: { in: [IT] } } }` on an entity or manual lookup, When the lookup is read through the tool, Then only matching rows return, no slice is created, and `country` is not offered to the model; given an unknown attribute, Then saving returns 400 `lookup_tool.invalid_filter`.
- **AC-13** Given an attribute used by a tool in a fixed filter or an exposed field, When the admin removes it, Then it returns 409 `lookup.attribute_in_use` listing the tools.

**Lean manifest**
- **AC-14** Given an activation with `manifest = lean`, When the model is offered the tool, Then it has the description of the tool listing its lookups and KBs with name and description, `lookup_describe__<name>` and `lookup_query__<name>`, and none of the `full` functions.
- **AC-15** Given `describe(["vendors", "contracts"])` for an entity lookup and a KB, When called, Then one result returns, per target, the kind, the operations, the filterable fields with type and operators, the sortable fields, the limits and the restriction of the fixed filters; no field lists its values, and a field with allowed values names the list that holds them.
- **AC-16** Given `query` with a valid target and operation, When called, Then the result has the format and the cap of 007 D-13 and the audit log records `<name>.<operation>`; `describe` is recorded as `<name>.describe`.
- **AC-17** Given `query` with an unknown filter field, an operator not valid for the type or a target not bound to the tool, When called, Then the model receives an error result with the part of the schema of that target that applies, and the round counts.
- **AC-18** Given a `describe` call, When the answer counts its rounds, Then it counts as one round whatever the number of targets.
- **AC-19** Given an activation, When `PUT /assistants/{id}/tools` has no `manifests`, Then the tools keep their current manifest; given `manifests` for a tool that is not a lookup tool, Then it returns 400 `tool.manifest_not_supported`.
- **AC-20** Given a tool saved before this spec, When read, Then it is `full` and behaves as before.
- **AC-21** Given `GET /tools/lookup/{id}`, When read, Then `definitions` holds `full` and `lean` with an `estimatedTokens` each.
- **AC-22** Given a target that is a computed list, When described, Then it lists value, count, `search`, `sort` (name, count) and no attributes; the filters of the KB that restrict it are named; given `query` with `operation = count` on it, Then the number is of the values that match `search` and the filters, not of documents.
- **AC-28** Given two KBs that both have a listable field `country`, When their lists are created, Then they are named `<kb1>.country` and `<kb2>.country`; given a rename to a name already in use, Then it returns 409 `lookup.name_taken`.
- **AC-23** Given a KB whose kind does not declare the lookups capability, When bound to a tool, Then it is skipped and named, as in 007.

**Measure**
- **AC-24** Given the sample project with a tool binding three lookups and two KBs, When both manifests are generated, Then the estimated tokens of `lean` are lower than those of `full`, and the figures are shown in the editor and written in the plan.

**Migration and stores**
- **AC-25** Given entity lookups configured before this spec, When the migration runs, Then their attributes are on the lookup, `Storage = Snapshot`, name and description are set, the generations stay current and the migration can be reverted.
- **AC-26** Given a PgVector KB with `entityName`, When its entity lookup is made, Then snapshots, `Live`, attribute filters and the lean manifest work as for Qdrant, with the scan returning typed attributes.
- **AC-27** Given the admin, When using the page of search tools, Then the RAG and Lookup tabs exist, one table lists the three kinds with their badges, and the Full / Lean switch and the token comparison are available, all without touching files.

## Deferred Decisions
| Decision | Chosen fallback | Revisit when |
|---|---|---|
| A description of a lookup per tool | The description of the lookup is used everywhere; the alias per tool of 007 stays | A real case needs different wording for the same list |
| A lean manifest for rag, web and MCP tools | Full manifests only | Small models show the same problem with those tools |
| Entity snapshots for Pinecone | Pinecone does not declare the capability | Its scan is good enough to fill a snapshot |
| Text search on attributes other than the name | Not offered | A case needs it |
| Indexes on the attributes of `lookup_values` | The plan picks them (for instance a GIN index on the jsonb) and measures them on the sample project with 50,000 rows | The measure shows a query over 1 s |

## Open Questions
None.

## Amendment 1: roles of a KB in a tool, one namespace, a `describe` that states only what applies (spec 012)
Used with real assistants, the lean manifest (D-8) shows that the targets of a lookup tool are not told apart well enough. A tool can bind a lookup and the knowledge base it comes from under the same name (an entity lookup is named `<kb>`, D-2), and `describe` and `query` resolve the lookup first, so the KB cannot be reached. `describe` lists the filters of the whole tool on every knowledge base, whether that KB maps them or not, and it lists no filter at all for the attributes of an entity lookup, which D-5 makes filterable. The tool editor asks for the lists and, separately, for the "knowledge bases to ask live", so the same KB is chosen twice and an entity KB can be offered as a document source it was never meant to be.

This amendment fixes the roles a KB can have in a tool, so that a KB is never both an entity lookup and a document target of the same tool, gives the targets one namespace, and makes `describe` state, for each target, only what that target applies. It amends D-1, D-8, D-10 and D-12, the acceptance criteria AC-14, AC-15, AC-22 and AC-27, and the function parameter of spec 007 A1 that names the knowledge bases asked live.

### Scope
**In:** the roles of a KB in a tool, the live KBs of a tool, one namespace of targets, the content of `describe`, the single selector of the editor, the guard on mappings, the removal of the migration.
**Out:** moving entities out of the KB model (an entity lookup keeps reading the KB's connection, mapping and snapshots), the layout of the editor beyond the single selector, any change to the full manifest's per-function filters (A1-8).

### Decisions
| # | Decision |
|---|---|
| A1-1 | **Roles of a KB in a lookup tool.** A KB is a **document KB** when its mapping sets `text` or `documentId`, and an **entity KB** when it sets `entityName`; it can be both. Only a document KB can be the live KB of a tool (`documents`, `count`). An entity KB that is not a document KB gives lookups only. A KB that has neither role gives computed lists only. A KB that is both is a live KB or the source of an entity lookup in a tool, never both (A1-3). |
| A1-2 | **Live KBs of a tool.** A tool can have several live KBs, all document KBs, up to `MaxKnowledgeBases` as today (`knowledgeBaseIds[]` stays). Each is a target of its own with its own name (A1-4) and its own description (A1-5). A call of `documents` or `count` reads **one** KB, the one the call names (the lean `query` already takes the KB as its target), so the cursor of `documents` no longer walks from one KB to the next and no longer names a KB. The lookups of the tool (computed, entity, manual) can come from any KB the tenant sees, and the computed lists of a live KB are allowed. This amends 007 A1 and D-8 (b). |
| A1-3 | **One view of a KB with both roles.** A tool that has the entity lookup of a KB cannot have the same KB among its live KBs, and the other way round (400 `lookup_tool.knowledge_base_both_views`); the admin makes a second tool for the other view. The computed lists of the live KB stay allowed. |
| A1-4 | **One namespace.** The targets of a tool, that is its lookups (by alias when there is one, else by name) and its live KB (by name), have distinct names. A name used twice is refused (400 `lookup_tool.target_name_taken`), in both manifests, and neither `describe` nor `query` picks one of two. A rename of a lookup or of a KB that would make two targets of a tool share a name is refused with 409 `lookup_tool.target_name_taken`, listing the tools. |
| A1-5 | **`describe` states only what the target applies.** For each target the answer names the operations, the filters, the sort and the limits that **that target** takes, and nothing else. A live KB: the exposed fields its mapping maps, with `eq` and `in`. A computed list: the exposed fields its source KB maps, `search`, `sort` by name or count. An entity or manual lookup: its attributes with their type and the operators of D-5 (`eq` and `in` for every type, `min` and `max` for `number` and `date`), and `sort` by the name or by an attribute of type `number` or `date`; the dictionary fields of its source KB are not listed for it. A filter that the tool does not offer, or that the target does not map, appears nowhere in the answer for that target. There are no filters "of the tool" shared by every target. |
| A1-6 | **`describe` and `query` agree.** What `describe` lists for a target is accepted by `query` for it, and what `query` rejects for a target is not in `describe`. A call that names a field, operator, sort or operation the target does not list returns the error result of D-8 with the schema of that target. |
| A1-7 | **`valuesIn` points to a callable target.** A filter names the list that holds its values only when that list is a target of the tool; otherwise the field is described with no pointer, and the description says so in words. |
| A1-8 | **Full manifest.** The roles (A1-1), the live KB (A1-2, A1-3) and the namespace (A1-4) apply to both manifests. The full manifest keeps one set of filter parameters per function (007 A1) and states in each parameter's description which targets it applies to; the filters on the attributes of entity lookups exist in the lean manifest only. |
| A1-9 | **Guard on the mapping.** A change of the mapping of a KB that removes the role a tool relies on (the document roles of a live KB, `entityName` of an entity lookup) is refused with 409 `knowledge_base.role_in_use`, listing the tools. |
| A1-10 | **One selector in the editor.** The editor of the tool has one selector of targets: the lists, the entity and manual lookups, and the live KBs chosen among the document KBs. The separate "knowledge bases to ask live" disappears. A KB that is not a document KB is never offered as a live KB, and a KB that is already a lookup source in the tool as an entity is not offered either (A1-3). This amends D-12. |
| A1-11 | **No migration.** D-10 is amended: tools are not converted. The development database is reset, the rules of A1-2 to A1-4 apply when a tool is saved, and a stored tool that does not follow them is deleted, never served. The compatibility of the `knowledgeBaseIds[]` field is dropped. |

### API Contract
| Verb | Route | Change | Errors |
|---|---|---|---|
| POST, PUT | `/tools/lookup` | `knowledgeBaseIds[]` stays and is validated against A1-1 and A1-3 | 400 `lookup_tool.knowledge_base_not_documentary` (A1-1), `lookup_tool.knowledge_base_both_views` (A1-3), `lookup_tool.target_name_taken` (A1-4) |
| PUT | `/knowledge-bases/{id}` (mapping) | A change that removes a role in use (A1-9) | 409 `knowledge_base.role_in_use` |
| PUT | `/knowledge-bases/lookups/{lookupId}`, `/knowledge-bases/{id}` (rename) | A rename that makes two targets of a tool share a name | 409 `lookup_tool.target_name_taken` |

Chat: no new route; `lookup_describe__<name>` and `lookup_query__<name>` as in D-8, with the content of A1-5.

### Authorization
Unchanged: `tools.manage` for the tool, `knowledge_base.manage` for the mapping and the names, `tools.use` for `describe` and `query`. The 409 lists name the tools the caller can see.

### Edge Cases & Failure Modes
- **A name collision between a target of a tool and a lookup renamed later**: refused at the rename (A1-4), so a saved tool never holds two targets with one name.
- **A live KB that is deleted, or that stops being visible to the assistant**: the tool is served without it, as today (012 "Edge Cases"), and the editor warns.
- **A KB that gains `entityName` or `text` after the tool was saved**: nothing changes for the tool, which keeps its single view; the new role is available to other tools.
- **A `describe` of a target whose snapshot is not ready**: as in D-8; the answer says it is not ready and lists no filters it cannot yet apply.
- **A manual lookup, which has no KB**: it is a target like the others; it takes no dictionary-field filters.

### Non-Functional Requirements
- The lean manifest stays smaller than the full one (AC-24), measured again with the sample project after the change: `describe` no longer repeats the same filters on every target.
- `describe` is computed from the same definitions `query` validates against (A1-6), so it cannot drift from what the tool accepts.

### Integrations
None new. The KnowledgeBases module exposes the role of a KB and the attributes of a lookup through `IKnowledgeBaseLookups` (spec 007); the Agents module builds the descriptions from them.

### Acceptance Criteria
- **AC-A1-1** Given an entity KB with `entityName` and neither `text` nor `documentId`, When it is saved as the live KB of a tool, Then it returns 400 `lookup_tool.knowledge_base_not_documentary`; given a document KB, Then it is accepted.
- **AC-A1-2** Given a tool with two document KBs as live KBs, When `documents` or `count` is called with one of them as the KB, Then only that KB is read and the cursor of `documents` does not name a KB; given a call that names a KB that is not a live KB of the tool, Then it returns an error result that names the valid ones.
- **AC-A1-3** Given a KB with both roles, When a tool that has its entity lookup is saved with the same KB as its live KB, Then it returns 400 `lookup_tool.knowledge_base_both_views`; given a tool with its computed lists and the KB as live KB, Then it is accepted.
- **AC-A1-4** Given a tool with the lookup `vendors` and a live KB named `vendors`, When it is saved, Then it returns 400 `lookup_tool.target_name_taken`; given an alias that makes the names distinct, Then it is accepted and both targets can be reached in the lean manifest.
- **AC-A1-5** Given a tool whose lookup is renamed to the name of its live KB, When the rename is sent, Then it returns 409 `lookup_tool.target_name_taken` listing the tool.
- **AC-A1-6** Given `describe` on a live KB and on a computed list that come from two KBs that map different fields, When called, Then each target lists only the exposed fields its own KB maps, and a field mapped by one is absent from the other.
- **AC-A1-7** Given `describe` on an entity lookup with `country` (text), `rating` (number) and `since` (date), When called, Then it lists the three attributes with their type and operators (`eq`, `in`; `min`, `max` for `rating` and `since`), and sort by name, `rating` and `since`, and no dictionary field of its KB.
- **AC-A1-8** Given every target of the sample project, When `describe` is read and each listed filter, operator and sort is sent to `query`, Then `query` accepts them all; given a filter that `describe` does not list for a target, Then `query` rejects it with that target's schema.
- **AC-A1-9** Given a filter whose list of values is not a target of the tool, When `describe` is read, Then the filter has no `valuesIn` and its description says the values are not listed.
- **AC-A1-10** Given a full manifest, When generated for a tool with an entity lookup, Then it has no parameter for the attributes of the lookup, and each filter parameter's description names the targets it applies to.
- **AC-A1-11** Given a KB used as a live KB or as an entity source by a tool, When a mapping change removes that role, Then it returns 409 `knowledge_base.role_in_use` listing the tools; given a change that keeps it, Then it is accepted.
- **AC-A1-12** Given the editor of the tool, When opened, Then it has one selector of targets, the live KBs are chosen among the document KBs only, and an entity KB is offered only as a lookup.

### Deferred Decisions
| Decision | Chosen fallback | Revisit when |
|---|---|---|
| Filters on the attributes of entity lookups in the full manifest | Not offered (A1-8) | A model that cannot use the lean manifest needs them |
| One call of `documents` or `count` across several live KBs | Not offered (A1-2): a call reads one KB | A real case needs one answer across two document KBs |
| Moving entities out of the KB model | An entity lookup keeps its KB as the source | The roles of a KB become awkward for the admin even with A1-1 |

### Open Questions
None.
