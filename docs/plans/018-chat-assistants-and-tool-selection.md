# Plan 018: Chat Assistants and Tool Selection

**Spec:** `docs/specs/018-chat-assistants-and-tool-selection.md` (Approved 2026-11-11)
**Architecture:** Modular Monolith; Vertical Slice inside the Agents and Conversations modules.
**Mode:** `/tdd`: every acceptance criterion becomes a failing test first.
**Status:** Implemented (2026-11-11)
**Affected:** Agents (Domain, Persistence, Chat, Features/Assistants, Features/Agents), Agents.Contracts, Conversations (Domain, Persistence, Features), `Quorum.Web.Client` (Chat page, assistant form, agent tools editor, API client), `tests/Quorum.Integration.Tests`, `docs/database-schema.md`, `docs/product.md`.

## What the code already gives us
- `AssistantChat` (Agents) is the one place where a turn resolves the root agent, its endpoint, its active tools (`ToolPool.Of` -> `ActiveTool`, exposed name = `ExposedName`) and its sub-agents (`LoadSubAgentsAsync`, which inherits the root endpoint when a sub-agent has none, spec 013 D-12). The effective tool set and the endpoint override are both applied there, so sub-agents inheriting the endpoint follow for free.
- `IAssistantChat` and `IAssistantCatalog` (`Quorum.Modules.Agents.Contracts`) are the only way Conversations talks to Agents; `ConversationEndpoints` passes `conversation.AssistantId` to `StartAnswerAsync` (send and regenerate) and `AttachmentEndpoints` to `MeasureContextBySizesAsync`. All of them gain the same options object.
- `GetDefaultAssistantIdAsync` is "the oldest assistant by id"; it is the wizard/settings assistant (spec 013 D-35) that D-18 protects.
- `Conversation` already stores `AssistantId` as a soft Guid, with a tenant and a user filter: a debug conversation of another user is a `404` with no new code.
- Tool attachment (`agent_tools` via `PUT /api/agents/{id}/tools`, `AgentToolsEndpoints`) already validates tools against the agent's visible pool; the mode rides on the same request.
- `GET /api/assistants` is used by the portal today; it becomes consumer-shaped, so the portal must move to `/api/admin/assistants` in the same PR that changes it.
- Note: the working tree of `refactor/query-volume-review` has uncommitted changes in `AssistantChat`, `ActiveTools`, `ToolPool` and the resolvers. Implementation PRs start from `main`; if that refactor lands first, rebase before PR 2.

## Decisions
| Topic | Choice |
|---|---|
| Where the selection is evaluated | One function in Agents, `ToolSelection.Effective(activeTools, disabledNames, isDebug)`: consumer: remove only names whose attachment is `Optional` (a required name in storage is ignored, D-17); debug: remove any name. Unknown names are ignored at run time, rejected at creation. Used by the turn and by the measure endpoints, so the context bar counts the tools actually sent. |
| What crosses the Contracts | `ChatTurnOptions(IReadOnlyList<string> DisabledTools, bool Debug, Guid? ModelEndpointId)` added to `StartAnswerAsync`, `MeasureContextAsync` and `MeasureContextBySizesAsync`; no MEAI/MAF type. A new `IChatAssistantAccess` in Contracts answers Conversations' questions: exposed assistants, tools of an assistant (consumer or admin view), validation of a selection (names, modes, `allowToolSelection`) and of an endpoint override (exists, tools supported, D-16). Conversations never reads Agents tables. |
| Tool name as id | The API speaks `ExposedName` (D-6). Mode lookup joins `agent_tools` (mode) with the pool row (name); two attachments cannot share a name (spec 015), so the name is a safe key. |
| Mode column | `agent_tools.Mode` text/enum, default `Required`, set by the migration for existing rows. `PUT /api/agents/{id}/tools` takes `{ tools: [{ toolId, mode }] }` in addition to the current `toolIds` (kept accepted, all `Required`) so older clients keep working; the response lists the mode. |
| Assistant flags | `Exposed`, `AllowToolSelection` columns; migration sets `Exposed = true`, `AllowToolSelection = false` for existing rows (column defaults are `false`/`false`, the migration backfills `Exposed`). `PUT /api/assistants/{id}` takes both; omitted keeps the stored value. De-exposing the oldest assistant (the default) returns `409` (D-18). |
| Conversation data | `Mode` (`Consumer` default, `Debug`), `DisabledTools` (jsonb text[]), `OverrideModelEndpointId?`. One migration of Conversations; existing rows become `Consumer` with an empty list. |
| Route layout | Consumer: `AssistantEndpoints` (Agents) keeps `/api/assistants`; the tools route is new. Admin: `/api/admin/assistants`, `/api/admin/assistants/{id}/tools` in Agents (`agents.manage`); `/api/admin/conversations` (POST, GET) and `PATCH /api/admin/conversations/{id}/overrides` in Conversations (`agents.manage`, the policy from `QuorumPolicies`). Conversations asks Agents through `IChatAssistantAccess` for anything about assistants. |
| Consumer list of conversations | `GET /api/conversations` filters `Mode == Consumer`; the admin list `Mode == Debug`. The per-id routes do not look at the mode. |
| Override changes in flight | `PATCH` rewrites the two columns before the next message; a turn in progress already holds its `ChatTurnOptions` (spec 013 AC-25 spirit). |
| Deleted override endpoint | Endpoint deletion is refused only for agents today; the turn fails with the existing "endpoint unreachable" style error mapped to a clear message (spec edge case). No change to endpoint deletion. |
| Test strategy | Integration tests with Testcontainers and the fake OpenAI-compatible endpoint that scripts the turns (as the tool tests do); one test per AC, named after it. Runtime tests at PR 2 call `IAssistantChat` with options directly; end-to-end ones at PR 4 go through the API. |

