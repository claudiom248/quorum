# Spec 004: MCP Tools

**Status:** In Review (2026-10-22), because of the amendment below. Approved (2026-10-17).
**Amended (2026-10-22), pending approval:** D-18 no longer blocks addresses (cloud metadata, link-local): whoever registers a server is responsible for what they enter. The `tool.url_blocked` error and AC-3 are removed or rewritten accordingly.
**Amended by spec 003 (2026-10-17):** the tool origin `Native` is renamed `Builtin` and the exposed prefix `native__` becomes `builtin__`; "native tools" in this document are the built-in tools. Rows, API values and names in the code follow the new vocabulary.

**Date:** 2026-10-17

## Problem
Today an assistant can only produce text from its prompt, the conversation and the attached documents. It cannot look anything up, compute anything exactly or act on the customer's systems. Small local models (the first customer's reality) are especially weak at arithmetic, dates and anything outside their training, so they need tools. At the same time a tool is the first thing in Quorum that **does something**: it runs code or calls systems on the customer's infrastructure, driven by a model that can be wrong or manipulated by a document it reads. This spec gives assistants tools through the **Model Context Protocol (MCP)** and puts the safety rules of the product principles around them from day one: visibility scopes, human confirmation, an audit log.

Out of scope here are the things that need other pieces first: users and roles (a dedicated spec), the agents of a topology (spec 005), knowledge bases (003) and web search (006).

## Scope
### In
- **MCP client over Streamable HTTP** (D-1): a registry of MCP servers per tenant, registered and managed by an admin; tool discovery; execution of tool calls.
- **Native tools** executed in-process: current date and time, calculator, `ask_user` (D-15).
- **Tool-calling loop** on top of the OpenAI-compatible chat: the model asks for tools, Quorum runs them and returns the results, up to the limits of D-8, always streamed.
- **Visibility scopes** (global, tenant, assistant; agent reserved) and **explicit activation** of tools per assistant (D-2, D-5, D-23).
- **Per-tool policy** `auto` / `confirm` with **human confirmation** in the chat during the stream (D-6, D-9, D-20).
- **Review of new or changed tools** before they are offered to any model (D-14).
- **Audit log** of every tool call, readable through the API only (D-12, D-13, D-28).
- **Permission names** `tools.manage`, `tools.attach`, `tools.use`, held by `Admin` (D-7).
- **"Supports tools" flag** on the model endpoint, pre-filled by the connection test (D-11).
- UI: admin page for servers and tools, tool activation per assistant, tool-call cards and confirmation dialog in the chat, tools segment in the context gauge (D-19, D-27).

### Out
- stdio MCP servers registered from the UI (an `mcp-runner` sidecar, later spec); with this spec they are reachable only through a gateway run by the operator (D-1).
- OAuth flow for MCP servers (static headers only, D-21).
- MCP resources, prompts, sampling and elicitation (D-29).
- Users and roles beyond `Admin`; per-user tool permissions (D-7).
- Audit log UI (API only, D-19).
- Persisting tool calls in the conversation history (D-4).
- Agents as entities and the `agent` scope in use (spec 005); web search (006); knowledge base search (003).
- Native tools beyond the three in D-15 (e.g. queries over CSV attachments).
- Remembered confirmations (D-20) and parallel execution of tool calls (D-22).
- Multiple application instances: pending confirmations live in memory (see Edge Cases).

