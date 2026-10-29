# Quorum — Modular Monolith

Self-hosted, extensible platform for AI assistants and multi-agent orchestration, licensed per user (teams/companies) and hosted by the customer.

## Language
- **Every development artifact is written in English**: code, identifiers, comments, commit messages, PR titles and descriptions, specs, plans, docs, config comments.
- Conversation with the user happens in **Italian**.

## Project Context

.NET 10 modular monolith: a single deployable (Host), modules with strict boundaries. Each module owns its features, data and domain logic.

Modules: **Tenancy, Licensing, Agents, Conversations, KnowledgeBases**.

## Tech Stack

- **.NET 10** / C# 14, ASP.NET Core Minimal APIs (`IEndpointGroup` per feature)
- **EF Core** — one DbContext and one schema per module, **PostgreSQL**
- **Microsoft.Extensions.AI** (`IChatClient`, `AIFunction`, `FunctionInvokingChatClient`) and **Microsoft Agent Framework** (`ChatClientAgent`) in the Agents module wherever they overlap with what we need; own adapters only at the edges (model endpoint, tool invoker). Their types never cross `Quorum.Modules.Agents.Contracts`.
- **Wolverine** — inter-module messaging with transactional outbox
- **FluentValidation**, **Serilog**
- **xUnit v3** + **Testcontainers**
- UI: Blazor WebAssembly served by the Host; it consumes only the public API

## Architecture

```
src/
  Quorum.Host/                       # Composition root
  Quorum.Shared/                     # Thin shared kernel: event contracts, Result, extensions
  Modules/
    Tenancy/Quorum.Modules.Tenancy/
    Licensing/Quorum.Modules.Licensing/
    Agents/Quorum.Modules.Agents/
    Conversations/Quorum.Modules.Conversations/
    KnowledgeBases/Quorum.Modules.KnowledgeBases/
tests/
  Quorum.Integration.Tests/          # Cross-module tests with Testcontainers
```

- Each module exposes a single public registration extension (`Add<Module>Module`); handlers, consumers and DbContexts stay `internal`.
- **Synchronous cross-module calls**: only through `Quorum.Modules.<X>.Contracts` projects (public interfaces and DTOs); a module never references another module's project. Integration events with Wolverine/outbox only when an asynchronous flow is needed.
- **Tenants**: every application entity has `TenantId`, with an EF Core global query filter. The one exception is `agents.web_search_configuration` — the platform has a single SearXNG instance that every tenant searches with, so that row is global by design and carries no filter (spec 005).
- **Module internal architecture**: Vertical Slice (one file per operation in `Features/`). Evolve to Clean/DDD only in the module whose domain requires it.
- Platform transactions spanning modules go through `IPlatformUnitOfWork` (shared kernel).

## Product Principles
- One-command installation (Docker / compose).
- Config-first: agents, tools, skills, prompts and topologies are defined in declarative files.
- MCP-compliant tools, open formats.
- One installation, multiple tenants (tenant ID in the data model from day one).
- Abstract, replaceable providers: LLM, embeddings, web search, vector store, conversation store.
- License: signed key (Ed25519) verified offline; online activation/revocation optional.
- Tool safety: per-agent/role permissions, human confirmation for destructive actions, audit log.
- **MVP**: chat with presets, 1 LLM provider, basic tools, topology from config, default conversation store.