## Order and parallel work
- **PR 1** first: schema and management fields. Every other PR needs the columns.
- **PR 2** (runtime and Contracts) depends on PR 1.
- **PR 3** (assistant routes) depends on PR 1 and PR 2's `IChatAssistantAccess`; it can be developed in parallel with the runtime part of PR 2 once the interface is agreed, but merges after it.
- **PR 4** (Conversations) depends on PR 2 and PR 3 and also adapts the portal Chat page minimally, because `assistantId` becomes mandatory (D-9) and the page would otherwise break.
- **PR 5** (UI) depends on PR 1, 3 and 4; **PR 6** (documentation) closes the plan.
- Spec and plan are on a `docs/` branch with their own PR; implementation PRs start from `main` once it is merged. Every PR targets `main`.
- Only PR 1 (Agents) and PR 4 (Conversations) carry a migration, in different modules.

## Acceptance criteria map
| AC | PR |
|---|---|
| AC-1, AC-18, AC-20 (API part) | 1 |
| AC-11, AC-12, AC-17 (runtime level) | 2 |
| AC-2, AC-4 (empty list), AC-5, AC-9 | 3 |
| AC-3, AC-4 (400 on creation), AC-6, AC-7, AC-8, AC-10, AC-13, AC-14, AC-15, AC-16, and AC-11/12/17 end to end | 4 |
| AC-19, AC-20 (UI part) | 5 |

## Steps

### PR 1: Schema and management fields (`feat/018-assistant-flags`)
1. Domain: `Assistant.Exposed`, `Assistant.AllowToolSelection` (create defaults `false`, revise parameters); `AgentTool.Mode` (`ToolMode` enum `Required`, `Optional`) with `SetMode`.
2. `AgentsDbContext` configuration and the migration `AddChatExposure`: new columns; backfill `Exposed = true` for existing assistants (AC-1); existing `agent_tools` rows `Required`.
3. `AssistantEndpoints`: request and response carry `exposed` and `allowToolSelection`; `409` when de-exposing the default assistant (oldest by id) (AC-18); validation of the root as before.
4. `AgentToolsEndpoints`: `GET` returns `mode`; `PUT` accepts `tools: [{ toolId, mode }]` or the old `toolIds` (all `Required`); invalid mode is `400`.
5. Tests: AC-1, AC-18, AC-20 (the fields persist and are returned).

### PR 2: Runtime and Contracts (`feat/018-selection-runtime`)
6. `Quorum.Modules.Agents.Contracts`: `ChatTurnOptions`; `StartAnswerAsync`, `MeasureContextAsync`, `MeasureContextBySizesAsync` take it (default empty for existing callers during the transition); `IChatAssistantAccess` with the operations of Decisions, plus DTOs (`ChatAssistant`, `ChatTool`, `ToolMode`).
7. `ToolSelection.Effective(...)` and its use in `AssistantChat.ResolveDefinitionsAsync` for the root tools only (sub-agent tools untouched, D-5), also on the `forMeasure` path.
8. Endpoint override: `LoadConfigurationAsync` / `LoadAssistantAsync` take `ModelEndpointId` from the options when set; validated (exists in the tenant, tools supported when tools or sub-agents remain, D-16); sub-agents without an endpoint inherit it through the existing path.
9. `ChatAssistantAccess : IChatAssistantAccess` registered in `AgentsModule`: lists, tool views (consumer view empty when `AllowToolSelection` is off), selection validation with the error lists of the spec.
10. Tests at the `IAssistantChat` level with the fake endpoint: the debug option removes a required tool (AC-11), the override endpoint is used by root and inheriting sub-agent (AC-12), a tool made required after creation is back on (AC-17).

