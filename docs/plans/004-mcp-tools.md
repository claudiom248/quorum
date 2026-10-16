# Plan 004: MCP Tools

**Spec:** `docs/specs/004-mcp-tools.md` (Approved 2026-10-17)
**Architecture:** Modular Monolith; Vertical Slice inside each module.
**Mode:** `/tdd`: every acceptance criterion becomes a failing test first.
**Status:** Approved (2026-10-17)

## Decisions
| Topic | Choice |
|---|---|
| Ownership | Everything about tools lives in **Agents** (entities, registry, MCP client, native tools, loop, audit). **Conversations** keeps the stream, the lock and the user: it relays the new SSE events, checks that a conversation belongs to the caller and forwards decisions. Conversations never reads a tool entity |
| Contract changes (`Agents.Contracts`) | `IAssistantChat.StartAnswerAsync` gains the conversation id (needed by the audit log). `AnswerChunk` gains an optional `Tool` payload and its kinds gain `ToolCall`, `ToolResult` and `UserQuestion`. `ContextUsage` gains `ToolsCharacters` (default 0). New `IToolInteractions` accepts a user's decision or answer for a pending call. Existing callers keep working with the defaults |
| Permission names | `QuorumPolicies` (Shared) gains `ToolsManage`, `ToolsAttach`, `ToolsUse`. Tenancy registers each as a policy that today requires the `Admin` role, in one place, so the users/roles spec only changes that mapping. Endpoints name policies, never roles |
| Model messages | `ChatTurn` (public, User/Assistant) is unchanged. Inside Agents an internal `ModelMessage` hierarchy (system, user, assistant with `tool_calls`, tool with `tool_call_id`) is what the loop builds and `OpenAiChatStream` serializes |
| One model round | `OpenAiChatStream` is split in two: `StreamRoundAsync` yields `Text`, `Reasoning` and, at the end, the assembled tool calls of that round. `ToolLoop` calls it up to `MaxRounds` times, executes the calls and yields `AnswerChunk`s. Argument fragments are assembled by `index`; both `arguments` as a JSON string (OpenAI, vLLM) and as an object (some Ollama builds) are accepted |
| Loop budget | Tool definitions are counted after the system prompt and attachments, then history is selected (`ContextWindow.Select` reserves them first). While the loop runs, the running total (system prompt, attachments, definitions, selected history, assistant tool-call messages, tool results) must stay within the usable budget; otherwise the answer ends `Failed` ("the tool results filled the context") |
| Execution and policy | `ToolExecutor` runs one call: resolve by exposed name → check state, policy and pool at call time → audit `Started` → (confirmation, PR 3) → run with the call timeout → cap the result → audit completion. Native tools implement `INativeTool`; MCP tools go through `McpToolInvoker`. Same pipeline for both |
| Exposed tool names | `native__<tool>` and `<server>__<tool>`. Characters outside `[a-zA-Z0-9_-]` in an MCP tool name are replaced by `_`; a name longer than 64 characters or colliding after sanitizing is shortened and given a short hash suffix. The model-facing name is mapped back to the tool id per request, never parsed |
| Pending interactions | `PendingInteractions` (singleton, in memory) holds one entry per confirmation or question: call id, conversation id, user id, kind, expiry and a `TaskCompletionSource`. The loop waits on it with the confirmation timeout and the lease token; the decision endpoints complete it. A restart loses all entries, which is a denial (D-9) |
| Keep-alive | While the stream is paused the relay emits a `ping` SSE event every 15 seconds (ignored by the client) so reverse proxies with a 60 s read timeout do not cut the connection and turn a slow user into a denial |
| MCP client | The official **`ModelContextProtocol.Core` 2.2.0** (Apache-2.0; client only, no ASP.NET server pieces) with `HttpClientTransport` in Streamable HTTP mode, over a named `HttpClient`. PR 6 starts with a prototype that proves it accepts our `HttpClient`, static headers, redirect control and cancellation; if it does not, a minimal own client (JSON-RPC `initialize`, `tools/list`, `tools/call`, cancellation notification) replaces it. A connection is opened lazily per server **per answer** and disposed when the answer ends (no long-lived sessions to repair), and `tools/list` runs on registration and refresh only |
| URL guard (D-18) | Named `HttpClient` `mcp-server` with `SocketsHttpHandler { AllowAutoRedirect = false, ConnectCallback }`: the callback resolves the host, rejects blocked addresses and connects to the validated IP, so DNS rebinding cannot swap it afterwards. Blocked: `169.254.0.0/16`, `fe80::/10`, `100.100.100.200` (Alibaba metadata), `fd00:ec2::254` (AWS IPv6 metadata), the unspecified addresses, and IPv4-mapped IPv6 forms of all of them. Registration and test also check the URL up front to return `tool.url_blocked` instead of a connection error. Redirects are never followed (a 3xx is a failed call) |
| Server secrets | Headers are stored as one JSON blob encrypted with Data Protection through the existing `ApiKeyProtector` (generalized by purpose string); the API returns `hasHeaders` only |
| Definition hash | SHA-256 of canonical JSON (name, description, input schema with sorted keys). Annotations are excluded |
| Audit | `Started` row written before execution (fail closed: any exception means the call does not run); completion written with `CancellationToken.None` like the answer in `AnswerStream`. Arguments are redacted recursively (keys containing `password`, `passwd`, `secret`, `token`, `apikey`, `api_key`, `authorization`, `credential`, `private_key`, case-insensitive) and stored up to 32,000 characters with an `ArgumentsTruncated` flag. Retention runs in a `BackgroundService` once a day with `ExecuteDeleteAsync` per tenant |
| Native tools | `native__current_datetime` (optional IANA/Windows timezone id), `native__calculator` (own recursive-descent parser over `decimal`/`double`: `+ - * / % ^`, parentheses, `sqrt pow abs round min max`, input limited in length and depth, no reflection or scripting), `native__ask_user` (PR 3). Rows are created idempotently: at startup for every tenant (an `IDatabaseInitializer`) and by the setup provisioning, which also activates them on the new default assistant (D-24) |
| Pool and warnings | One query computes an assistant's pool (enabled tools whose server is available and whose scope covers the tenant or the assistant; native tools are always in the pool). `warnings` (D-26) come from the number of active tools and the characters of their rendered definitions against the usable budget |
| Settings | `ToolSettings` row per tenant, created with the defaults of the spec; the loop reads it once per answer |
| UI | Existing MudBlazor components. Chat: a `ToolCallCard` per call, a confirmation dialog, an inline `ask_user` prompt, a tools segment in the context gauge. Admin: a Tools page (servers, tools, scope, policy, review) and a tools section for the assistant in Settings |
| Tests | xUnit v3 + Testcontainers as before. `FakeModelServer` learns multi-round scripts (a queue of scripts per request, tool-call chunks in the OpenAI and Ollama shapes, rejection of `tools`). MCP tests use a real MCP server hosted in-process on loopback (test-only package `ModelContextProtocol.AspNetCore`) plus a hand-written `HttpMessageHandler` for failures (redirect, slow, 500, bad JSON). Time-dependent paths (timeouts, retention) use `FakeTimeProvider` where `TimeProvider` is injected |