## AI Stack Decisions
- Use MEAI and MAF wherever they overlap; own adapters only at the edges.
- Tool loop = `FunctionInvokingChatClient` with a Quorum `FunctionInvoker`.
- Model access = own `IChatClient` adapter (not `Microsoft.Extensions.AI.OpenAI`, which loses Ollama's `reasoning`).
- Assistants answer as `ChatClientAgent`. Attachments via `AIContextProvider`.
- `ContextWindow` stays a Quorum rule (reject, never silently drop).

## Coding Standards
- C# 14: primary constructors, collection expressions, records, pattern matching.
- File-scoped namespaces, no regions, comments only for the "why".
- Naming: PascalCase for public members, `_camelCase` for private fields, `Async` suffix.
- Module namespaces: `Quorum.Modules.<Module>`.

## Commands

```bash
dotnet tool restore                 # dotnet-ef (local tool)
docker compose -f deploy/docker-compose.dev.yml up -d postgres
dotnet build
dotnet run --project src/Quorum.Host --launch-profile http   # http://localhost:5250, Scalar at /scalar
dotnet test                         # needs Docker (Testcontainers)
dotnet format --verify-no-changes

# Add a migration to a module
dotnet ef migrations add <Name> \
  --project src/Modules/<Module>/Quorum.Modules.<Module> \
  --startup-project src/Modules/<Module>/Quorum.Modules.<Module> \
  --output-dir Persistence/Migrations
```

## Running the App
- Use only the base `docker-compose.yml`: `sh deploy/compose.sh --profile bundled-db up --build -d` (app on http://127.0.0.1:8080).
- **Never** use the dev compose or `dotnet run` for running the app.
- A local model is reached via `host.docker.internal`.
- LiteLLM proxy at `localhost:4000` (from container: `host.docker.internal:4000/v1`), never Ollama directly.

## Git and Versioning
- Trunk-based: short-lived branches from `main`, PRs with squash merge.
- Spec and plan on a `docs/spec-NNN` branch with a dedicated PR, merged right after approval; implementation starts from `main` with one branch per PR in the plan.
- Commits and PR titles follow **Conventional Commits** (`feat:`, `fix:`, `perf:`, `docs:`, `refactor:`, `test:`, `build:`, `ci:`, `chore:`; `!` for breaking changes); reference the spec when there is one, e.g. `feat: ... (spec 001)`.
- SemVer version computed by MinVer from `vX.Y.Z` tags; tags and `CHANGELOG.md` are created by release-please. **Never create tags or edit `CHANGELOG.md` by hand.**

## Workflow
- **Plan first** for non-trivial tasks (3+ steps or architectural decisions).
- **Verify before done**: `dotnet build` and `dotnet test`, then diagnostics.
- **Pre-PR**: run `dotnet build -c Release` and `dotnet format --verify-no-changes` before pushing (CI builds Release, where analyzers such as CA1875 are errors).
- **Tests**: run the full suite only at the end of a PR. Earlier, only the impacted area (`--filter-class` / `--filter-namespace`) and `dotnet build`. For a one-line fix, rerun only the failed test.
- Stop and re-plan when implementation goes sideways.

## Libraries
- Before writing a client or parser, look for an official or widely used package and state what was found. Own code only at the edge or when the library cannot do the job, with the reason stated.

## Anti-patterns
Do not generate code that:
- Defines endpoints in `Program.cs` (use `IEndpointGroup` with auto-discovery).
- Uses `DateTime.Now` (use `TimeProvider`), `new HttpClient()` (use `IHttpClientFactory`), `async void`, `.Result`/`.Wait()`.
- Returns domain entities from endpoints (map to DTOs) or uses `Results.Ok()` (use `TypedResults`).
- Creates repositories over EF Core or uses an in-memory database in tests (use Testcontainers).
- Catches bare `Exception` or uses string interpolation in log messages.
- References another module's DbContext or internal types, shares tables between modules, or calls another module's handlers.
- Puts business logic in `Quorum.Shared` or creates a single global DbContext.
- Publishes events without a transactional outbox.

## Admin and Security
- No address blocking and no parameter filtering on URLs or connection strings an admin registers.
- Secrets protection: encrypted at rest, never returned or logged, no redirects sending keys to hosts the admin did not enter.
- Private-area users are trusted and may author routers and scripts. No sandbox against hostile code. Guard against accidents: timeouts, result and call caps, secrets kept out of scripts (brokered connections), audit, `PendingReview`/`Confirm` flow.
