# Spec 019: Models as a First-Class Resource

**Status:** Approved (2026-11-11)
**Date:** 2026-11-11

Builds on spec 013 (agents, scope rules D-13/D-21 of spec 017, model endpoints as a resource D-33, assistant facade D-35), spec 003 (knowledge bases) and spec 016 (telemetry). It replaces the "model endpoint" of the Agents module with a **model**: a resource of a new `Models` module that agents and knowledge bases both select from a list.

## Problem

A "model endpoint" lives inside the Agents module and in the Agents branch of the admin menu, although it is not only about agents. Knowledge bases need a model too, an embedding model, and today they get it in a poor way: the KB stores a free-text `EmbeddingModel` and a `QueryPrefix`, and `IEmbeddings` always sends the request to the **first** endpoint of the tenant (`OrderBy(Id).First()`). So a KB cannot choose which server embeds, two KBs cannot use servers with different keys or URLs, a typo in the model name is found only at the first search, and the limit is already recorded in `docs/deferred.md` ("Embedding endpoint per knowledge base").

The fix is to treat the model as a platform resource of its own: registered once (connection, key, what it can do), then **selected from a list** by whoever needs it, exactly as agents select a model endpoint today. The admin UI follows: models leave the Agents tree and become a top-level section.

## Decisions

### Entity and capabilities

- **D-1**: A **model** is one registered model of an OpenAI-compatible server: name, base URL (with the version segment), provider model name, optional API key (encrypted), **capabilities**, and the fields those capabilities need. It replaces `ModelEndpoint` (spec 013 D-33). The connection stays inside the model (URL and key are repeated per model on the same server); a separate connection entity is a deferred decision.
- **D-2**: **Capabilities** are declared by the admin, not an exclusive "purpose": `Chat`, `Tools` and `Embeddings`. `Tools` requires `Chat`. At least one capability is required. A model may have `Chat` and `Embeddings` together.
- **D-3**: Capability fields. `Chat`: `ContextTokenBudget` (mandatory, as today). `Embeddings`: `QueryPrefix` (optional, the text put before a query before it is embedded, `search_query: ` for nomic, `query: ` for e5, as the KB field is today) and `EmbeddingDimensions` (optional, informational). A field that does not belong to the model's capabilities is stored empty.
- **D-4**: The **query prefix is a property of the model**, not of the knowledge base. A KB indexed with another convention uses a second model registered with the same URL and provider name and the other prefix. There is no per-KB override.
- **D-5**: **The connection test suggests, the admin decides.** Testing a model reports the capabilities it detects: `Tools` as today (spec 013), `Embeddings` by embedding a fixed short text, which also returns the **dimensions** of the vector. The form pre-fills the suggested capabilities and the dimensions; the server stores what the request says and does not enforce dimensions anywhere.
- **D-6**: A model is **unique by name per tenant** (owner), like endpoints today. It is edited, renamed and deleted only by its owner tenant.

### Visibility

- **D-7**: Models have a **scope** like agents, skills and KBs: `Global` or `Tenant` with `ScopeTenantIds`, on top of the owning `TenantId`. The scope decides which tenants can **select** the model; it does not change who can edit it.
- **D-8**: The rule of spec 017 D-13 applies to every consumer: an agent or a knowledge base must be **visible at most as widely as the model it uses**. A `Global` agent or KB accepts only `Global` models; one limited to some tenants accepts `Global` models or models whose scope includes those tenants; an assistant-scoped KB follows its owner tenant. A violation is refused at save.
- **D-9**: At runtime a model is resolved **by id without the tenant filter**, as agents already do for endpoints, so a shared agent or KB runs on the model its owner chose, with the owner's key. A non-owner never sees the URL or the key: the model, as it appears in the lists of other tenants, has no `baseUrl` and no `hasApiKey`.

### Protections