### PR 3: Assistant routes (`feat/018-assistant-routes`)
11. Consumer: `GET /api/assistants` lists exposed assistants only with `{ id, name, allowToolSelection }`; `GET /api/assistants/{id}/tools` (`404` for not exposed, empty when selection off) (AC-2, AC-4, AC-5).
12. Admin: `GET /api/admin/assistants` (all, with flags and root slug), `GET /api/admin/assistants/{id}/tools` (all root tools with mode), policy `agents.manage` (AC-9, `403` for other users).
13. Portal: `QuorumApi` assistant calls move to `/api/admin/assistants` and read the new fields, so the pages that list assistants keep working; the assistant form flags come in PR 5.
14. Tests: AC-2, AC-4 (empty list), AC-5, AC-9.

### PR 4: Conversations (`feat/018-conversation-selection`)
15. Domain: `Conversation.Mode`, `DisabledTools`, `OverrideModelEndpointId`; `Start` takes them. Migration `AddConversationSelection` (existing rows `Consumer`, empty list).
16. `POST /api/conversations`: `{ assistantId, disabledTools? }`; `assistantId` mandatory (AC-3); validation through `IChatAssistantAccess` (`404` not exposed, `400` invalid or sent while selection is off, AC-4, AC-7). Stop using `GetDefaultAssistantIdAsync` here (D-9).
17. `GET /api/conversations` filters `Consumer`; `GET /api/conversations/{id}` returns `assistantId`, `disabledTools` and `debug` (AC-8, D-14).
18. Admin: `POST /api/admin/conversations`, `GET /api/admin/conversations`, `PATCH /api/admin/conversations/{id}/overrides` (`404` for a consumer conversation, AC-15), validation of D-16 (AC-13), changes visible on the next message (AC-14); `agents.manage` policy.
19. `SendMessage`, `Regenerate` and the attachment measures build `ChatTurnOptions` from the conversation (disabled tools, `Debug`, override) (AC-6, AC-10, AC-11, AC-12, AC-16, AC-17 end to end).
20. Portal Chat page minimal change: it lists `/api/admin/assistants`, creates through `/api/admin/conversations`, and lists `/api/admin/conversations` in its sidebar, so the portal does not regress; the debug panel is PR 5.
21. Tests: AC-3, AC-4, AC-6 to AC-8, AC-10, AC-13 to AC-17.

### PR 5: Admin UI (`feat/018-ui`)
22. `ApiModels`/`QuorumApi`: flags, tool mode, admin conversation and override calls.
23. Assistant form: `Exposed` and `AllowToolSelection` switches with a short explanation of each; the list shows both as chips.
24. Agent tools editor (`AgentToolPicker`): per-tool `Required`/`Optional` selector, default `Required`, saved with the tools (AC-20).
25. Chat page as debug chat (D-20): assistant selector (all assistants), model-endpoint override selector, tool panel with a toggle per root tool (required ones included, marked), applied to the next message through the `PATCH`; the conversation shows its current overrides (AC-19).
26. Component tests for the tool rows and the panel; manual check of the debug chat in the browser pane.

### PR 6: Documentation (`docs/018-docs`)
27. `docs/database-schema.md` (new columns), `docs/product.md` (consumer and admin routes, required/optional tools), `docs/deferred.md` (public API item points to this spec for `exposed` and `disabledTools`). Roll the plan status to Implemented.

## Risks
- **Breaking change** of `POST /api/conversations` (mandatory `assistantId`) and of `GET /api/assistants` (consumer shape): only the portal consumes them today; PR 3 and PR 4 move it in the same PRs.
- **Contracts change** touches every caller of `IAssistantChat`: the transition default keeps PR 2 reviewable alone; PR 4 removes the default for conversation callers.
- **Refactor in flight** on `AssistantChat`/`ToolPool` in another branch (see above).

## Open Questions
None.