## Decisions
| # | Decision |
|---|---|
| D-1 | The MCP client in this spec speaks **Streamable HTTP only**. Quorum never spawns child processes: an MCP package would run with the same access as Quorum, including the key ring volume (the master key of stored secrets) and the database credentials. stdio servers are reachable through an external stdio-to-HTTP gateway run by the operator (e.g. supergateway as a compose sidecar, documented, not shipped). Registering stdio servers from the UI through a Quorum-controlled `mcp-runner` sidecar is a later spec (`docs/deferred.md`). |
| D-2 | A tool is made available through a **visibility scope**, not through a permission matrix: `global` (all tenants), `tenant` (one or more tenants), `assistant` (one or more assistants), `agent` (one or more agents). |
| D-3 | Besides tools coming from MCP servers, Quorum ships **native built-in tools** executed in-process. |
| D-4 | Tool calls are shown to the user **live while streaming** and are **not persisted** with the conversation (like the model's reasoning). Consequence: the stored answer is only the final text, and in later turns the model does not see earlier tool results. The audit log (D-12) is the only record. |
| D-5 | The schema models all four scopes. `assistant`, `tenant` and `global` are enforced and tested; the `agent` scope is reserved and rejected with `tool.scope_not_supported` until agents exist (spec 005). |
| D-6 | Human confirmation is a **per-tool policy**: every new tool starts as `confirm`; the admin sets `auto` per tool. MCP annotations (`readOnlyHint`, `destructiveHint`) come from the server and are untrusted: they are shown to the admin as information only. Native tools start as `auto`. |
| D-7 | This spec defines **permission names** only: `tools.manage` (register servers, review tools, set policies and scopes, read the audit log, change tool settings), `tools.attach` (activate visible tools on an assistant), `tools.use` (invoke tools by chatting with an assistant that has them). They are checked through ASP.NET authorization policies; today only `Admin` holds them. Who may use a tool is decided by access to the assistant, so there is no role × tool matrix. Users and roles (User, Builder, Admin) are a dedicated spec (`docs/deferred.md`). |
| D-8 | Limits of the tool-calling loop for one answer, defaults configurable by the admin: at most **8 rounds** (a round is one model call followed by its tool calls), **5 minutes** in total, **30 seconds** per tool call. When a limit is hit the answer ends as `Failed` with a readable error; text already received is kept. |
| D-9 | A tool call under the `confirm` policy pauses the stream and waits for the user's decision for up to **5 minutes**. Timeout, closing the stream or leaving the page counts as **denied**: the model receives "denied by the user" and no action runs without an explicit yes. |
| D-10 | A tool result is limited to a configurable size (default **20,000 characters**). A larger result is **not truncated**: the model receives an error saying the result is too large and may retry with narrower arguments (consistent with spec 002: no silent truncation). |
| D-11 | Model endpoints gain a **"supports tools"** flag. The connection test pre-fills it when the server reports the capability (e.g. Ollama's model details); otherwise it defaults to on and the admin can change it. If an assistant with tools uses a model that rejects the `tools` parameter, the answer fails with a clear message naming the flag; there is no silent fallback. Malformed tool calls (invalid arguments, unknown tool) are returned to the model as an error result and count as a round (D-8). |
| D-12 | **Audit log content** per tool call: who, when, assistant, conversation, server and tool, confirmation decision (auto / approved / denied / timed out), duration, outcome, and the **arguments** (values of known sensitive keys redacted). Of the result only the outcome and its size are stored, never its content. |
| D-13 | Audit retention: **90 days**, configurable by the admin; a periodic job purges older entries. Readable only with `tools.manage`; the API offers filters (period, tool, user, outcome) and no way to modify or delete entries. |
| D-14 | A server's tool list is read on registration and on an explicit "refresh". A tool that is **new, or whose name, description or input schema changed** (compared by hash) becomes **pending review**: it is not offered to any model until the admin reviews and enables it. A changed tool also goes back to the `confirm` policy. |
| D-15 | Native tools: **current date and time** (with timezone), **calculator** (safe expression evaluator, no code execution) and **ask_user** (the model asks the user a clarifying question, reusing the stream pause of D-9), all policy `auto`. |
| D-16 | Tool names sent to the model are **always prefixed** with their origin: `<server>__<tool>` for MCP tools, `native__<tool>` for native ones. Server names are restricted so that the full name matches `[a-zA-Z0-9_-]{1,64}`. |
| D-17 | The visibility scope is set **on the server** and applies to all its tools. Each tool can be individually enabled or disabled and has its own policy. |
| D-18 | MCP server URLs: **Quorum does not filter addresses** (amended 2026-10-22). The administrator who registers a server, and their colleagues, are responsible for what they enter; the internal network, link-local and cloud metadata addresses are all accepted. Redirects to a different host are not followed. |
| D-19 | UI in this spec: an admin page to register and refresh servers, review tools, set scope and per-tool policy; the activation of tools on the assistant; in the chat, tool-call cards and the confirmation dialog. The audit log is consulted through the API only; its UI is a later spec (`docs/deferred.md`). |
| D-20 | The confirmation dialog offers only **approve this call** or **deny**; choices are not remembered. To reduce prompts the admin sets the tool to `auto`. |
| D-21 | Authentication to HTTP MCP servers uses **static headers** (e.g. `Authorization: Bearer`), encrypted at rest with the master key and never returned by the API (only "set / not set"). The OAuth flow for MCP servers is a later spec (`docs/deferred.md`). |
| D-22 | Tool calls of one round run **one at a time, in the order the model emitted them**; each `confirm` call waits for its decision before the next starts. Parallel execution of `auto` calls can be added later without changing the API. |
| D-23 | The scope defines the **pool** of tools an assistant can use; nothing is active by default. Each assistant **explicitly activates** the tools it uses from its pool (`tools.attach`). A tool in the `global` scope does not appear by itself in any assistant. |
| D-24 | Native tools are activated on the default assistant created by the **new** setup wizard. Existing installations keep their behaviour on upgrade: the admin activates the tools from the settings. |
| D-25 | Registering a server that is unreachable succeeds: it is saved as **unavailable** with a visible warning (as the model endpoint in spec 001, AC-4b). An unavailable server offers no tools to the model; "refresh" reads its tools once it is back. |
| D-26 | No hard limit on active tools per assistant (large models cope with many). The UI **warns about the risks** of too many tools: lower accuracy of tool choice on small models and context consumed by tool definitions. The warning appears when the assistant has more than 15 active tools or the tool definitions take more than 20% of the usable context budget. |
| D-27 | The definitions of the active tools sent to the model (name, description, input schema) **count against the context budget**, after the system prompt and attachments; the context gauge of spec 002 shows them as a separate segment. |
| D-28 | If the audit entry of a tool call cannot be written, **the call does not run** (fail closed): the entry is written as `started` before execution and completed afterwards; on failure the model receives an error result. |
| D-29 | The client supports **tools only**. Other MCP capabilities (resources, prompts, sampling, elicitation) are not declared by the client and are ignored; sampling stays excluded for safety. |
| D-30 | `global` and multi-tenant scopes can be set by the `Admin` of the default tenant (the installation admin). Until multi-tenancy can be enabled they behave like the single tenant's scope. The installation operator role belongs to the users/roles spec. |
| D-31 | Tool output is **untrusted data**: it travels as a tool message, and the system prompt carries a fixed instruction that tool results and attached documents are reference material, never instructions to follow. The confirmation dialog shows the full arguments of the call so the user sees what will run. |

## Domain Model
Every application entity carries `TenantId`. Tools live in the **Agents** module (schema `agents`), which already owns the model endpoint and runs the model call. Conversations owns the stream and the user's decisions (see Module boundaries).

| Entity | Module | Main fields | Lifecycle |
|---|---|---|---|
| **McpServer** | Agents | Id, TenantId (owner), Name (unique per tenant, `[a-z0-9_-]`), Url, Headers (encrypted), Status (`Available` / `Unavailable`), LastError, LastRefreshedAt, Scope (`Global` / `Tenant` / `Assistant`), ScopeTenantIds, ScopeAssistantIds, CreatedAt, UpdatedAt | Created by registration; editable; **hard delete** removes its tools and their activations |
| **Tool** | Agents | Id, TenantId, ServerId (null for native), Origin (`Mcp` / `Native`), Name, Description, InputSchema, DefinitionHash, State (`PendingReview` / `Enabled` / `Disabled`), Policy (`Auto` / `Confirm`), Annotations (informational) | Created by discovery (MCP) or by an idempotent provisioner at startup (native, `Auto`); a tool the server stops listing is deleted on refresh |
| **AssistantTool** | Agents | AssistantId, ToolId, TenantId | The activation of D-23; removed with the tool or the assistant |
| **ToolSettings** | Agents | TenantId, MaxRounds (8), TotalTimeout (5 min), CallTimeout (30 s), ConfirmationTimeout (5 min), MaxResultCharacters (20,000), AuditRetentionDays (90) | One row per tenant, created with defaults |
| **ToolAuditEntry** | Agents | Id, TenantId, UserId, AssistantId, ConversationId, CallId, ServerName and ToolName (snapshots), Decision (`Auto` / `Approved` / `Denied` / `TimedOut`), Outcome (`Started` / `Succeeded` / `Failed` / `Denied` / `Cancelled` / `TimedOut`), Arguments (redacted JSON), ResultCharacters, DurationMs, ErrorCode, StartedAt, CompletedAt | Written as `Started` before execution, completed afterwards; never edited or deleted through the API; purged after the retention period |
| **ModelEndpoint** (existing) | Agents | gains `SupportsTools` (bool, default true) | Editable in settings; pre-filled by the connection test |

- **Visibility**: a tool is in an assistant's pool when its server is available, the tool is `Enabled`, and the server's scope covers the assistant's tenant (`Global`), lists the tenant (`Tenant`) or lists the assistant (`Assistant`). The `Agent` scope exists in the schema and is rejected.
- **Offered to the model**: pool ∩ activated (`AssistantTool`). Policy and state are checked again at call time.
- **Definition hash**: SHA-256 of name, description and canonicalized input schema. Annotations do not take part in it.
- **Native tools**: appear in the tools list as origin `Native`; their policy can be changed by the admin like any other. They cannot be deleted.
- **Module boundaries**: Conversations never reads tool entities. `IAssistantChat` (Agents.Contracts) streams, besides `Text` and `Reasoning` chunks, tool events (call started, confirmation requested, result, user question), and a new contract `IToolInteractions` accepts the user's decision or answer for a pending call. Conversations checks that the conversation belongs to the user and forwards the decision. `ContextUsage` gains a tools segment.

## API Contract
Errors use ProblemDetails with a stable `code`. All routes are under `/api`; authentication as in spec 001. Routes under `/tools` require `tools.manage`; activation requires `tools.attach`; chat routes require `tools.use`.

**Servers and tools (admin)**

| Verb | Route | Request | Response | Errors |
|---|---|---|---|---|
| POST | `/tools/servers/test` | `{ url, headers? }` | `{ reachable, tools[] }` | 400; 401; 403; 422 `tool.server_unreachable` |
| GET | `/tools/servers` | - | `[{ id, name, url, hasHeaders, status, lastError, scope, toolCount, pendingReviewCount }]` | 401; 403 |
| POST | `/tools/servers` | `{ name, url, headers?, scope }` | 201 `{ id, status, warning?, tools[] }` (saved even if unreachable, D-25) | 400 `tool.invalid_name` / `tool.scope_not_supported`; 401; 403; 409 `tool.server_name_taken` |
| GET | `/tools/servers/{id}` | - | server detail with its tools | 401; 403; 404 |
| PUT | `/tools/servers/{id}` | `{ name, url, headers?, scope }` (omitted `headers` keeps the stored ones) | 204 | 400; 401; 403; 404; 409 |
| DELETE | `/tools/servers/{id}` | - | 204 | 401; 403; 404 |
| POST | `/tools/servers/{id}/refresh` | - | `{ status, added[], changed[], removed[] }` | 401; 403; 404; 422 `tool.server_unreachable` |
| GET | `/tools` | - | `[{ id, origin, serverName, name, description, state, policy, annotations }]` (all tools of the tenant, native included) | 401; 403 |
| PUT | `/tools/{id}` | `{ state: Enabled / Disabled, policy: Auto / Confirm }` | 204 | 400; 401; 403; 404 |
| GET | `/tools/audit` | `from`, `to`, `tool`, `userId`, `outcome`, `page`, `pageSize` | `{ items[], total }` ordered by `startedAt` desc | 400; 401; 403 |
| GET | `/settings/tools` | - | `{ maxRounds, totalTimeoutSeconds, callTimeoutSeconds, confirmationTimeoutSeconds, maxResultCharacters, auditRetentionDays }` | 401; 403 |
| PUT | `/settings/tools` | same fields | 204 | 400; 401; 403 |

**Assistants**

| Verb | Route | Request | Response | Errors |
|---|---|---|---|---|
| GET | `/assistants` | - | `[{ id, name }]` (needed to pick assistants in a server's scope) | 401 |
| GET | `/assistants/{id}/tools` | - | `{ pool: [{ toolId, name, active }], warnings[] }` | 401; 403; 404 |
| PUT | `/assistants/{id}/tools` | `{ toolIds[] }` (replaces the active set) | 204 | 400 `tool.not_in_pool`; 401; 403; 404 |

**Model endpoint settings (spec 001 routes)**: `GET/PUT /settings/assistant` gain `supportsTools`; `POST /settings/assistant/test` and `POST /setup/model/test` return `supportsTools` as `true`, `false` or `null` (unknown).

**Chat**

| Verb | Route | Request | Response | Errors |
|---|---|---|---|---|
| POST | `/conversations/{id}/messages` and `/regenerate` | as in spec 001 | the SSE stream gains the events below | as before, plus 400 `chat.tools_exceed_budget` |
| POST | `/conversations/{id}/tool-calls/{callId}/decision` | `{ decision: "approve" / "deny" }` | 204 | 400; 401; 404 (unknown, expired or not the user's conversation); 409 already decided |
| POST | `/conversations/{id}/tool-calls/{callId}/answer` | `{ text }` (for `ask_user`) | 204 | 400; 401; 404; 409 |

New SSE events (besides `reasoning`, `delta`, `done`):
- `tool_call` `{ callId, tool, arguments, requiresConfirmation, expiresAt? }`: the model asked for a tool; when `requiresConfirmation` is true the stream is paused until a decision or `expiresAt`.
- `tool_result` `{ callId, status: succeeded / failed / denied / cancelled / timed_out, content?, sizeCharacters }`: shown in the card, not stored.
- `ask_user` `{ callId, question, expiresAt }`: the stream is paused until the answer or `expiresAt`.

Server `headers` are returned as `hasHeaders` only. Conversations of another user or tenant return 404 (as in spec 001).

## Authorization
- **Permissions** (D-7): `tools.manage`, `tools.attach`, `tools.use`, held by `Admin`. Endpoints are protected by named authorization policies, never by a role name, so the users/roles spec only maps roles to permissions.
- **Tenant isolation**: servers, tools, activations, settings and audit entries carry `TenantId` with the global filter. A `Global` or multi-tenant server (D-30) is visible to other tenants through the scope rule, never writable by them.
- **Confirmations**: only the owner of the conversation can decide or answer a pending call; other users get 404.
- **Secrets**: server headers are encrypted with the master key, never returned and never written to logs or the audit log.
- **Outbound requests** (D-18): no address is refused. Redirects to a different host are not followed, every connection has a timeout and header values never reach logs or messages.

## Edge Cases & Failure Modes
- **Loop limits** (D-8): hitting rounds, total time or per-call time ends the answer as `Failed` with a readable error; the call in flight is cancelled and audited as `TimedOut`.
- **Confirmation pending** (D-9): timeout, stream closed or page left = denied. The conversation lock of spec 001 stays held while waiting, so another message is rejected with 409.
- **Restart or crash with a confirmation pending**: pending decisions live in memory only; the stream breaks, nothing runs, and the answer is stored as `Failed`/`Interrupted` as in spec 001. Single-instance deployment is assumed.
- **User stops the answer during a tool call**: the in-flight MCP call is cancelled on a best-effort basis (cancellation notification); the audit entry is completed as `Cancelled`; the answer is stored `Interrupted`.
- **Conversation deleted while a call is pending or running**: the answer, the call and the pending confirmation are cancelled (spec 001 rule).
- **Regeneration** re-runs the loop from scratch; tools run again and confirmation applies again. Earlier approvals are never reused (D-20).
- **Tool changed, disabled or removed during an answer**: state and policy are checked at call time; a call to a tool that is no longer active returns an error result to the model (`tool.unavailable`) and counts as a round.
- **Server unreachable, slow or answering with an error during a call**: the model receives an error result; the audit entry is `Failed`.
- **Malformed call** (unknown tool, arguments that do not match the schema): returned to the model as an error result; counts as a round (D-11).
- **Result too large** (D-10): error to the model, no truncation.
- **Context budget** (D-27): the definitions of the active tools are counted after the system prompt and attachments. If they do not fit, sending is rejected with `chat.tools_exceed_budget`, naming how many characters are missing and suggesting to deactivate tools (nothing is dropped silently). While the loop runs, the history plus the tool messages must keep fitting the usable budget; otherwise the answer ends `Failed` stating that the tool results filled the context.
- **Audit failure** (D-28): the call does not run; the model receives an error result.
- **Audit retention job** failing: logged, retried at the next run; never blocks chat.
- **Concurrent decisions** for the same call: the first wins, the second gets 409.
- **Server removed or renamed**: tools and activations are deleted with the server; audit entries keep the name snapshots. A rename changes the tool names the model sees (D-16) from the next answer.
- **Duplicate server name** in a tenant: 409 `tool.server_name_taken`.
- **Refresh** (D-14): new and changed tools become pending review; a tool that disappears is deleted together with its activations; the result lists `added`, `changed` and `removed`.
- **Native `ask_user` unanswered**: after the confirmation timeout the model receives "the user did not answer".
- **Model without tool support** (D-11): clear failure naming the flag.
- **Prompt injection** (D-31): a document or tool result that asks the model to call a tool cannot bypass the policy; `confirm` tools still stop for the user, who sees the full arguments.
- **Header values** and tool arguments never appear in application logs.

## Non-Functional Requirements
- No performance targets in this spec: tool-call overhead, time to first token with tools and concurrent users are measured and targeted later.
- No outbound telemetry; the only outbound traffic added is to the MCP servers the admin registers.
- Structured logs: server id, tool name, call id, outcome and duration only. Never arguments, results, headers or file contents; arguments exist only in the audit log (D-12).
- The audit log write precedes each call (D-28): one extra database write per call, accepted for traceability.
- Connecting to an MCP server has a timeout (10 seconds) separate from the per-call timeout; a registration test and a refresh respect it.
- Streaming stays Server-Sent Events.

## Integrations
- **MCP servers**: Streamable HTTP transport of the MCP specification (JSON-RPC: `initialize` and `notifications/initialized`, `tools/list` with pagination, `tools/call`, and the cancellation notification). The client declares only the tools use of the protocol (D-29); `tools/list_changed` notifications are ignored (changes are picked up on refresh, D-14). The protocol revision negotiated and the choice between the official C# SDK and a minimal own client are settled in the plan.
- **OpenAI-compatible model endpoint**: `tools` in the request, `tool_calls` fragments in the streamed deltas (arguments arrive in pieces and are assembled), `role: tool` messages with `tool_call_id` for results. Verified at least with Ollama; the capability detection for `supportsTools` (D-11) is verified in the plan.
- **PostgreSQL**: new tables in the `agents` schema (servers, tools, activations, settings, audit entries).
- No events published externally.

## Acceptance Criteria
**Servers and tools**
- **AC-1** Given an admin and a reachable MCP server, When registering it, Then the server is saved as `Available` and its tools are listed as `PendingReview` with policy `Confirm`.
- **AC-2** Given an unreachable server, When registering it, Then it is saved as `Unavailable` with a warning and offers no tools.
- **AC-3** Given a server URL pointing to a link-local or cloud metadata address (e.g. `169.254.169.254`), When registering or testing it, Then it is not refused for its address: the outcome is the one of any other address (an unreachable server is saved as unavailable, D-25). *(Replaces the former `tool.url_blocked` rule.)*
- **AC-4** Given a server that answers with a redirect to a different host, When connecting, Then the redirect is not followed.
- **AC-5** Given a duplicate or invalid server name, When registering, Then it returns 409 `tool.server_name_taken` or 400 `tool.invalid_name`.
- **AC-6** Given stored headers, When the server is read, Then only `hasHeaders` is returned; the value is encrypted in the database and absent from logs.
- **AC-7** Given a registered server whose tool changed name, description or schema, When the admin refreshes it, Then that tool becomes `PendingReview`, returns to `Confirm` and is no longer offered to any model; new tools are pending review and vanished tools are removed with their activations.
- **AC-8** Given a `PendingReview` tool, When the admin enables it, Then it can be activated on an assistant in the scope.
- **AC-9** Given an MCP `readOnlyHint` or `destructiveHint`, When the tool is listed, Then it is shown as information and does not change the policy.
- **AC-10** Given a user without `tools.manage`, When calling any `/tools` or `/settings/tools` route, Then it returns 403; unauthenticated returns 401.

**Scope and activation**
- **AC-11** Given a server with scope `assistant` listing one assistant, When another assistant's pool is read, Then the server's tools are not in it.
- **AC-12** Given a server with scope `global` or `tenant`, When the pool of an assistant of an included tenant is read, Then its enabled tools are in the pool but not active.
- **AC-13** Given a registration with scope `agent`, When saved, Then it returns 400 `tool.scope_not_supported`.
- **AC-14** Given a tool not in the pool or not enabled, When activating it, Then it returns 400 `tool.not_in_pool`.
- **AC-15** Given an assistant with no active tools, When a message is sent, Then the model request carries no `tools`.
- **AC-16** Given a new installation, When the setup completes, Then the native tools are active on the default assistant; given an upgraded installation, Then no tool is active until the admin activates it.
- **AC-17** Given more than 15 active tools or definitions above 20% of the usable budget, When the assistant's tools are read, Then `warnings` explains the risk and nothing is blocked.
- **AC-18** Given tenant A's server, When tenant B reads servers or tools, Then it sees nothing of A's unless the scope includes B.

**Tool-calling loop**
- **AC-19** Given an active native tool and a model asking for it, When answering, Then Quorum runs it, sends the result back and streams the final text; the stream carries `tool_call` and `tool_result` events.
- **AC-20** Given active tools, When the request is built, Then names are prefixed (`<server>__<tool>`, `native__<tool>`) and the definitions count in the context usage as a separate segment.
- **AC-21** Given definitions that do not fit the budget with the system prompt and attachments, When sending, Then it returns 400 `chat.tools_exceed_budget` and nothing is stored.
- **AC-22** Given a model that keeps asking for tools, When 8 rounds are reached (or the configured value), Then the answer ends `Failed` with a readable error and the text so far is kept.
- **AC-23** Given a tool call exceeding the call timeout, When it is reached, Then the call is cancelled, the model receives an error result and the audit entry is `TimedOut`.
- **AC-24** Given a total time above the limit, When reached, Then the answer ends `Failed`.
- **AC-25** Given a model that asks for an unknown tool or sends arguments that violate the schema, When handled, Then it receives an error result and the round counts.
- **AC-26** Given a result larger than the limit, When returned by a tool, Then the model receives a "result too large" error and nothing is truncated.
- **AC-27** Given several tool calls in one round, When executed, Then they run one at a time in the order emitted.
- **AC-28** Given tool results, When later turns are built, Then they are not part of the history (D-4); the stored message is the final text only.
- **AC-29** Given a model that rejects the `tools` parameter, When an assistant with tools answers, Then the answer fails with a message naming the "supports tools" flag.
- **AC-30** Given a model endpoint test against a server that reports tool capability, When testing, Then `supportsTools` is returned `true` or `false`; otherwise `null`.
- **AC-31** Given tool results or documents containing instructions, When the request is built, Then the system prompt states they are data and not instructions, and `confirm` tools still stop for the user.

**Confirmation and ask_user**
- **AC-32** Given a `confirm` tool, When the model calls it, Then the stream emits `tool_call` with `requiresConfirmation: true` and pauses; nothing runs yet.
- **AC-33** Given a pending confirmation, When the owner approves, Then the tool runs and the stream continues; the audit decision is `Approved`.
- **AC-34** Given a pending confirmation, When the owner denies, Then the model receives "denied by the user" and the audit entry is `Denied`.
- **AC-35** Given a pending confirmation, When 5 minutes pass, the stream is closed or the page is left, Then it counts as denied (`TimedOut` or `Cancelled`) and nothing runs.
- **AC-36** Given a pending confirmation, When another user or tenant decides it, Then it returns 404; a second decision on the same call returns 409.
- **AC-37** Given the `ask_user` tool, When the model calls it, Then the stream emits `ask_user` and pauses; the owner's answer is returned to the model, and an unanswered question times out as in AC-35.
- **AC-38** Given a regeneration, When tools are needed again, Then confirmation is asked again.
- **AC-39** Given the application restarts with a confirmation pending, When it comes back, Then nothing ran and the answer is stored as not completed.
- **AC-40** Given a stopped answer during a tool call, When the stream closes, Then the call is cancelled, the audit entry is `Cancelled` and the answer is `Interrupted`.

**Audit**
- **AC-41** Given any tool call, When it is executed or refused, Then an audit entry records who, when, assistant, conversation, server and tool names, decision, outcome, duration and redacted arguments.
- **AC-42** Given a result, When audited, Then only its outcome and size are stored, never its content.
- **AC-43** Given arguments with sensitive keys (e.g. `password`, `token`, `apiKey`), When audited, Then those values are redacted.
- **AC-44** Given a failure writing the audit entry, When a call is about to run, Then it does not run and the model receives an error result.
- **AC-45** Given entries older than the retention period, When the purge job runs, Then they are deleted; recent ones are kept.
- **AC-46** Given the audit API, When filtering by period, tool, user or outcome, Then only matching entries return; no route modifies or deletes entries.
- **AC-47** Given a server deleted or renamed, When old entries are read, Then they still show the original names.

**UI**
- **AC-48** Given the admin page, When registering, refreshing and reviewing, Then servers, statuses, pending-review tools, scope and per-tool policy can be managed without touching files.
- **AC-49** Given an answer using tools, When streaming, Then the chat shows a card per call (tool, arguments, status, result) and a confirmation dialog with the full arguments when required.
- **AC-50** Given active tools, When the context gauge is shown, Then the tool definitions appear as their own segment.

## Deferred Decisions
| Decision | Fallback chosen now | Revisit trigger |
|---|---|---|
| stdio MCP servers from the UI (`mcp-runner`) | Streamable HTTP only; stdio through an operator-run gateway, documented | Customers need stdio servers without editing the compose file |
| OAuth for MCP servers | Static headers | Customers need SaaS MCP servers requiring OAuth |
| Audit log UI | API only | Dedicated spec after 004 |
| Users and roles mapped to the permission names | Only `Admin`, holding all permissions | Dedicated users/roles spec |
| `agent` scope in use | Reserved, rejected with `tool.scope_not_supported` | Spec 005 (agents) |
| Persisting tool calls in the conversation history (D-4) | Not persisted; later turns do not see earlier results | Users asking to review past tool calls, or models needing earlier results |
| Remembered confirmations (D-20) | Approve each call; the admin sets `auto` per tool | Confirmation fatigue reported |
| Parallel execution of `auto` calls (D-22) | One at a time | Slow tools making answers too long |
| Native tool: queries over CSV attachments | Not included | After 004, when attachments and tools are in use |
| MCP resources, prompts, sampling, elicitation (D-29) | Ignored | A concrete need |
| Live tool-list changes (`tools/list_changed`) | Manual refresh | Servers whose tools change often |
| Multiple application instances | Single instance; pending confirmations in memory | Any clustered deployment |
| Rate limits and quotas on tool calls per user | None | Abuse or cost reports |
| Performance targets | None; measure first | After the first measurements on reference hardware |

## Open Questions
_None._
