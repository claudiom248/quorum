# Deferred work

Backlog of things decided *not* to do yet, so they are not forgotten. An item enters here when a spec or a conversation explicitly postpones it; it leaves when a spec picks it up. Specs keep their own "Deferred Decisions" table with the fallback in force: this file is the cross-spec index.

## High priority

Items that hurt what users see today and should be picked up before new features.

| Item | Origin | Why deferred | Revisit when |
|---|---|---|---|
| The lookup tool description does not steer the local model enough: it passed a country name ("Italy") instead of the stored code (`IT`) to a filter, twice in a row, and corrected itself only after two empty answers; and it uses `document_ids` only when the question asks for it explicitly, otherwise it searches with `rag` and a filter. Reword the tool description (and the filter hints) so values come from the lookup list as stored and `document_ids` is the way to narrow to known documents | Found while testing spec 007 from chat | It is a change to the tool description, not to the lookup mechanism, so it was left out of the spec 007 PRs | Next fix on the lookup tools; before any new work on the model-facing tool definitions |
| The web fetch tool (spec 006) reads any absolute http(s) address the model names, including loopback, private and link-local ones (`http://169.254.169.254/...`, `http://localhost:5432`): a read-SSRF whose response text comes back into the model's context. Fix: validate the *resolved* endpoint (`SocketsHttpHandler.ConnectCallback`) and refuse loopback, `127.0.0.0/8`, `10/8`, `172.16/12`, `192.168/16`, `169.254/16`, `fc00::/7`, `::1` — a check on the `Uri` before the request does not close DNS rebinding | Found reviewing spec 006 (2026-10-29) | Postponed by the developer while closing the PR; the fetch already refuses redirects, bounds the bytes it reads and clamps the wait, so the exposure is a read of an internal service rather than a crash | Before the web fetch tool is offered to a deployment that can reach internal services; a dedicated hardening spec with the outbound policy of the platform |

