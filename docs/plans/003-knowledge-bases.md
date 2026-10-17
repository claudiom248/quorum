# Plan 003: Knowledge Bases

**Spec:** `docs/specs/003-knowledge-bases.md` (Approved 2026-10-17)
**Architecture:** Modular Monolith; Vertical Slice inside each module.
**Mode:** `/tdd`: every acceptance criterion becomes a failing test first.
**Status:** In Review

## Decisions
| Topic | Choice |
|---|---|
| Ownership | A new module **KnowledgeBase** (`Quorum.Modules.KnowledgeBase` + `.Contracts`, schema `knowledge_base`) owns dictionary, KB registrations, the Qdrant connector, discovery and search. The **rag tool** is a tool of origin `Rag` in **Agents** (pool, activation, policy, audit and the loop are there). **Conversations** stores and returns the sources of a message and relays the new SSE event. Nobody reads another module's tables |
| Contracts between modules | `KnowledgeBase.Contracts`: `IKnowledgeBaseSearch` (search the given KBs for a tenant and assistant) and `IKnowledgeBaseCatalog` (describe the visible KBs and fields of a binding; check that a binding's KBs and fields are visible to a whole scope). `Agents.Contracts` gains `IEmbeddings` (KnowledgeBase embeds a text with a named model on the tenant's endpoint) and `IRagToolUsage` (which rag tools use a dictionary field, for `knowledge_base.field_in_use` and `usedByCount`). Contracts reference only `Quorum.Shared` (the architecture test already enforces it); the two Contracts projects do not reference each other. No Microsoft.Extensions.AI type crosses a Contracts project |
| Permission | `QuorumPolicies.KnowledgeBaseManage = "knowledge_base.manage"`, registered in `TenancyModule` next to the tool policies (`Admin` today). Rag tool endpoints use `tools.manage` / `tools.attach` / `tools.use` |
| Qdrant access | Own thin **REST adapter** (`QdrantRestClient`) over a named `HttpClient`, not `Qdrant.Client` (gRPC) and not `Microsoft.Extensions.VectorData`: the payload is schemaless and chosen by the admin, which typed records do not allow, and one URL (the REST port, 6333) is what the admin pastes and what the URL guard checks. Calls: `GET /collections/{name}` (vectors, distance, payload indexes), `POST /collections/{name}/points/scroll` (sampling, payload only), `POST /collections/{name}/points/query` (search; Qdrant 1.10 or later, verification says so when it is older), header `api-key`. Step 8 starts with a prototype against the dev Qdrant to settle date filters, named vectors and the response shapes |
| URL guard | `McpUrlGuard` and `IMcpHostResolver` move to `Quorum.Shared` as `OutboundUrlGuard` / `IHostResolver` (infrastructure, no business logic) and Agents keeps using them; the Qdrant `HttpClient` (`knowledge-base-qdrant`) has the same handler settings: no redirects, no system proxy, `ConnectCallback` that validates the address the socket opens, request logging removed. The existing `McpUrlGuardTests` keep passing unchanged |
| Secrets | `KnowledgeBaseSecretProtector` (own purpose string `Quorum.KnowledgeBase.Connection`) encrypts the API key with Data Protection; the API returns `hasApiKey` only. The connection is stored as kind-specific JSON (`Url`, `Collection`, `VectorName`, protected `ApiKey`) |
| Embeddings | `OpenAiEmbeddings` in Agents implements `IEmbeddings` with an own small client in the style of `OpenAiChatClient` (`POST {baseUrl}/embeddings`, the tenant's model endpoint and key, one input per call, the model name given by the KB). An endpoint without `/embeddings` or a model that is not served gives a clear error. The result is validated as a non-empty vector of finite numbers |
| Mapping model | `Mapping` is owned JSON on the KB: `Roles { Text, DocumentId?, Title?, Position?, SourceUrl? }` (payload paths, dot notation) and `Fields [{ Path, DictionaryFieldId }]`. `MappingConventions` (role names, metadata naming) and `PayloadSampler` (type inference) produce the draft; `JsonSchemaReader` produces the same draft from a pasted schema. One draft model, three sources, so the review screen is the same |
| Visibility | `KnowledgeBaseVisibility` and `DictionaryVisibility` are the only queries that turn the tenant filter off, like `ToolVisibility`: own rows, `Global`, listed `Tenant`, and `Assistant` only for the owner's assistants. Another tenant's view of a shared KB is a reduced DTO without URL, collection or key |
| Generated tool definition | `RagToolDefinition` builds the description and the JSON Schema of a rag tool from its binding and `IKnowledgeBaseCatalog` **at the start of each answer**, not at save time, so a renamed or deleted KB never leaves a stale enum. `Tool.InputSchema` holds `{}` for rag tools; `ActiveToolsQuery` hands the stored rows to a `RagToolResolver` in `AssistantChat.PrepareAsync` that replaces description and schema, drops rag tools left with no visible KB and returns the result to the existing `QuorumToolFactory`. The admin preview endpoint uses the same builder, so what the admin sees is what the budget counts |
| Call-time check | `Tool.DefinitionHash` of a rag tool is the SHA-256 of its stored configuration (KBs, exposed fields, fixed filters, limits, scope). The existing check in `ToolCallInvoker` ("the hash I was offered is still the stored one") then refuses a call after an admin changed the binding mid-answer. The search itself re-validates against the live catalog |
| Filter parameters | Built from the exposed fields: text → `<field>: string[]`; number → `<field>_min`, `<field>_max`; date → `<field>_from`, `<field>_to` (ISO 8601 date); boolean → `<field>: boolean`; list → `<field>: string[]`. Descriptions come from the dictionary. Fixed filters use the same shapes in `FixedFilters` JSON. `FilterTranslator` turns them into one Qdrant `must` list per KB using each KB's mapped payload paths; a KB lacking a mapped path for any filter is skipped (D-12). Limits: at most 20 values per list, 200 characters per value |
| Search engine | `KnowledgeBaseSearch`: resolve the visible KBs of the binding → embed once per distinct (model, prefix) → query each KB in parallel under one 15 s token → keep scores per KB, drop below the threshold → interleave round-robin in KB-name order → cut whole chunks to the character cap → map payload to roles. Per-KB failures become notes; `Status` is updated on each search (`Unavailable` on failure, `Available` on success) with a single-row `ExecuteUpdate`, never loading the entity |
| Tool output | `RagQuorumTool` renders the result as numbered blocks (`[n] KB · title, position`, then the chunk inside a per-request delimiter and the data label of spec 002, reusing the delimiter generator of `AttachmentBlock`), then notes (skipped KBs, failed KBs, "nothing found"). The numbers come from a per-answer `SourceNumbering` that the factory hands to every rag tool of the answer |
| Sources | `QuorumTool` gets a virtual `RunWithSourcesAsync` returning `ToolOutput(Text, Sources)`, defaulting to `RunAsync`; the invoker publishes a `Sources` chunk (`AnswerChunkKind.Sources`, `SourcesEvent`) before the `ToolResult` chunk. Conversations collects them in `AnswerStream`, stores them as `MessageSource` rows with the assistant message and sends `sources` over SSE live |
| Audit | `ToolAuditEntry` gains a nullable `SourceCount` (migration in PR 5). Arguments, redaction, fail-closed and retention are untouched. The search text and chunks never reach the audit log |
| Test infrastructure | `Testcontainers` Qdrant (the `Testcontainers.Qdrant` package, or a generic container if it does not fit) beside the existing `PostgresFixture`. `FakeModelServer` learns `/embeddings`. `tools/Quorum.SampleKnowledgeBase` (a library plus a small console tool) builds the sample collections of D-20 with a **deterministic embedding** (hashed bag of words into 64 dimensions, normalised) so that tests can assert real ranking without a model; the integration tests reference the library. Time-dependent paths use `FakeTimeProvider` |
| UI | Existing MudBlazor components and the `ToolAdminView` style (logic in plain classes, covered by bUnit). Admin: a "Knowledge" area with the dictionary, the knowledge bases (guided registration, verify, test search) and, in the Tools page, the rag tool editor with the definition preview. Chat: a `SourcesList` under the answer |

## Steps

### PR 1 - Module and dictionary (`feat/003-dictionary`)
1. `Quorum.Modules.KnowledgeBase` and `.Contracts` projects, solution entries, `AddKnowledgeBaseModule` registered in the Host, `KnowledgeBaseDbContext` (schema `knowledge_base`, tenant filter, design-time factory), migration `Initial`. The module-reference architecture tests must pass without changes.
2. `QuorumPolicies.KnowledgeBaseManage` and its registration in `TenancyModule`. AC-42 (the `/knowledge-bases` half).
3. `DictionaryField` entity, name rules (regex, reserved names and suffixes), uniqueness among visible fields, scopes `Global` / `Tenant`, `DictionaryVisibility`. AC-1, AC-2, AC-3.
4. Dictionary endpoints (`GET/POST /knowledge-bases/dictionary`, `PUT/DELETE .../{id}`); `usedByCount` and `field_in_use` count KBs only for now (tools join in PR 5). Description editable, name and type fixed. AC-4 (KB part).

### PR 2 - Qdrant connector and inspect (`feat/003-qdrant-inspect`)
5. `OutboundUrlGuard` and `IHostResolver` move to `Quorum.Shared`; Agents uses them; MCP tests unchanged.
6. `IEmbeddings` in `Agents.Contracts` and `OpenAiEmbeddings` in Agents; `FakeModelServer` serves `/embeddings`.
7. `tools/Quorum.SampleKnowledgeBase` library: deterministic embedding, the collections of D-20 (flat and nested payload, list tags, fields without payload index, named vector, a different vector size, an empty collection) and the pasteable schema; `QdrantFixture` in the tests. AC-51 (generator part).
8. Prototype of the Qdrant REST calls against the dev Qdrant (date range on strings, named vectors, `points/query` shape, payload index listing), then `QdrantRestClient` with the guarded `HttpClient`, API key header, 10 s connect timeout and the version check.
9. Inspect: `PayloadSampler` (20 points, no vectors, type inference, mixed types as text with a warning), `MappingConventions`, `JsonSchemaReader` (draft 07 / 2020-12, inline properties, 100 KB, unsupported keywords ignored with a warning), collisions and invalid or reserved names flagged, `unindexedFields`, vector names, dimension probe through `IEmbeddings`. `POST /knowledge-bases/inspect`. AC-6, AC-7, AC-8, AC-9, AC-11, AC-15 (inspect half).

### PR 3 - Knowledge base registry (`feat/003-kb-registry`)
10. `KnowledgeBase` entity with connection JSON, `Mapping`, scope (the object shape of spec 004), status; migration `AddKnowledgeBases`; protected API key; `KnowledgeBaseVisibility`.
11. Endpoints: create, list, detail, update, delete, verify. Name rules and uniqueness, mapping validation (`text` role required, type compatibility, ISO 8601 for dates, chosen vector name when the collection has several), field visibility rule (a Global KB uses only Global fields), `createFields` confirmed by the admin, type conflict. The API key is never returned. A failed verification saves the KB as `Unavailable` with a warning that names the cause and the consequence. AC-5, AC-10, AC-11, AC-12, AC-13, AC-14, AC-15, AC-16, AC-17, AC-18.
12. Shared KBs for other tenants: the reduced DTO (name, description, status). AC-43 (KB half).

### PR 4 - Search (`feat/003-search`)
13. `FilterTranslator` and value validation by type (list and string caps), fixed filters, KB skipped when it lacks a mapped path. AC-25, AC-26, AC-28, AC-31.
14. `KnowledgeBaseSearch`: embedding with the KB's prefix, parallel per-KB queries under the 15 s limit, threshold, interleaving, whole-chunk cap, payload-to-role mapping with skipped unusable chunks, partial failures as notes, status updates on failure and success. AC-27, AC-29, AC-30, AC-34, AC-35, AC-36.
15. `IKnowledgeBaseSearch` and `IKnowledgeBaseCatalog` in the Contracts project (catalog description, scope check of a binding). `POST /knowledge-bases/{id}/search` for the admin, not audited as a tool call. Logs carry only ids, counts, durations and codes. AC-46, AC-47.

### PR 5 - The rag tool (`feat/003-rag-tool`)
16. Agents: `ToolOrigin.Rag`, `RagTool` entity (tool id, KB ids, exposed field ids, fixed filters, limits, scope, configuration hash), `ToolAuditEntry.SourceCount`; migration `AddRagTools`. Pool and visibility rules for rag scopes in `ToolPool` / `ToolVisibility`; exposed name `rag__<name>`. AC-23 (pool part).
17. `RagToolDefinition` and `RagToolResolver` (live catalog, drops rag tools with no visible KB). AC-19, AC-20.
18. Endpoints under `/tools/rag` (create, list, detail with preview, update, delete) with limits, visibility of KBs and fields for the whole scope, fixed-filter validation, `rag.*` codes; rag tools appear in `GET /tools` and in the assistants' pools; default policy `Auto`. AC-21, AC-22, AC-23.
19. `RagQuorumTool` and the factory change: call to `IKnowledgeBaseSearch`, output format with delimiters and the data label, per-answer `SourceNumbering`, errors as tool results without internal detail, audit of the number of sources, instruction to cite `[n]` added to the system prompt when a rag tool is offered. AC-24, AC-32, AC-33, AC-45.
20. `IRagToolUsage` for the dictionary (`field_in_use` and `usedByCount` now count tools). A deleted KB is simply absent from the catalog, so rag tools stop using it without writes across modules. Scope narrowing and tenant isolation tests. AC-4, AC-43, AC-44, AC-48.
21. Permission tests for `/tools/rag` and `/knowledge-bases`. AC-42.

### PR 6 - Citations (`feat/003-citations`)
22. `AnswerChunkKind.Sources`, `AnswerSource`, `SourcesEvent` in `Agents.Contracts`; `QuorumTool.RunWithSourcesAsync`; the invoker publishes `Sources` before `ToolResult`; numbering continues across calls in one answer. AC-37.
23. Conversations: `MessageSource` entity and migration; `AnswerStream` relays the `sources` event and stores the sources with the message (only references: KB name, document id, title, position, URL); `GET /conversations/{id}` returns `sources[]`; regeneration replaces them; deleting the conversation or message deletes them; a deleted KB leaves the stored copy untouched. AC-38, AC-39, AC-41.

### PR 7 - Admin UI (`feat/003-ui-admin`)
24. API client and models; plain view classes for the registration flow (draft mapping rows, warnings, scope summary).
25. Pages: dictionary; knowledge bases with the guided registration (connection → inspect with warnings → optional pasted schema → mapping review and editing → scope → save), verify, test search; the rag tool editor in the Tools page (KBs, exposed fields up to 10, fixed filters, limits, definition preview with its estimated size). AC-49.

### PR 8 - Sources in the chat (`feat/003-ui-chat`)
26. `SourcesList` under the answer, `[n]` markers matching, cited sources first and the others collapsed, links only for `http` and `https` with `rel="noopener noreferrer"`, text never as HTML; sources come back when the conversation is reopened. AC-40, AC-50.

### PR 9 - Documentation and verification (`docs/003-closing`)
27. `docs/knowledge-bases.md` for data owners (payload conventions and the schema to hand to the admin, payload indexes for the fields to filter on, same embedding model and prefix as the ingestion), a pointer from the README, and how to run the sample project against the dev compose. AC-51 (running it).
28. `/verify`, and a manual run with a real Ollama embedding model and chat model against the seeded dev Qdrant: registration, a question with filters, the sources in the chat. Record the observations (retrieval quality is measured here, not promised).

## Risks
- **Retrieval quality on small models**: the model may not call the tool, filter wrongly or ignore the sources. Mitigation: a short generated definition, admin-chosen exposed fields, errors that name what to fix, the `[n]` instruction; quality is observed in step 28.
- **Qdrant REST differences across versions**: `points/query` needs 1.10 or later; response shapes and datetime filters changed over versions. Mitigation: the prototype in step 8, a version check in verification, tests against the pinned image of the dev compose.
- **Ingestion and retrieval models differ** (dimension, prefix, normalisation): the worst failure is silent bad ranking. Mitigation: the dimension check, the optional prefix, the admin test search with visible scores, and the data-owner documentation. A model of the same size but different space cannot be detected.
- **Payloads vary**: 20 sampled points may miss optional fields or show mixed types. Mitigation: the review step lets the admin add and edit mappings by hand; mixed types default to text with a warning.
- **Unindexed filter fields on large collections** are slow. Mitigation: `unindexedFields` warning at inspect, documented; no automatic index creation (Quorum never writes to the store).
- **Cross-tenant leaks through shared KBs**: the visibility queries are the only place that disable the tenant filter. Mitigation: a dedicated test per rule (own, Global, Tenant list, Assistant), the reduced DTO, and the search taking tenant and assistant explicitly and refusing a tenant that is not the ambient one.
- **Prompt injection through retrieved chunks**: delimiters with a per-request token, the data label, policies and confirmation of other tools unchanged (spec 004 D-31); a hostile chunk cannot call a tool that needs confirmation without the user seeing the arguments.
- **Context budget**: the generated definition counts in the tools segment and a result is capped at 6,000 characters by default; the admin ceilings keep a result from filling the context. The preview shows the size.
- **Search latency**: embedding plus Qdrant on each call, under the 15 s limit and the tool-call timeout of spec 004 (30 s), whichever is shorter. A slow embedding model makes every question slow; documented, measured in step 28.
- **Three modules, three migrations** (`knowledge_base`, `agents`, `conversations`) across PRs: review them in order and keep each reversible.
- **Moving the URL guard to Shared** touches a security component. Mitigation: a mechanical move, the existing guard tests unchanged, done in its own early step.
- **Hosted background answer and the tenant context**: the answer runs on a background task but in the request's scope; the search receives tenant and assistant as arguments and the test covers a tenant mismatch.

## Details beyond the spec to confirm
- Qdrant is reached over REST (port 6333) with an own adapter, minimum Qdrant 1.10; no gRPC and no `Microsoft.Extensions.VectorData` (reason in Decisions).
- `McpUrlGuard` and its resolver move to `Quorum.Shared` as `OutboundUrlGuard`.
- `Agents.Contracts` gains `IEmbeddings` and `IRagToolUsage`; `KnowledgeBase.Contracts` has `IKnowledgeBaseSearch` and `IKnowledgeBaseCatalog`.
- The rag definition is built at the start of each answer from the live catalog, and `Tool.DefinitionHash` is the hash of the stored configuration.
- Scope in the API is the object `scope: { kind, tenantIds?, assistantIds? }` for dictionary fields (`Global` / `Tenant`), KBs and rag tools, with the validation rules of spec 004 (`Global` takes no ids, `Tenant` lists tenants besides the owner, `Assistant` lists existing assistants of the owner); extra codes `knowledge_base.invalid_scope` and `rag.invalid_scope`.
- Limits not in the spec: a rag tool name is at most 40 characters (`[a-z0-9_-]`), a KB description at most 500 characters, a filter list at most 20 values of at most 200 characters, the admin test search `limit` at most 20.
- Round-robin order is by KB name, so results are deterministic.
- Embedding calls are one text at a time; no caching in this spec (deferred).
- `ToolAuditEntry.SourceCount` is a new nullable column.
- Sampling reads the first 20 points returned by `scroll` (not a random sample).
- The test-only dependency `Testcontainers.Qdrant` (or a generic Qdrant container) and the new `tools/Quorum.SampleKnowledgeBase` project, referenced by the integration tests and by nothing in the product.
