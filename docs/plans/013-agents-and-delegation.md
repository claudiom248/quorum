# Plan 013: Agents and Delegation

**Spec:** `docs/specs/013-agents-and-delegation.md` (Approved 2026-11-03, amended the same day while planning)
**Architecture:** Modular Monolith; Vertical Slice inside each module.
**Mode:** `/tdd`: every acceptance criterion becomes a failing test first.
**Status:** Draft
**Affected:** Agents (Domain, Persistence, Chat, Tools, Features, Provisioning, Contracts), Conversations (Streaming), `Quorum.Web.Client` (Settings, Chat, scope forms), `tests/Quorum.Integration.Tests`, `docs/database-schema.md`.

## Decisions
| Topic | Choice |
|---|---|
| Where the runtime reads its configuration | `AssistantChat.PrepareAsync` resolves assistant -> root agent -> endpoint, prompt and tools. `ToolPool.For`, `ActiveToolsQuery` and the invoker's call-time re-check take an agent id instead of an assistant id. |
| Scope rename (D-17) | `ToolScopeKind.Assistant` becomes `Agent` and `ScopeAssistantIds` becomes `ScopeAgentIds` on `mcp_servers`, `rag_tools`, `lookup_tools`, `web_search_tools`, `web_fetch_tools`. Knowledge base and lookup scopes (KnowledgeBases module) stay on assistants (D-41); the runtime passes the assistant of the turn to them, also for sub-agents. |
| Migration | One EF Core migration of the Agents module: creates `agents`, `agent_sub_agents`, `agent_tools` (with `Manifest`), adds `RootAgentId`, `MaxParallelSubAgents` to `assistants`, `Name` to `model_endpoints`, `AgentId` to `tool_audit_entries`; copies each assistant to an agent and its activations to `agent_tools`; maps scope ids; then drops `SystemPrompt`, `ModelEndpointId` and `assistant_tools`. Slug from the assistant name, numeric suffix on collision. |
| Delegation tool | A new `QuorumTool` (`AgentQuorumTool`) per sub-agent edge, named by the child slug, one string argument `task`. It runs through `ToolCallInvoker`, so size limit (D-29), audit, and cancellation come for free; it is exempt from `CallTimeout` (D-39). |
| Sub-agent run | A `ChatClientAgent` over its own `OpenAiChatClient` / `ContextGuardChatClient` (the child's endpoint, or the root's when none), its own `FunctionInvokingChatClient` with the child's tools and its own `MaxRounds`, the shared total deadline, no history except the task, no delegation tools and no `ask_user` (D-4, D-5, D-40). |
| Parallelism (D-14, D-38) | `AllowConcurrentInvocation = true` on the root client; the invoker serializes ordinary tool calls with a per-run semaphore of 1 and admits delegation calls through a semaphore of `MaxParallelSubAgents`. Confirmations go through one run-level gate so one dialog is open at a time. |
| Audit and database access | Parallel delegations must not share one `AgentsDbContext`: the invoker takes an `IDbContextFactory` (or the audit writes go through one gate) so each call has its own context. |
| Events | `ToolCallEvent` and `ToolResultEvent` gain optional `ParentCallId` and `AgentSlug` (additive); Conversations forwards them in the stream; the Chat page nests rows under the delegation row. |
| API shape | As in the spec: `/api/model-endpoints`, `/api/agents` (edges in the document), `/api/agents/{id}/tools`, `/api/assistants` CRUD, `/api/settings/assistant` kept as a facade. Permission `agents.manage` in `QuorumPolicies`. |
| Test strategy | Integration tests with Testcontainers; a fake OpenAI-compatible endpoint scripts the root and sub-agent turns (as the existing tool tests do). One test per AC. |