- **D-10**: Deleting a model used by agents or knowledge bases is refused with `409`, listing them (`BlockingReference`, kinds `agent` and `knowledge_base`).
- **D-11**: Removing a capability that something relies on is refused with `409` and the list: `Chat` with agents on it, `Tools` with agents that have tools or sub-agents (spec 013 D-19, unchanged in behaviour), `Embeddings` with KBs on it. The assistant settings facade (spec 013 D-35) stays exempt from the `Tools` check, as today.
- **D-12**: Narrowing the scope of a model (or removing a tenant from it) so that an agent or KB using it stops satisfying D-8 is refused with `409` and the list.
- **D-13**: Changing the URL, provider name or prefix of an embedding model that KBs use is **allowed, with no warning** (the vector space may change and make those KBs unsearchable; the admin owns the consequence).

### Consumers

- **D-14**: Agents reference a model by `modelId` (renamed from `modelEndpointId` in the API, in the stored column and in the agent document). Only models with `Chat` can be selected; with tools or sub-agents also `Tools`. Spec 013 D-12 (a sub-agent without a model inherits the root's) is unchanged.
- **D-15**: A knowledge base references an **embedding model by id** (`embeddingModelId`, mandatory). Only models with `Embeddings` visible per D-8 can be selected. The free-text `embeddingModel` and `queryPrefix` fields disappear from the KB API and the KB table. The KB search embeds the query with the model's prefix, and queries that share a model are embedded once (today: the same `(model, prefix)` pair).
- **D-16**: The first-run wizard and `/api/settings/assistant` (spec 001, spec 013 D-35) keep working as a facade; the model they create has `Chat` and, when detected, `Tools`.

### Architecture

- **D-17**: A new module `Quorum.Modules.Models` (+ `.Contracts`), with its own DbContext and schema `models`. It owns the table, the CRUD, the connection test (probe), the API-key protector and the embedding client (all moved out of Agents). The Data Protection **purpose string of the key is kept** (`Quorum.Agents.ModelEndpoint.ApiKey`) so keys stored before the change still decrypt.
- **D-18**: Dependencies go through `Quorum.Modules.Models.Contracts`: a catalog (list visible models by capability, get one, check that a model is usable for a given consumer scope and capability, resolve the connection of a model for the chat client), `IEmbeddings` (now by `modelId`, applying the model's prefix; moved from `Agents.Contracts`), the probe, and a **usage source** interface that Agents and KnowledgeBases implement and register, so Models can answer D-10, D-11 and D-12 without referencing them. Agents and KnowledgeBases keep a plain `Guid` for the model: **no database foreign key across schemas**.
- **D-19**: The wire types of Microsoft.Extensions.AI and the OpenAI client stay inside the Models module, as they do inside Agents today.

### Admin UI

- **D-20**: **Models** is a top-level menu entry between Chat and the Agents group (route `/models`, `/models/{id}`), visible to admins. `/agents/endpoints` redirects to it. The "Model endpoints" entry leaves the Agents group, and the product vocabulary is "model" everywhere (UI, API, docs).
- **D-21**: The Models page lists the models with their capabilities and scope; the form has the capability checkboxes, shows the fields of D-3 only for the selected capabilities, the scope selector of agents, and the connection test with its suggestions (D-5). Models of other tenants appear read-only (D-9).
- **D-22**: The agent form selects among chat models (as now, filtered). The knowledge base wizard and editor replace the "Embedding model" text field and the prefix field with a **list of embedding models**; with none available the list says so and links to `/models`. The KB list and detail show the model name.
- **D-23**: The getting-started checklist and the setup redirect point to `/models`.

### API changes

- **D-24**: The API of model endpoints becomes the API of models, with a breaking rename (`feat!`), no compatibility path: `/api/model-endpoints` → `/api/models`; `modelEndpointId` → `modelId` in agents, `agents` settings, assistants and the declarative reference; `embeddingModel` + `queryPrefix` → `embeddingModelId` in the KB registration, inspect and detail.

### Database: squash and upgrade

- **D-25**: The migrations of **every module** are squashed into a single `Initial` per module, generated from the final model: Tenancy, Licensing, Agents, KnowledgeBases, Conversations and Models. Migration tests that exercised the old steps are removed or rewritten. A `PendingModelChanges` check keeps the model and the snapshot aligned.
- **D-26**: **No data copy in the migrations.** Existing data is restored by two SQL scripts, **delivered in the conversation and not committed to the repository**: (1) before the first start, rename every old module schema to `<schema>_old`; (2) after the first start (which creates the new schemas empty), copy the data across with explicit column lists. The result must be the state before the change.
- **D-27**: The restore transforms: each old model endpoint → a model **with the same id**, capabilities `Chat` and, when `SupportsTools`, `Tools`, the same URL, key (ciphertext copied as is), provider name and budget; each KB's embedding model → an `Embeddings` model owned by the KB's tenant, connection copied from that tenant's **first endpoint** (`min(id)`, the one the old code used), provider name and prefix from the KB, one model per distinct `(tenant, embedding model name, prefix)`, named after the embedding model name (a numeric suffix on a clash); the KB gets its `embeddingModelId`. A restored model's scope is the widest of the agents and KBs that use it (so D-8 holds): `Global` if any user is `Global`, else `Tenant` with the union of their tenants, else `Tenant` with no extra tenants.
- **D-28**: A KB must always have a model (D-15). The restore script **stops with an error before writing anything**, listing the KBs whose tenant has no endpoint. It runs in a single transaction.
- **D-29**: Other installations have **no supported upgrade path**: the breaking-change notes of the release say a squash happened and the restore is not provided.

## Scope

### In

- The Models module (entity, CRUD, probe with dimensions, key protector, embedding client by id), contracts, scope and the guards of D-10 to D-12.
- Agents and KnowledgeBases adapted: `modelId`, `embeddingModelId`, usage sources, scope checks, removal of `QueryPrefix`/`EmbeddingModel` from KBs.
- Admin UI: top-level Models section, agent and KB selectors, empty states, redirects, checklist.
- Squash of all migrations; the two restore scripts (delivered, not committed).
- Documentation: `docs/product.md` glossary, `docs/database-schema.md`, `docs/knowledge-bases.md`, `docs/deferred.md` (the "embedding endpoint per KB" row is resolved), `CLAUDE.md` architecture list.

### Out

- A separate connection/provider entity shared by several models (D-1).
- Per-KB prefix override, usage view ("used by") on the model page, warning on edits of models in use (D-4, D-13).
- Non-OpenAI-compatible providers, reranker or other capabilities, a declarative file format for models.
- Re-embedding or any ingestion, enforcing dimensions against the KB store.
- A supported upgrade path other than the maintainer's own database (D-29), and the script in the repository (D-26).

## Domain Model

| Entity | Fields | Notes |
|---|---|---|
| **Model** (renamed `ModelEndpoint`; schema `models`) | Id, TenantId, Name (unique per tenant), BaseUrl, ModelName, ProtectedApiKey?, Capabilities (`Chat`, `Tools`, `Embeddings`), ContextTokenBudget? (with `Chat`), QueryPrefix? and EmbeddingDimensions? (with `Embeddings`), Scope (`Global`, `Tenant`), ScopeTenantIds, UpdatedAt | Global query filter on `TenantId`; visible-set queries ignore it and apply the scope, as `VisibleAgents` does |
| **Agent** | `ModelEndpointId?` → `ModelId?` | Null only for sub-agents that inherit (spec 013 D-12). No FK |
| **KnowledgeBase** | `EmbeddingModel`, `QueryPrefix` → `EmbeddingModelId` (required) | No FK. Existing rows restored per D-27 |

Lifecycle: create → edit (rename, reconnect, change capabilities and scope under the guards) → delete when unused. No archive state.

## API Contract

| Verb | Route | Request | Response | Errors |
|---|---|---|---|---|
| GET | `/api/models?capability=` | optional capability filter (`chat`, `tools`, `embeddings`) | models visible to the tenant: `{ id, name, modelName, capabilities, contextTokenBudget?, embeddingDimensions?, queryPrefix?, scope, scopeTenantIds, owned, baseUrl?, hasApiKey? }` (`baseUrl`, `hasApiKey` only when `owned`) | 401; 403 |
| GET | `/api/models/{id}` | - | one model, same shape | 404 (also when not visible) |
| POST | `/api/models` | `{ name, baseUrl, modelName, apiKey?, capabilities, contextTokenBudget?, queryPrefix?, embeddingDimensions?, scope, scopeTenantIds }` | 201 | 400 (name, URL, capabilities, D-2/D-3, scope); 409 `model.name_taken` |
| PUT | `/api/models/{id}` | same; `apiKey` omitted keeps the key, empty removes it | 204 | 400; 404; 409 `name_taken`; 409 D-11 and D-12 with the blocking list |
| DELETE | `/api/models/{id}` | - | 204 | 404; 409 `model.in_use` with the list (D-10) |
| POST | `/api/models/test` | `{ baseUrl, apiKey?, modelName?, modelId? }` | `{ reachable, models[], suggestedCapabilities, embeddingDimensions? }` | 400; problem with the stable code of the failure |

Changed elsewhere (D-24): agents (`modelId`), `/api/settings/assistant`, assistants, KB registration/inspect/detail (`embeddingModelId`; the detail also returns the model's name), `IEmbeddings.EmbedQueryAsync(text, modelId)` in `Models.Contracts`. Validation of a consumer's model: unknown or invisible id, missing capability, or a scope broader than the model (D-8) are `400` on the consumer's field.

## Authorization

- Create, edit, delete and test: new permission `models.manage`, held by `Admin` (like `agents.manage`, spec 013 D-26).
- Read (list, get): holders of `models.manage`, `agents.manage` or `knowledge_base.manage`, so the pickers of agents and KBs work for whoever manages those.
- Tenant boundary: a tenant sees its own models plus `Global` ones and those that list it; only the owner can change a model (D-6, D-7). The key is never returned to any client (D-9).

## Edge Cases & Failure Modes

- **Name clash** (also on concurrent saves): `409 name_taken`, via the unique index.
- **Capabilities**: none selected, or `Tools` without `Chat`, or `Chat` without a valid budget → `400`.
- **Stale references**: impossible by construction, because delete, capability removal and scope narrowing are refused (D-10 to D-12); an in-use embedding model can still be reconnected (D-13), and the KBs then fail their next verification or search with the usual `embeddings.*` code.
- **Embedding failures** keep their stable codes (`embeddings.unreachable`, `not_supported`, `model_not_found`, `failed`, `invalid_response`) and never carry the text, key or URL. `embeddings.no_endpoint` becomes `embeddings.no_model` for an id that does not resolve.
- **Test of a model with both capabilities**: the chat probe and the embedding probe run independently; one failing does not hide the other's suggestion.
- **Facade** (D-16): the wizard and Settings keep creating or updating the root agent's model; turning `Tools` off there is not blocked (D-11).
- **Restore** (D-26 to D-28): runs only when the new schemas are empty; refuses otherwise and when a KB has no usable endpoint; a single transaction, so a failure leaves nothing half-copied.

## Non-Functional Requirements

- Volumes are small (tens of models per installation): no pagination on the lists, as for endpoints today.
- One extra lookup per chat turn and per KB search to resolve the model by id; no cache in this spec. The search still embeds once per distinct model.
- Telemetry (spec 016) keeps its attributes; the embedding span reports the provider model name as today.
- No secret in logs or responses: unchanged.

## Integrations

- External: OpenAI-compatible servers for chat and `/embeddings`, unchanged.
- Internal: synchronous calls through `Models.Contracts`, `Agents.Contracts`/`KnowledgeBases.Contracts` (usage sources); no events and no outbox are introduced.
- Docs and CLAUDE.md architecture list gain the Models module.

## Acceptance Criteria

- **AC-1** Given a model with `Chat` and `Tools`, When an agent selects it, Then the agent runs on it as it ran on an endpoint before.
- **AC-2** Given the new KB wizard, When choosing the embedding model, Then a list of visible models with `Embeddings` is shown and no free-text field exists.
- **AC-3** Given two KBs of the same tenant on two embedding models on different servers, When searching both, Then each query is embedded by its own model, with that model's key and prefix.
- **AC-4** Given an embedding model with the prefix `search_query: `, When a KB search runs, Then the embedded text starts with it; and two KBs sharing the model cause one embedding call.
- **AC-5** Given no capability, or `Tools` without `Chat`, or `Chat` without a valid budget, When saving a model, Then the request is rejected with `400`.
- **AC-6** Given a model name already used in the tenant, When creating or renaming (also concurrently), Then `409 model.name_taken`.
- **AC-7** Given a model used by agents or KBs, When deleting it, Then `409 model.in_use` lists them by kind.
- **AC-8** Given agents with tools on a model, When removing `Tools` (or `Chat`), or KBs on it When removing `Embeddings`, Then `409` lists them; and the settings facade can still turn `Tools` off for the assistant's own model.
- **AC-9** Given a `Global` agent or KB, When selecting a model that is not `Global`, Then `400`; and a tenant-limited one accepts a model whose scope includes those tenants.
- **AC-10** Given a model used by a tenant that it lists, When removing that tenant from the scope, Then `409` lists the users.
- **AC-11** Given a `Global` KB owned by tenant A on A's model, When tenant B searches it, Then it embeds with A's model and key, and B never receives the URL or the key.
- **AC-12** Given tenant B, When listing models, Then A's visible models appear without `baseUrl` and `hasApiKey`, and B cannot edit or delete them (`404`/`403` as per the owner check).
- **AC-13** Given an embedding endpoint, When testing it, Then the result suggests `Embeddings` and returns the vector dimensions; given a chat-only server, `Embeddings` is not suggested with the error code of the failure.
- **AC-14** Given an embedding model used by KBs, When changing its URL, name or prefix, Then the save succeeds.
- **AC-15** Given a caller with only `agents.manage` or only `knowledge_base.manage`, When listing models, Then it succeeds; When creating one, Then `403`.
- **AC-16** Given the admin UI, When opening the menu, Then **Models** is a top-level entry between Chat and Agents, the Agents group has no "Model endpoints", and `/agents/endpoints` redirects to `/models`.
- **AC-17** Given no embedding model, When opening the KB wizard, Then the list says so and links to `/models`.
- **AC-18** Given a fresh database, When the app starts, Then each module applies one `Initial` migration, and the model has no pending changes.
- **AC-19** Given a 0.17 database with data in every module, When running the pre-start script, the first start and the restore script, Then ids, row counts, relations and the stored API keys are preserved; every old endpoint is a model with the same id; every KB has an `embeddingModelId` matching D-27; scopes satisfy D-8; and the agents' chat works on a real model.
- **AC-20** Given a KB whose tenant has no endpoint, When running the restore script, Then it stops before writing anything and lists the KBs.
- **AC-21** Given non-empty new schemas, When running the restore script, Then it refuses.

AC-19 to AC-21 are verified by hand on a copy of the maintainer's database before delivery; the scripts are not part of the repository or of CI.

## Deferred Decisions

| Decision | Chosen fallback | Revisit trigger |
|---|---|---|
| Connection shared by several models | URL and key repeated in each model | Many models on one server make key rotation tedious |
| Per-KB query-prefix override | A second model with the other prefix | KBs of the same model need different conventions often |
| Warning or block on editing an embedding model in use (D-13) | Allowed silently | A KB breaks after an edit |
| "Used by" view on the model page | The `409` lists it on conflict only | Admins ask where a model is used |
| Supported upgrade for other installations (D-29) | None; the release notes say so | A second installation exists |
| Other capabilities (reranking, vision) | The capability set is extensible | A feature needs one |

## Open Questions

None.