## Other deferred work

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
| Built-in tool: queries over CSV attachments (sum, filter, group) | Spec 004 | Needs a contract between Conversations (attachments) and Agents; small local models do arithmetic on tables badly | After 004, when attachments and tools are in use |
| Persisting tool calls in the conversation history | Spec 004 | Tool calls are shown live and audited, not stored with the messages; later turns do not see earlier tool results | Users asking to review past tool calls, or models needing earlier results |
| MCP resources, prompts, sampling and elicitation | Spec 004 | The client supports tools only; sampling is excluded for safety | A concrete need |
| Standard assistant behaviour where pre-check failures (message too long, attachments or tool definitions that do not fit) become inline errors in the conversation instead of an HTTP 400 | Specs 001/002/004 | Pre-checks stay a 400 before the stream starts, so nothing is stored and the client can fix the request | A UI or product decision to match other assistants, which accept the message and show the error in the conversation |
| Tool confirmation through `ToolApprovalRequestContent` / `ApprovalRequiredAIFunction` (Microsoft.Extensions.AI, with the session binding of Microsoft Agent Framework) instead of our own pause | Spec 004 | They suspend and resume: the answer ends at the approval request and the decision arrives with a new request that carries the approval messages in the history. Spec 004 chose a stream that stays open and waits (D-9): timeout, closed stream and restart are denials, tool messages are never stored (AC-28), and `ask_user` and the audit log need the same pause, which MEAI/MAF do not cover | Streams held open for minutes prove a problem (proxies, single instance), or stored tool messages are wanted for another reason (replay, multi-agent topologies in spec 005) |
| Ingestion in Quorum (extraction, chunking, embedding, writing into the store) | Spec 003 | Data owners populate knowledge bases, often with their own ETL; Quorum only registers and queries them | Customers without ETL capability |
| Other knowledge base kinds: raw documents from folders, S3-compatible or remote storage with a blob pattern, external SQL/MongoDB sources (pgvector is spec 008 and Pinecone spec 009) | Spec 003 | Spec 003 implements Qdrant only, behind a per-kind abstraction; raw documents need query-time parsing, a search strategy and permissions on the sources | A customer needs them; each kind is a dedicated spec |
| pgvector authentication other than user and password (cloud IAM tokens, Kerberos), certificates managed from the UI; `sparsevec`, `bit`, L1 distance; a free SQL query as the source; per-KB index tuning | Spec 008 | Spec 008 takes a connection string for anything the driver supports, dense vectors and three distances, and a relation (views adapt the schema) | A customer on a managed service that requires it, poor retrieval on exact terms, or data owners that cannot create a view |
| Pinecone document indexes (Documents API: typed schema, full-text search, `score_by`), sparse and hybrid search, integrated search by text, Pinecone reranking, record id as the source of a role | Spec 009 | Spec 009 supports dense vector indexes through the Vectors API, search by vector, metadata keys only | A customer with document indexes, poor retrieval on exact terms, or ids that carry the document name |
| Moving the Pinecone client to a newer API version, or to an official SDK | Spec 009 | `2026-07` is pinned in a thin REST client because the official .NET SDK was archived on 2026-07-03 | Pinecone announces the retirement of `2026-07` (each version is supported at least 12 months), or ships a maintained SDK |
| Embedding endpoint per knowledge base (different servers and models) | Spec 003 | The installation's model endpoint is used, with the embedding model name stored per KB | A KB indexed with a model the installation endpoint does not serve |
| Hybrid search, sparse vectors and reranking for knowledge bases | Spec 003 | Dense vector search only | Poor retrieval quality on exact terms or codes |
| Per-user or per-document permissions on knowledge bases (e.g. from OIDC claims) | Spec 003 | Access is at KB level; the admin carves slices with fixed filters | Users and roles spec, KBs shared among mixed audiences |
| Background verification and payload drift detection for knowledge bases | Spec 003 | Verification is manual | Many KBs or frequent payload changes |
| An endpoint for the data owner's ETL to refresh lookups, with a service token per knowledge base | Spec 007 | Lookups refresh every 24 hours and from a button in the admin | Data that changes faster than the interval, or ingestion that ends at known times |
| Lookups for `Pinecone` knowledge bases | Spec 007 | Qdrant and PgVector (spec 008 Amendment 2); Pinecone has no facet and reports no support for lookups | A customer on Pinecone |
| Normalising values or a synonym table for computed lookups; a cache shared between several instances; sums, averages and joins over metadata | Spec 007 | Values as stored (synonyms only in manual lookups), in-memory cache with the tables as source of truth, and no aggregates beyond counts of documents | Data with many spellings of one value and no ingestion fix, a multi-instance deployment, or external SQL sources with named parametrised queries |
| Renaming a lookup or moving it between knowledge bases | Spec 007 | A lookup follows its field and its knowledge base | A concrete need |
| `PUT /api/settings/web` writes the platform's one web row and is guarded by `tools.manage`, which every tenant's `Admin` holds: an administrator of any tenant sets the SearXNG instance, the User-Agent and the robots/cache policy for everybody. The docs say the default tenant's administrator edits it, but nothing in the code knows which tenant that is | Specs 005 and 006 (found reviewing 006, 2026-10-29) | There is no default-tenant concept to check against: `CompleteSetup` creates one and nothing records its id. Needs the tenancy decision, not a one-line policy change | The users and roles spec, or any spec that gives the platform a "default tenant" identity |
| License key verification | Spec 001 | Runs as implicit Community without a key | Dedicated Licensing spec |

## Planned specs

Decided on 2026-10-15 when splitting the MVP; each gets its own spec, plan and PRs.

| Spec | Topic |
|---|---|
| 002 | Conversation attachments: text extracted from uploaded files used as context of one conversation |
| 003 | Knowledge bases (RAG): consume-only (no ingestion), Qdrant, metadata dictionary and mapping, `rag` tool with filters, citations |
| 004 | MCP tools: client, registry, per-agent/role permissions, human confirmation, audit log |
| 005 | Declarative config and topology: format for assistants, agents and topologies, multi-agent execution |
| 006 | Web search as a tool (after 004) |
| 007 | Metadata lookups: lists of values and counts from the metadata of knowledge bases, a lookup tool with live operations and `document_ids` on rag tools (added 2026-10-22) |
| 008 | pgvector knowledge bases: a relation of a PostgreSQL database as a KB kind, completing the per-kind abstraction (added 2026-10-22; numbered after 005 and 006, already cited by approved specs, and after 007 (lookups)) |
| 009 | Pinecone knowledge bases: a Pinecone index as a KB kind (after 008) |
| - | Licensing (no number yet) |
| - | Users and roles (User, Builder, Admin) mapped onto the permission names of spec 004 |
| - | `mcp-runner` sidecar: stdio MCP servers registered from the UI (after 004) |
| - | Audit log UI (after 004) |
| - | OAuth flow for MCP servers (after 004) |
