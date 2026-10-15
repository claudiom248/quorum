# Plan 001: First Run and First Response

**Spec:** `docs/specs/001-first-run-and-first-response.md` (Approved 2026-10-15)
**Architecture:** Modular Monolith; Vertical Slice inside each module.
**Mode:** `/tdd`: every acceptance criterion becomes a failing test first.
**Status:** Approved (2026-10-15)

## Decisions
| Topic | Choice |
|---|---|
| Cross-module communication | `Quorum.Modules.<X>.Contracts` projects (public interfaces and DTOs) for synchronous calls; no Wolverine/outbox in this spec |
| Tenant isolation | `TenantId` column with an EF Core global query filter |
| Response already in progress (409) | In-memory lock per conversation (single app instance) |
| Data access | EF Core + Npgsql, one DbContext and one schema per module (`tenancy`, `agents`, `conversations`), migrations at startup |
| Users and login | ASP.NET Core Identity in the Tenancy module, HttpOnly cookie, lockout after 5 attempts |
| Secret encryption | ASP.NET Core Data Protection with keys in a dedicated volume |
| Model client | Microsoft.Extensions.AI (`IChatClient`) with the OpenAI provider on the configured URL; `GET /v1/models` via `IHttpClientFactory` |
| Streaming | `TypedResults.ServerSentEvents`; streaming fetch on the WASM side |
| UI | Blazor Web App, global InteractiveWebAssembly render mode without prerendering (`Quorum.Web.Client` hosted by the Host) |
| Packages | Central Package Management |
| Tests | xUnit v3 + Testcontainers (Postgres); fake `IChatClient` for streaming and errors |

## Steps

### PR 1 - Foundations (`feat/001-foundations`)
1. CPM and shared kernel: `Directory.Packages.props`; `Result`, `IEndpointGroup` with auto-discovery, `ITenantContext`.
2. Contracts projects for Tenancy, Agents, Conversations.
3. Host: ProblemDetails, Serilog, OpenAPI + Scalar, Data Protection on a volume, migrations at startup, `/api/version`; `AppFixture` with Testcontainers.

### PR 2 - Setup (`feat/001-setup`)
4. Tenancy: Tenant, User (Identity), SetupState; DbContext with the `TenantId` filter; initial migration.
5. Setup mode and token (status, verify-token, closing the setup API). AC-1, AC-2, AC-9.
6. Agents: ModelEndpoint, Assistant, encrypted API key, connection test and model list. AC-3, AC-4.
7. Atomic setup completion (transaction on a shared connection, constraint on SetupState), automatic login. AC-4b, AC-5, AC-6, AC-7, AC-8.

### PR 3 - Authentication, settings, chat (`feat/001-chat`)
8. Login, logout, `/auth/me`, lockout, antiforgery. AC-10, AC-11, AC-12.
9. Model and system prompt settings. AC-24, AC-25.
10. Conversations: entities, CRUD, title, 404 on other users' conversations. AC-14, AC-20, AC-21, AC-22, AC-23.
11. Streaming chat: `IAssistantChat` in Agents (system prompt, budget as characters/4 with a 20% margin, rejection of over-long messages); SSE, `Interrupted`/`Failed` statuses, 409, regeneration. AC-13, AC-15, AC-15b, AC-16, AC-17, AC-18, AC-19.

### PR 4 - UI and installation (`feat/001-ui`)
12. Blazor WASM client (wizard, login, conversations and chat, settings); container image via the SDK; `docker-compose.yml` with the `bundled-db` profile; `.env.example`. AC-26. Close with `/verify`.

## Risks
- Transaction across several DbContexts: requires a shared Npgsql connection. Fallback: create the assistant first, close SetupState last, idempotent completion.
- Streaming in Blazor WASM: requires `SetBrowserResponseStreamingEnabled`; minimal prototype at the start of step 11.
- Small local models: slowness and errors mid-stream; test against a real Ollama in addition to the fake client.
- Migrations at startup: safe with a single instance; multiple replicas will need a lock.