## Steps

### PR 1 - Foundations (`feat/004-foundations`)
1. Shared and Tenancy: `QuorumPolicies.ToolsManage/ToolsAttach/ToolsUse`, registered as `Admin`-only policies. AC-10.
2. Agents domain and migration `AddTools`: `Tool` (native origin only for now), `AssistantTool`, `ToolSettings`, `ToolAuditEntry`, `ModelEndpoint.SupportsTools`; tenant filters and indexes. Native tool provisioning (startup initializer and setup), activation on the new default assistant, none on upgrade. AC-16.
3. `supportsTools`: settings GET/PUT, model connection tests return `true`/`false`/`null` (the probe reads the model's capabilities where the server reports them; Ollama `POST /api/show` is the first candidate, verified against a real Ollama). AC-30.
4. Tools API: `GET /tools`, `PUT /tools/{id}` (state, policy), `GET/PUT /settings/tools`, `GET /assistants`, `GET/PUT /assistants/{id}/tools` with `tool.not_in_pool` and `warnings`. AC-8 (native), AC-14, AC-17, AC-18 (tenant isolation of native rows).
5. Definition renderer: the exact JSON the model receives per tool (also used to measure the budget in PR 2) and its length.

### PR 2 - Tool-calling loop with native tools (`feat/004-loop`)
6. `OpenAiChatStream` split into `StreamRoundAsync`; request carries `tools` and tool messages; assembly of `tool_calls` fragments; HTTP 400 on `tools` mapped to the "supports tools" message. AC-29.
7. `ToolLoop` and `ToolExecutor` for `auto` tools: rounds, total time, per-call timeout, sequential execution, malformed or unknown calls and unavailable tools as error results, result cap. AC-19, AC-22 to AC-27.
8. Native tools `current_datetime` and `calculator`. AC-19.
9. Audit: `Started` before execution, completion after, redaction, truncation flag, fail closed. AC-41, AC-42, AC-43, AC-44.
10. Budget: `ContextWindow` reserves definitions, `ContextUsage.ToolsCharacters`, `chat.tools_exceed_budget`, running total in the loop. AC-20, AC-21.
11. Contract and relay: `StartAnswerAsync` with conversation id, new chunk kinds, `AnswerStream` emits `tool_call` and `tool_result`; fixed instruction in the system prompt that tool results and documents are data. AC-15, AC-28, AC-31.

### PR 3 - Confirmation and `ask_user` (`feat/004-confirmation`)
12. `PendingInteractions` and `IToolInteractions`; `POST .../tool-calls/{callId}/decision` and `/answer` with ownership check (404) and double decision (409). AC-36.
13. `confirm` policy in the executor: `tool_call` with `requiresConfirmation` and `expiresAt`, pause, approve, deny, timeout, closed stream and cancellation; keep-alive `ping`. AC-32 to AC-35, AC-40.
14. `native__ask_user` on the same mechanism. AC-37.
15. Regeneration asks again; restart with a pending call; conversation deleted while pending. AC-38, AC-39.

### PR 4 - Chat and settings UI (`feat/004-ui-chat`)
16. API client and models for the new events; `ToolCallCard`, confirmation dialog with the full arguments, `ask_user` prompt, tools segment in `ContextBreakdown`. AC-49, AC-50.
17. Settings: "supports tools" with the pre-filled value from the test, the assistant's tool activation with the warnings; the wizard shows the flag too.

### PR 5 - Audit API and retention (`feat/004-audit`)
18. `GET /tools/audit` with filters and paging, read-only. AC-46.
19. Retention job and its setting. AC-45.

### PR 6 - MCP servers registry (`feat/004-mcp-registry`)
20. Prototype of `ModelContextProtocol.Core` against our requirements (see Decisions); decide SDK or own client before anything else.
21. `McpServer` entity, `Tool` gains server id, hash, annotations, `PendingReview`; migration `AddMcpServers`. Encrypted headers. AC-6.
22. URL guard and the `mcp-server` HttpClient. AC-3, AC-4.
23. Endpoints: `POST /tools/servers/test`, create, list, detail, update, delete, refresh; name rules and uniqueness; unreachable server saved as `Unavailable`. AC-1, AC-2, AC-5, AC-9, AC-10.
24. Review flow: hash comparison, new and changed tools pending review and back to `Confirm`, vanished tools removed with their activations. AC-7, AC-8.
25. Scopes: pool computation for `Global`, `Tenant`, `Assistant`; `agent` rejected. AC-11, AC-12, AC-13, AC-18.

### PR 7 - MCP tool execution (`feat/004-mcp-execution`)
26. `McpToolInvoker` and connection per answer; prefixed names with sanitizing; call-time checks (server unavailable, tool changed or disabled). The existing executor pipeline, policy and audit apply unchanged. AC-19 and AC-23 to AC-26 with an MCP tool; AC-20.
27. End-to-end tests with a real in-process MCP server: confirmation, denial, large result, server error, redirect, timeout. AC-47 (a deleted or renamed server keeps its audit names).

### PR 8 - Admin UI (`feat/004-ui-admin`)
28. Tools page: register, test, refresh, scope, tool review, enable/disable, policy; the unavailable warning; annotations shown as information. AC-48.
29. Close with `/verify`, a manual run against a real Ollama with a tool-capable model, and a real MCP server through a supergateway sidecar; document the gateway in `docs/`.

## Risks
- **Model quality**: small local models call tools badly (wrong arguments, repeated calls, no call at all). Mitigation: errors go back to the model as results, round limit (D-8), the "supports tools" flag and the warnings (D-26). Quality is measured in the manual run of step 29, not promised.
- **Tool-call formats differ by server**: Ollama, vLLM and llama.cpp stream `tool_calls` differently (whole call in one chunk, fragments by `index`, `arguments` as string or object). Mitigation: fixtures for each shape in the fake server, verified against a real Ollama; unknown shapes fail with a clear message.
- **SDK fit**: `ModelContextProtocol.Core` may not let us control the handler, redirects or cancellation the way D-18 needs. Mitigation: the prototype is step 20 and a minimal own client is the fallback. Confirm the license of every transitive dependency when adding packages, as for PdfPig.
- **Idle connections during a pause**: proxies and browsers may cut a silent SSE stream. Mitigation: the 15 s `ping`; a cut still ends in a safe denial.
- **Lock held while waiting**: the conversation lock stays up to 5 minutes (or per tool call up to 30 s) so another message returns 409. Accepted by the spec.
- **Single instance**: pending interactions and locks are in memory; a second replica would break them. Documented limit, already true for the conversation lock.
- **Time zones in the container**: `native__current_datetime` needs tzdata, which the runtime image may not include. Verify in step 8 with the image; add the package in the Dockerfile if missing.
- **Prompt injection**: a document or tool result can push the model to call tools. The policy, the confirmation with full arguments and the audit are the controls; the instruction in the prompt (D-31) only helps. No tool may be `auto` by default except harmless native ones.
- **Audit volume and sensitivity**: arguments can be large or contain secrets under keys we do not know. Mitigation: redaction by key name, a size cap with a flag, 90-day retention, `tools.manage` only. The redaction list is a heuristic, stated as such in the docs.
- **Exposed names**: sanitizing and hash suffixes make names less predictable than D-16 states for unusual tool names. The mapping is per request, and the audit keeps the real names.
- **EF concurrency**: the audit writes and the tool queries use the Agents `DbContext` while the stream is being consumed in the request scope; the loop must never run two operations on it at once (calls are sequential by D-22).
- **Migrations on the same module**: three migrations touch `agents` across PRs; review them in order and keep each one reversible.

## Details beyond the spec to confirm
- Exposed-name sanitizing rule (see Decisions).
- Audit arguments cap of 32,000 characters with a flag.
- `ping` every 15 seconds during pauses.
- The extra blocked addresses in the URL guard.
- Test-only dependency on `ModelContextProtocol.AspNetCore`.