## Order and parallel work
- **PR 1** (model endpoints) first: it is small and independent of the agent model.
- **PR 2** (agents foundation) depends on PR 1 only for the endpoint `Name` migration, so it follows it: two migrations in the same module must not be written in parallel.
- **PR 3** (agents API) and **PR 4** (assistants CRUD) depend on PR 2. They touch different features and can be developed in parallel, with PR 3 merged first because PR 4 validates against agent rules.
- **PR 5** (delegation runtime) depends on PR 2; edges are seeded through the DbContext in tests, so it can start before PR 3 lands, but merges after it.
- **PR 6** (UI) depends on PR 3, 4 and 5.
- **PR 7** (documentation) closes the plan.

## Acceptance criteria map
| AC | PR |
|---|---|
| AC-8 | 1 |
| AC-1, AC-22 | 2 |
| AC-2, AC-3, AC-4, AC-5, AC-6, AC-7, AC-24 (agent and endpoint writes), AC-26 | 3 |
| AC-23, AC-27, AC-24 (assistant writes) | 4 |
| AC-9, AC-10, AC-11, AC-12, AC-13, AC-14, AC-15, AC-16, AC-17, AC-18, AC-20, AC-21, AC-25, AC-28, AC-29, AC-30, AC-31 | 5 |
| AC-19 | 6 |

## Steps

### PR 1 — Model endpoints as a resource (`feat/013-model-endpoints`)
1. `QuorumPolicies.AgentsManage = "agents.manage"` held by `Admin`; registered where the other policies are.
2. `ModelEndpoint.Name` (required, unique per tenant); migration backfills a name from the model name with a numeric suffix on collision; the wizard and `AssistantProvisioning` generate one.
3. `ModelEndpointEndpoints` (`IEndpointGroup`, `agents.manage`): list, create, update, delete, `POST /test` reusing `IModelEndpointProbe`. Update refuses (400) a change to `SupportsTools = false` when agents in use have tools or sub-agents (the check is wired in PR 3, when agents exist). Delete returns `409` listing the assistants using it for now (agents in PR 3).
4. Tests: AC-8 (assistant as the referencing entity until PR 3), name uniqueness, key handling (omitted keeps, empty removes).

### PR 2 — Agents foundation, behaviour preserved (`feat/013-agents-foundation`)
5. Domain: `Agent`, `AgentSubAgent`, `AgentTool` (with `Manifest`); `Assistant` gets `RootAgentId` and `MaxParallelSubAgents` (default 1) and loses `SystemPrompt` and `ModelEndpointId`. `ToolScopeKind.Agent`, `ScopeAgentIds` on the five tool tables.
6. `AgentsDbContext` configuration and the migration of the Decisions table, with the data copy in SQL inside the migration. Test the migration against a database seeded with the old schema (AC-1).
7. Runtime: `AssistantChat.PrepareAsync`, `ToolPool.For`, `ActiveToolsQuery`, the invoker's re-check (`ToolCallInvoker` line ~363), `ToolRunContext` (adds `AgentId`) and the audit entry (`AgentId`, D-28) read the root agent. `MeasureContext*` use the same path.
8. Provisioning and facade: `AssistantProvisioning` creates endpoint, root agent and assistant; `/api/settings/assistant` reads and writes the root agent and its endpoint (D-35, AC-22). `AssistantDirectory` and `GET /api/assistants` return the new shape.
9. Keep `GET/PUT /api/assistants/{id}/tools` as a facade over the root agent's tools so the UI keeps working; add read-only `GET /api/agents` for the scope pickers; adapt `ScopeForm`, `ScopeView`, `ApiModels` and `ToolAdminView` to agent ids.
10. Tests: every existing Agents and Conversations test passes unchanged in behaviour; AC-1, AC-22.

