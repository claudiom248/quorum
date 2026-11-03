# Spec 013: Agents and Delegation

**Status:** Approved (2026-11-03), amended 2026-11-03 while planning (D-11, D-15, D-31, D-39 to D-42, AgentTool, AC-20)
**Date:** 2026-11-02
**Amended by spec 015 (2026-11-03):** the slug of an agent is also a name the model reads, so it may not equal the name of a tool, whatever the case (spec 015 D-5); a delegation is told apart from a tool by `isDelegation` on the `tool_call` event (spec 015 D-7).
**Amended (2026-11-04):** D-43, the ledger style of the chat as a preference of the person.

Replaces the "005 - Declarative config and topology" entry of `docs/deferred.md`. The declarative file format for assistants and agents is a separate, later spec; this one covers the entities, the API, the admin UI and the multi-agent execution.

## Problem

Today an assistant is a single implicit agent: its system prompt and model endpoint live on the `Assistant` entity, the model endpoint is managed inside `/api/settings/assistant`, and there is effectively one default assistant. Every capability has to be packed into one prompt with one tool list. Operators need specialised, reusable agents (e.g. "Contracts expert", "Web researcher") that an assistant can delegate to, without the user ever talking to anyone but the assistant.

## Decisions

- **D-1**: Agents are a first-class entity. Each carries its own system prompt, model endpoint, tools and permissions (spec 004), and can be reused by several assistants.
- **D-2**: There is **no `Topology` entity**. The graph is the set of edges declared on agents; an assistant points to a root agent.
- **D-3**: Delegation pattern is **agent-as-tool**: the caller invokes a sub-agent like a tool and receives its final message as the tool result. Parallel fan-out happens when the model calls several sub-agents in one turn.
- **D-4**: A sub-agent receives **only the task text** written by the caller, never the conversation. It runs in its own context and returns a single final message.
- **D-5**: **No nesting.** Only the root agent delegates; a sub-agent cannot call other sub-agents, so cycles and depth limits do not exist. Tools are configured per agent and the same tool can be attached to several agents, so a sub-agent that needs a tool owns it directly. Relaxing the rule later is backward compatible, tightening it is not.
- **D-6**: Existing assistants are migrated: each gets a root agent holding its current system prompt, model endpoint and tools; those fields are removed from `Assistant`.
- **D-7**: **Handoff is out of this spec.** Direction for the follow-up spec: the user sees which agent is answering, and the current agent decides when to hand the conversation back. The design of 013 must not preclude it.
- **D-8**: Design constraint: the data model must be serialisable to the future declarative format without restructuring (stable names for cross-references, no runtime state mixed into definitions).
- **D-9**: The "when to call this agent" description lives on the agent (one description, reused everywhere) and becomes the tool description seen by the caller.
- **D-10**: An agent can be the root of an assistant and also a sub-agent of another. When it acts as a sub-agent it does not delegate (D-5).
- **D-11**: Sub-agent runs appear in the live tool-call ledger (#146) nested under the delegation call. As for ordinary tools today, a reloaded conversation does not show the ledger: `Message` stores only text, status and sources.
- **D-12**: The model endpoint of a sub-agent is optional and, when omitted, inherited from the assistant's model, that is the root agent's endpoint once D-6 has moved it there. The root agent's endpoint is therefore mandatory.
- **D-13**: When a sub-agent fails (endpoint unreachable, tool error, timeout), the failure policy is configured **per edge**. When omitted, the default is to return a readable error as the result of the delegation tool, so the caller can retry, use something else or answer with what it has. The alternative policy makes the failure abort the turn.
- **D-14**: The limit of sub-agents running in parallel is configured **per assistant**.
- **D-15**: There is no dedicated timeout for a delegation; only the timeouts of the model calls and of the tools apply. See D-39 for how this meets the per-call timeout of spec 004.
- **D-16**: Deleting an agent that is the root of an assistant or a sub-agent of another is refused with `409 Conflict`, listing the assistants and agents that reference it.
- **D-17**: Tools attach to agents (`agent_tools` replaces `assistant_tools`). The tool visibility scope `Assistant` is replaced by `Agent` (`ScopeAgentIds` replaces `ScopeAssistantIds`); the migration maps each assistant id to the id of its new root agent.
- **D-18**: An agent is identified by a **slug unique per tenant** (like `mcp_servers.Name`), which is also the name of the delegation tool seen by the calling model, plus a free `DisplayName` for the UI. The slug is the stable cross-reference of the future declarative format (D-8).
- **D-19**: Saving an agent with tools or sub-agents on a model endpoint with `SupportsTools = false` is refused with a validation error. The same check applies when the endpoint of an agent already in use changes.
- **D-20**: Agents have a **scope** like MCP servers (`Global`, `Tenant`), on top of the owning `TenantId`.
- **D-21**: A sub-agent must be visible at least as widely as its parent: a `Global` parent accepts only `Global` children; a parent limited to some tenants accepts `Global` children or children whose scope includes those tenants. Violations are refused at save, and narrowing the scope of an agent in a way that breaks an edge is refused with `409`, as in D-16.
- **D-22**: Stopping a turn or closing the connection cancels the running sub-agents, propagating cancellation to their model calls and tool calls.
- **D-23**: The default limit of parallel sub-agents per assistant is **1** (sequential execution), configurable (D-14), as stated in `docs/product.md` for local hardware. The admin raises it for cloud endpoints.
- **D-24**: The edges to sub-agents live **inside the agent resource**: the agent document holds the list of sub-agents (slug and failure policy, D-13) and a `PUT` replaces it atomically.
- **D-25**: The root agent of an assistant must be visible to the assistant's tenant (`Global`, or a scope that includes it). Narrowing the scope of an agent in a way that breaks an assistant is refused with `409`, as in D-16 and D-21.
- **D-26**: Managing agents and their edges requires a new permission name `agents.manage`, held by `Admin` for now (spec 004 only defines and checks permission names). Attaching tools stays under `tools.attach`.
- **D-27**: The admin UI gets an Agents page (list and edit form: prompt, endpoint, tools, sub-agents) and the root agent selection on the assistant. No graph view.
- **D-28**: Tool audit entries record the invoking `AgentId` in addition to `AssistantId`, also for tool calls made by sub-agents.
- **D-29**: The result of a delegation obeys the same size rule as any tool result (spec 004 D-10, consistent with spec 002): the limit (`ToolSettings.MaxResultCharacters`, enforced by `ToolCallInvoker` for every tool call) therefore also covers delegations; a final message larger than it is **not truncated**; the caller receives a "result too large" error and may retry asking for a shorter answer.
- **D-30**: There is no cap on the number of sub-agents of an agent; the admin UI warns when sub-agents plus tools exceed **10**. The warning is informational, not a limit.
- **D-31**: The durable trace of a delegation is the tool audit log (spec 004 D-12): the delegation call with its task as arguments and its outcome, and the tool calls of the sub-agent with their `AgentId` (D-28). Neither the result content nor the intermediate model text of the sub-agent is stored. Storing the ledger per message is a separate spec.
- **D-32**: During a delegation the user sees only the ledger entry of the sub-agent with its state (in progress, done, failed) and, nested, its tool calls as they arrive; the sub-agent text is not streamed to the user. Streaming it can be added later without breaking anything.
- **D-33**: Model endpoints become their own resource (`/api/model-endpoints`); agents reference them by id and a key is stored once.
- **D-34**: The 013 adds create, read, update and delete of assistants (name, root agent, parallelism limit). Choosing the assistant in the chat UI stays out.
- **D-35**: `/api/settings/assistant` and the first-run wizard of spec 001 stay as a facade over the root agent (and its endpoint) of the default assistant, so existing installations and the approved wizard keep working.

- **D-37**: Tools under the `confirm` policy (spec 004 D-6, D-9) are allowed inside sub-agents. The confirmation dialog appears in the nested ledger entry with the full arguments and the sub-agent slug, and the sub-agent waits; denial or the 5-minute timeout returns "denied by the user" to the sub-agent. With several pending confirmations, one dialog at a time.
- **D-39**: A delegation call is exempt from `ToolSettings.CallTimeout` (30 s by default), which would cut almost every delegation on a local model. It runs under the total-time limit of the answer; the tool calls inside the sub-agent each keep their `CallTimeout`, and the sub-agent has its own `MaxRounds`.
- **D-40**: The built-in `ask_user` tool is not offered to an agent running as a sub-agent. If the sub-agent lacks something, it says so in its result and the root agent decides whether to ask the user. Confirmation of tools (D-37) is unaffected.
- **D-41**: The scopes of knowledge bases and lookups (specs 003, 007) stay on assistants. The tools of a sub-agent are resolved against the assistant of the turn.
- **D-42**: The per-activation lookup manifest (spec 012 D-8) moves with the activation to `agent_tools`.
- **D-43**: How the tool calls and sub-agents of an answer are drawn in the chat (D-11, D-32) is a **preference of the person**, kept with their account: `GET /api/me/preferences` returns `{ "ledgerStyle": "classic" }` and `PUT /api/me/preferences` takes `ledgerStyle` (`classic`, `cards`, `timeline` or `bars`; omitted keeps it, `classic` is the default). It is chosen in Settings, Display, and follows the person from one browser to another; it is not a cookie. The four styles show the same calls (a sub-agent is one entry with its state and, nested, its calls; its text stays hidden, D-32). The classic ledger numbers its top level only.
- **D-38**: Spec 004 D-22 (tool calls of a round run one at a time) stays for ordinary tools. Delegation calls of the same round run in parallel up to the per-assistant limit (D-14); confirmations stay one at a time.
- **D-36**: Deleting an assistant is allowed even when it has conversations. The conversations keep their soft `AssistantId` reference, stay readable, and new messages are refused with a clear error.

## Scope

### In

- Agent entity, sub-agent edges on the agent, assistant -> root agent reference, data migration of existing assistants.
- Model endpoints as a resource.
- CRUD of agents, model endpoints and assistants; tools attached to agents.
- Agent-as-tool execution (sequential or parallel, per assistant limit).
- Admin UI: Agents page, Model endpoints page, root agent and parallelism on the assistant.
- Ledger nesting of sub-agent runs and `AgentId` in the tool audit.

### Out

- Handoff and the active-agent state in Conversations (follow-up spec).
- Declarative file format and visual graph editor.
- Nested delegation.
- Choosing the assistant in the chat UI.
- Per-user permission checks on sub-agent tool calls (see Deferred Decisions).
- Streaming the sub-agent text to the user.

## Domain Model

Module Agents (schema `agents`). Every entity carries `TenantId` with the global query filter.

| Entity | Fields | Notes |
|---|---|---|
| **ModelEndpoint** (existing, extended) | Id, TenantId, Name (new, unique per tenant), BaseUrl, ModelName, ProtectedApiKey?, ContextTokenBudget, SupportsTools, UpdatedAt | Created by the facade/wizard with a generated name; managed through `/api/model-endpoints` |
| **Agent** (new) | Id, TenantId, Slug (unique per tenant), DisplayName, Description, SystemPrompt, ModelEndpointId?, Scope (`Global`, `Tenant`), ScopeTenantIds, UpdatedAt | `ModelEndpointId` is null only for agents that always run as sub-agents and inherit the caller's endpoint (D-12) |
| **AgentSubAgent** (new) | ParentAgentId, ChildAgentId, FailurePolicy (`ReturnError` default, `AbortTurn`), TenantId | Edge of D-24; unique per pair; no self-edge |
| **AgentTool** (replaces `assistant_tools`) | AgentId, ToolId, TenantId, Manifest | D-17, D-42 |
| **Assistant** (changed) | Id, TenantId, Name, RootAgentId, MaxParallelSubAgents (default 1) | `SystemPrompt` and `ModelEndpointId` removed (D-6) |

- **Slug**: same format as `mcp_servers.Name` (short, lowercase letters, digits and hyphens), because it is the tool name seen by the model.
- **Delegation tool**: for each sub-agent of the running agent, one tool named by the child slug, described by the child's `Description`, with a single string argument `task`. Its result is the final message of the child (D-3, D-4).
- **Lifecycle**: agents and endpoints are created, edited and deleted by the admin; deletion is refused while referenced (D-16, plus endpoints in use by agents). There is no archive state.
- **Migration** (D-6, D-17): for each existing assistant, one agent is created with the assistant's prompt, endpoint and tools; its slug derives from the assistant name (suffix on collision); scope `Tenant` with the assistant's tenant; tool `ScopeAssistantIds` values are mapped to the new agent ids; `agents.tool_audit_entries.AgentId` stays null for existing rows.
- **Runtime state** is never stored on definitions (D-8). A turn uses the definitions as they were when it started.

## API Contract

All under `/api`, errors as `ProblemDetails`, validation errors as `ValidationProblem` (conventions of the existing endpoints).

| Verb | Route | Request | Response | Errors |
|---|---|---|---|---|
| GET | `/model-endpoints` | - | `[{ id, name, baseUrl, modelName, hasApiKey, contextTokenBudget, supportsTools }]` | 401; 403 |
| POST | `/model-endpoints` | `{ name, baseUrl, modelName, apiKey?, contextTokenBudget, supportsTools? }` | 201 endpoint | 400; 401; 403; 409 name taken |
| PUT | `/model-endpoints/{id}` | same; `apiKey` omitted keeps the key, empty removes it | 204 | 400; 404; 409 (D-19 violated by an agent using it) |
| DELETE | `/model-endpoints/{id}` | - | 204 | 404; 409 listing the agents using it |
| POST | `/model-endpoints/test` | `{ baseUrl, apiKey?, modelName? }` | `{ reachable, models, supportsTools }` | 400 (as the existing probe) |
| GET | `/agents` | - | `[{ id, slug, displayName, description, scope, modelEndpointId?, subAgentCount, toolCount }]` | 401; 403 |
| GET | `/agents/{id}` | - | agent document below | 404 |
| POST / PUT | `/agents`, `/agents/{id}` | agent document | 201 / 204 | 400 (slug, format, D-5, D-19, D-21, name clash); 404; 409 (scope narrowing, D-21, D-25) |
| DELETE | `/agents/{id}` | - | 204 | 404; 409 listing assistants and agents referencing it |
| GET / PUT | `/agents/{id}/tools` | `{ toolIds: [] }` | tool list | 400 (tool not visible to the agent, D-19); 404; policy `tools.attach` |
| GET | `/assistants` | - | `[{ id, name, rootAgentSlug, maxParallelSubAgents }]` | 401 (open to any authenticated user, as today) |
| POST / PUT | `/assistants`, `/assistants/{id}` | `{ name, rootAgentSlug, maxParallelSubAgents }` | 201 / 204 | 400 (root not visible, D-25; no endpoint on the root); 404 |
| DELETE | `/assistants/{id}` | - | 204 (D-36) | 404 |
| GET / PUT / POST | `/settings/assistant`, `/settings/assistant/test` | unchanged (D-35) | unchanged | unchanged |

Agent document:

```json
{
  "slug": "contracts-expert",
  "displayName": "Contracts expert",
  "description": "Answers questions on the company's contracts. Call it with the contract and the clause to check.",
  "systemPrompt": "...",
  "modelEndpointId": "uuid or null",
  "scope": "Tenant",
  "scopeTenantIds": ["uuid"],
  "subAgents": [{ "slug": "web-researcher", "failurePolicy": "ReturnError" }]
}
```

- `subAgents` references agents by slug (D-8, D-24); the `PUT` replaces the list atomically.
- `GET /assistants/{id}/tools` and its write counterpart are replaced by `/agents/{id}/tools` (the admin UI is the only client).

## Authorization

- Agents, model endpoints and assistants (write): permission `agents.manage` (D-26), held by `Admin`.
- Agent and endpoint reads: `agents.manage` (they expose prompts and connection data). `GET /assistants` stays open to authenticated users.
- Attaching tools to agents: `tools.attach` (unchanged).
- Tenant boundary: every entity filtered by `TenantId`; visibility of `Global` agents across tenants follows the rule already used for MCP servers (spec 004). A sub-agent must be visible at least as widely as its parent (D-21) and the root visible to the assistant's tenant (D-25).
- A sub-agent uses only its own tools, with the permissions of spec 004 attached to it. User-level checks on delegated calls are deferred (see Deferred Decisions).

## Edge Cases & Failure Modes

- **Slug**: duplicate within the tenant, or invalid format, is a validation error. An agent cannot be its own sub-agent.
- **Name clash**: a sub-agent slug equal to the name of a tool attached to the same parent is a validation error (the model sees one flat tool list).
- **Sub-agent with its own edges** acting as a sub-agent: its edges are kept but ignored at run time (D-5); the UI says so.
- **Endpoint without tools** (D-19): refused at save, also when an endpoint changes under agents already in use.
- **Sub-agent failure** (D-13): `ReturnError` hands the model a readable error as the tool result; `AbortTurn` ends the turn with an error to the user. Applies to unreachable endpoints, tool errors and timeouts of the model or tool calls (D-15).
- **Parallelism** (D-14, D-23): calls above the limit wait; with the default of 1 they run in the order requested.
- **Cancellation** (D-22): cancelling the turn cancels sub-agents; the ledger entries end as cancelled.
- **Result too large** (D-29): the caller receives the "result too large" error.
- **Concurrent edits**: a turn in progress keeps the definitions it started with; saving an agent never alters a running turn (proposed, confirm in review).
- **Deletions** (D-16): agent referenced, endpoint used by agents, scope narrowing that breaks an edge or a root: all refused with `409` and the list of references.
- **Migration**: slug collisions get a numeric suffix; an assistant without a valid endpoint cannot exist today, so no migrated agent lacks one.

## Non-Functional Requirements

- A delegation adds one model round trip to the sub-agent plus its tool calls; there is no new latency budget, the existing ones apply.
- No volume assumptions beyond tens of agents per tenant; the admin UI warns above a threshold (D-30).
- Sequential by default (D-23) for local hardware; the Microsoft Agent Framework / Extensions.AI types stay inside the Agents module and never cross `Quorum.Modules.Agents.Contracts`.
- Logs are structured (Serilog) and carry the agent slug and the delegation call id; no sub-agent text in logs at information level.
- The migration is a regular EF Core migration of the Agents module and must preserve every existing assistant's behaviour: same prompt, endpoint, tools.

## Integrations

- **Microsoft Extensions.AI / Agent Framework**: the delegation tool is an `AIFunction` that runs a `ChatClientAgent` for the child, on the child's `IChatClient`; own adapters only at the edges (spec stack rule).
- **Conversations (Contracts)**: ledger entries gain the agent slug and the id of the parent delegation call so nested runs can be shown (D-11, D-31); `AssistantId` references stay soft UUIDs.
- **Spec 004 (tools)**: scope `Agent`, `agent_tools`, `AgentId` in the audit; the size rule for results (D-29). Human confirmation of destructive tools inside a sub-agent run: see Open Questions.
- No events and no Wolverine usage: there is no asynchronous cross-module flow.

## Acceptance Criteria

- **AC-1** Given an installation with assistants from before this spec, When the migration runs, Then each assistant has a root agent with the same system prompt, endpoint and tools, and the conversations keep working with the same answers.
- **AC-2** Given an admin, When creating an agent with a valid slug, description, prompt and endpoint, Then it is stored and listed; a duplicate or malformed slug returns a validation error.
- **AC-3** Given an agent with a sub-agent edge, When saving it with itself as sub-agent, Then the request is rejected.
- **AC-4** Given a parent and a child on an endpoint with `SupportsTools = false`, When saving the parent, Then the request is rejected (D-19); the same when the endpoint is changed to one without tools.
- **AC-5** Given a `Global` parent and a `Tenant`-scoped child, When saving the edge, Then it is rejected (D-21).
- **AC-6** Given an edge between valid agents, When narrowing the scope of the child so the edge breaks, Then the request returns `409` listing the edge.
- **AC-7** Given an agent that is a sub-agent or the root of an assistant, When deleting it, Then `409` lists the references; unreferenced, it is deleted.
- **AC-8** Given a model endpoint used by agents, When deleting it, Then `409` lists the agents.
- **AC-9** Given a root agent with a sub-agent, When the user asks something the sub-agent covers, Then the model sees a tool named by the sub-agent slug with its description and a `task` argument, and calling it returns the sub-agent's final message.
- **AC-10** Given a delegation, When the sub-agent runs, Then it receives only the task text, not the conversation (D-4).
- **AC-11** Given a sub-agent without its own endpoint, When it runs, Then it uses the endpoint of the root agent (D-12).
- **AC-12** Given a sub-agent with its own sub-agents, When it runs as a sub-agent, Then it exposes no delegation tools (D-5).
- **AC-13** Given an agent with tools, When it runs as root or as sub-agent, Then only its own tools are available.
- **AC-14** Given `MaxParallelSubAgents = 1` and the model calling two sub-agents in one step, When they run, Then they run one after the other; with 2 they run together.
- **AC-15** Given a failing sub-agent with the default policy, When the model called it, Then the model receives a readable error as the tool result and the turn continues.
- **AC-16** Given an edge with `AbortTurn`, When the sub-agent fails, Then the turn ends with an error to the user.
- **AC-17** Given a final message of the sub-agent larger than the tool result limit, When returned, Then the caller receives the "result too large" error and nothing is truncated (D-29).
- **AC-18** Given a turn with running sub-agents, When the user cancels or the connection closes, Then the sub-agents and their model and tool calls are cancelled (D-22).
- **AC-19** Given a delegation, When shown in the ledger, Then the sub-agent appears as one entry with its state and, nested, its tool calls; its text is not shown (D-32).
- **AC-20** Given a delegation, When audited, Then the audit log holds the delegation call with the task as arguments and its outcome, and the sub-agent tool calls with their `AgentId`; neither result content nor intermediate sub-agent text is stored (D-31).
- **AC-30** Given a delegation that runs longer than `CallTimeout`, When the answer is within its total time, Then the delegation is not cut (D-39).
- **AC-31** Given an agent running as a sub-agent, When its tools are offered, Then `ask_user` is not among them (D-40).
- **AC-21** Given a tool call by a sub-agent, When audited, Then the entry records the sub-agent `AgentId` and the `AssistantId` (D-28).
- **AC-22** Given the first-run wizard or the Settings page, When saving the assistant model and prompt, Then they are written to the default assistant's root agent and its endpoint (D-35).
- **AC-23** Given an assistant whose root agent has no model endpoint, When saving it, Then the request is rejected (D-12).
- **AC-24** Given a user without `agents.manage`, When calling the agent, endpoint or assistant write endpoints, Then the response is `403`.
- **AC-25** Given a turn in progress, When an admin edits one of its agents, Then the running turn is not affected.
- **AC-26** Given a sub-agent slug equal to the name of a tool on the same parent, When saving, Then the request is rejected.
- **AC-28** Given a `confirm` tool attached to a sub-agent, When the sub-agent calls it, Then the user sees the confirmation in the nested ledger entry with the full arguments; approval runs the tool, denial or the timeout returns "denied by the user" to the sub-agent (D-37).
- **AC-29** Given a round with two delegation calls and two ordinary tool calls, When executed with a limit of 2, Then the delegations run in parallel and the ordinary tools one at a time (D-38).
- **AC-27** Given an assistant with conversations, When deleting it, Then it is deleted; opening one of its conversations shows the history and sending a message returns a clear error (D-36).

## Deferred Decisions

| Decision | Chosen fallback | Revisit trigger |
|---|---|---|
| Effective permissions of a sub-agent tool call: intersection of agent and user permissions, or agent only | Agent permissions only (spec 004). Users and roles are not managed yet (`deferred.md`). The user identity must stay available in the execution context so the intersection can be added. | Users and roles spec |
| Handoff on a stateless public endpoint (`/v1/chat/completions`) | The active agent is stored server-side per conversation; a stateless endpoint offers no handoff. The stateful public endpoint (`/v1/responses` mapped onto Conversations) is a separate future spec, see `docs/deferred.md`. | Handoff spec and public endpoint spec |
| Streaming the sub-agent text to the user | Not streamed (D-32). | Feedback on long delegations on slow local models |

## Open Questions

None.
