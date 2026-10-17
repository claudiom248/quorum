# Deferred work

Backlog of things decided *not* to do yet, so they are not forgotten. An item enters here when a spec or a conversation explicitly postpones it; it leaves when a spec picks it up. Specs keep their own "Deferred Decisions" table with the fallback in force: this file is the cross-spec index.

| Item | Origin | Why deferred | Revisit when |
|---|---|---|---|
| Keep the original uploaded file (not only the extracted text) | Spec 002 | The first iteration stores only the extracted text: less sensitive data at rest, no file storage to manage | Re-extraction, re-download of the original or knowledge base ingestion (spec 003) need it |
| XLSX attachments | Spec 002 | Out of the MVP formats; CSV covers tabular text | A user need for spreadsheets with several sheets or formulas |
| OCR for scanned PDFs | Spec 002 | Heavy dependency in the image, language-dependent; scanned PDFs are rejected with a clear error | Real requests for scanned documents |
| Image attachments | Spec 002 | Needs a vision-capable model, not guaranteed with the local models in the presets | A vision model in the presets |
| Text encodings other than UTF-8 (e.g. Windows-1252 CSV from Excel) | Spec 002 | Rejected with a clear error asking to save as UTF-8; detection can guess wrong on accented text | Real user need |
| Room for the model's answer in the context budget | Specs 001/002 | Nothing is reserved and no maximum output is set; keeps the budget rule simple | Truncated or failed answers with large attachments |
| Documents longer than the context budget | Spec 002 | Rejected with a clear error; no silent truncation or automatic summarization | Knowledge bases (spec 003) |
| Antivirus/malware scan of uploaded files (e.g. ClamAV) | Spec 002 | Not considered for now: other mitigations come first (extraction never executes or resolves file content, bounded reads, hardened XML); a scanner is an extra service to operate | Other mitigations prove insufficient, compliance requirements, or any feature that stores or re-serves the original file |
| Process isolation of the document parsers (PDF, DOCX) | Spec 002 | The parsers run inside the Host process with size, part and time limits; a pathological file could still exhaust memory or overflow the stack, which would stop the application for every tenant | Multi-user deployments, or any report of a crash while reading a document: move extraction to a worker process |
| Users and roles (User: chat only; Builder: configures assistants, agents and topologies; Admin: models, users, license, MCP servers, tool policies, audit log) mapped onto the permission names of spec 004 | Spec 004 | Needs user management, OIDC and license seats; spec 004 only defines and checks the permission names, held by `Admin` | Dedicated users/roles spec |
| stdio MCP servers registered from the UI through a Quorum-controlled `mcp-runner` sidecar (own container with Node and uv, authenticated HTTP API, no access to the key ring or the database; Quorum stays the source of truth and re-syncs the runner) | Spec 004 | Spawning processes inside Quorum would expose the master key and database credentials to third-party packages; spec 004 supports Streamable HTTP only, with stdio reachable through an operator-run gateway (supergateway) | Dedicated spec after 004, when customers need stdio servers without editing the compose file |
| Audit log viewer in the UI (table with filters and pagination over the tool audit log) | Spec 004 | Spec 004 exposes the audit log through the API only (read with `tools.manage`) | Dedicated spec after 004 |
| OAuth flow for MCP servers (callback, token refresh, per-user or per-server grants) | Spec 004 | Spec 004 authenticates to HTTP MCP servers with static headers only; OAuth needs a redirect flow, token storage and refresh | Dedicated spec, when customers need SaaS MCP servers that require OAuth |
| Native tool: queries over CSV attachments (sum, filter, group) | Spec 004 | Needs a contract between Conversations (attachments) and Agents; small local models do arithmetic on tables badly | After 004, when attachments and tools are in use |
| Persisting tool calls in the conversation history | Spec 004 | Tool calls are shown live and audited, not stored with the messages; later turns do not see earlier tool results | Users asking to review past tool calls, or models needing earlier results |
| MCP resources, prompts, sampling and elicitation | Spec 004 | The client supports tools only; sampling is excluded for safety | A concrete need |
| Standard assistant behaviour where pre-check failures (message too long, attachments or tool definitions that do not fit) become inline errors in the conversation instead of an HTTP 400 | Specs 001/002/004 | Pre-checks stay a 400 before the stream starts, so nothing is stored and the client can fix the request | A UI or product decision to match other assistants, which accept the message and show the error in the conversation |
| Tool confirmation through `ToolApprovalRequestContent` / `ApprovalRequiredAIFunction` (Microsoft.Extensions.AI, with the session binding of Microsoft Agent Framework) instead of our own pause | Spec 004 | They suspend and resume: the answer ends at the approval request and the decision arrives with a new request that carries the approval messages in the history. Spec 004 chose a stream that stays open and waits (D-9): timeout, closed stream and restart are denials, tool messages are never stored (AC-28), and `ask_user` and the audit log need the same pause, which MEAI/MAF do not cover | Streams held open for minutes prove a problem (proxies, single instance), or stored tool messages are wanted for another reason (replay, multi-agent topologies in spec 005) |
| License key verification | Spec 001 | Runs as implicit Community without a key | Dedicated Licensing spec |

## Planned specs

Decided on 2026-10-15 when splitting the MVP; each gets its own spec, plan and PRs.

| Spec | Topic |
|---|---|
| 002 | Conversation attachments: text extracted from uploaded files used as context of one conversation |
| 003 | Knowledge bases (RAG): admin-defined collections on Qdrant/pgvector, ingestion, chunking, citations |
| 004 | MCP tools: client, registry, per-agent/role permissions, human confirmation, audit log |
| 005 | Declarative config and topology: format for assistants, agents and topologies, multi-agent execution |
| 006 | Web search as a tool (after 004) |
| - | Licensing (no number yet) |
| - | Users and roles (User, Builder, Admin) mapped onto the permission names of spec 004 |
| - | `mcp-runner` sidecar: stdio MCP servers registered from the UI (after 004) |
| - | Audit log UI (after 004) |
| - | OAuth flow for MCP servers (after 004) |