### PR 3 — Agents API (`feat/013-agents-api`)
11. `AgentEndpoints` (`agents.manage`): list, get, create, update (atomic `subAgents` replacement), delete with `409` and the reference list (D-16).
12. `AgentRules`: slug format and uniqueness (D-18), no self edge, sub-agent slug vs tool name clash (AC-26), `SupportsTools` check (D-19, also from the endpoint update of PR 1), scope visibility between parent and child (D-21) and for the root of an assistant (D-25), `409` on narrowing.
13. `/api/agents/{id}/tools` (`tools.attach`) replaces `/api/assistants/{id}/tools`; the facade of PR 2 is removed and the UI call sites move.
14. Endpoint deletion now also lists agents (AC-8 complete).
15. Tests: AC-2 to AC-7, AC-24, AC-26.

### PR 4 — Assistants CRUD (`feat/013-assistants-crud`)
16. `AssistantEndpoints` (`agents.manage` for writes, authenticated for `GET`): create, update (`rootAgentSlug`, `maxParallelSubAgents`), delete (D-36).
17. Validation: root visible to the tenant and with an endpoint (AC-23, D-12, D-25). Conversations: sending to a deleted assistant returns the clear error already used for a missing assistant, history stays readable (AC-27).
18. Tests: AC-23, AC-27, AC-24 for assistant writes.

### PR 5 — Delegation runtime (`feat/013-delegation-runtime`)
19. Contracts: `ToolCallEvent` and `ToolResultEvent` gain `ParentCallId` and `AgentSlug`; `AnswerStream` in Conversations forwards them (additive, existing clients unaffected).
20. `AgentQuorumTool` and its factory registration in `QuorumToolFactory`: one tool per sub-agent edge of the running agent, description from the child (D-9), argument `task`.
21. `SubAgentRunner`: builds the child's chat stack (D-4, D-5, D-11, D-12, D-40), runs it under the shared deadline and cancellation (D-22), returns the final message; its tool calls publish events with `ParentCallId`.
22. Invoker changes: delegation exempt from `CallTimeout` (D-39); failure policy per edge (`ReturnError` / `AbortTurn`, D-13); size rule applies (D-29); parallel admission (D-14, D-38), per-call `DbContext` (Decisions table), single confirmation gate (D-37).
23. Tests with the scripted fake endpoint: AC-9 to AC-18, AC-20, AC-21, AC-25, AC-28 to AC-31.

### PR 6 — Admin UI and ledger (`feat/013-agents-ui`)
24. Agents page (list, form: prompt, endpoint, tools, sub-agents with failure policy, scope; warning above 10 sub-agents plus tools, D-30) and Model endpoints page; root agent and parallelism on the assistant; Settings keeps working through the facade.
25. `ToolCallGroup` nests rows by `ParentCallId` and shows the sub-agent slug and state (AC-19); the confirmation card works inside a nested row (AC-28).
26. Component tests for the nested ledger; manual check in the browser pane.

### PR 7 — Documentation (`docs/013-agents-docs`)
27. `docs/database-schema.md` (snapshot at the new migration, ER diagram, soft references), `docs/product.md` (glossary: no Topology, root agent), `docs/mcp-tools.md` where it names assistant activation, and the status lines of spec and plan.

## Risks
- **Migration data copy** is the highest risk (AC-1): test it on a database built from the previous snapshot with assistants, activations and scoped tools; keep the old columns until the copy is verified inside the same migration.
- **Parallel delegation and EF**: `ToolCallInvoker` takes a scoped `AgentsDbContext`; parallel calls would share it. Handled in step 22; a regression test runs two delegations at once.
- **`FunctionInvokingChatClient` concurrency**: `AllowConcurrentInvocation` is per client, so ordinary tools must be serialized by our own semaphore (step 22).
- **Tool-call timeout and total time**: delegations share the answer's total time; a long delegation consumes it, which is the intended bound (D-39).
- **Scope rename** touches five tool tables and the Web client; done in PR 2 with the UI adapted in the same PR so `main` always works.
- **Context budget of a sub-agent** uses its own endpoint's `ContextTokenBudget`; a sub-agent on a smaller model can fail its task: the error reaches the caller as in D-13.

## Open Questions
None.
