# Quorum — Modular Monolith

Self-hosted, extensible platform for AI assistants and multi-agent orchestration, licensed per user (teams/companies) and hosted by the customer.

Status: **initial solution scaffold created, no features implemented**. Product decisions in `docs/product.md` and `docs/brainstorming.md`; specs in `docs/specs/`, plans in `docs/plans/`.

## Language
- **Every development artifact is written in English**: code, identifiers, comments, commit messages, PR titles and descriptions, specs, plans, docs, config comments.
- Conversation with the user happens in Italian.

## Project Context

.NET 10 modular monolith: a single deployable (Host), modules with strict boundaries. Each module owns its features, data and domain logic.

## Tech Stack

- **.NET 10** / C# 14, ASP.NET Core Minimal APIs (`IEndpointGroup` per feature)
- **EF Core** — one DbContext and one schema per module, **PostgreSQL**
- **Microsoft.Extensions.AI** (`IChatClient`, `AIFunction`, `FunctionInvokingChatClient`) and **Microsoft Agent Framework** (`ChatClientAgent`) in the Agents module wherever they overlap with what we need; own adapters only at the edges (model endpoint, tool invoker). Their types never cross `Quorum.Modules.Agents.Contracts`.
- **Wolverine** — inter-module messaging with transactional outbox (introduced with the first asynchronous flow)
- **FluentValidation**, **Serilog**
- **xUnit v3** + **Testcontainers**
- `dotnet-claude-kit` plugin enabled in `.claude/settings.json`; the Roslyn MCP server comes with the plugin.

## Architecture

```
src/
  Quorum.Host/                       # Composition root: registers modules, middleware, endpoint mapping
  Quorum.Shared/                     # Thin shared kernel: event contracts, Result, extensions
  Modules/
    Tenancy/Quorum.Modules.Tenancy/            # Tenants, users, roles
    Licensing/Quorum.Modules.Licensing/        # Ed25519 signed keys, offline verification
    Agents/Quorum.Modules.Agents/              # Agents, tools (MCP), skills, prompts, topologies
    Conversations/Quorum.Modules.Conversations/# Conversations and pluggable stores
tests/
  Quorum.Integration.Tests/          # Cross-module tests with Testcontainers
```

Each module exposes a single public registration extension (`Add<Module>Module`); handlers, consumers and DbContexts stay `internal`.

- **Synchronous cross-module calls**: only through the `Quorum.Modules.<X>.Contracts` projects (public interfaces and DTOs); a module never references another module's project. Integration events with Wolverine/outbox only when an asynchronous flow is needed.
- **Tenants**: every application entity has `TenantId`, with an EF Core global query filter.
- **Module internal architecture**: Vertical Slice (one file per operation in `Features/`). Evolve to Clean/DDD only in the module whose domain requires it.
- **UI**: Blazor WebAssembly served by the Host; it consumes only the public API, like any external client.

## Product principles
- One-command installation (Docker / compose).
- Config-first: agents, tools, skills, prompts and topologies are defined in declarative files; the visual graph editor reads and writes the same schema.
- MCP-compliant tools, open formats.
- One installation, multiple tenants (tenant ID in the data model from day one).
- Abstract, replaceable providers: LLM, embeddings, web search, vector store, conversation store.
- License: signed key (Ed25519) verified offline; online activation/revocation optional.
- Tool safety: per-agent/role permissions, human confirmation for destructive actions, audit log.

## MVP
Chat with presets on top of a backend/API: 1 LLM provider, a few basic tools, topology from config, default conversation store. Graph editor, plugin store and semantic cache later.

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

# Add a migration to a module (each module has its own DbContext, schema and design-time factory)
dotnet ef migrations add <Name> \
  --project src/Modules/<Module>/Quorum.Modules.<Module> \
  --startup-project src/Modules/<Module>/Quorum.Modules.<Module> \
  --output-dir Persistence/Migrations
```

Platform transactions spanning modules go through `IPlatformUnitOfWork` (shared kernel): every module DbContext in the request scope uses the same connection and joins the open transaction when it saves.

## Git and versioning
Details in `docs/release-process.md`.
- Trunk-based: short-lived branches from `main`, PRs with squash merge.
- Spec and plan on a `docs/spec-NNN` branch with a dedicated PR, merged right after approval; implementation starts from `main` with one branch per PR in the plan.
- Commits and PR titles follow **Conventional Commits** (`feat:`, `fix:`, `perf:`, `docs:`, `refactor:`, `test:`, `build:`, `ci:`, `chore:`; `!` for breaking changes); reference the spec when there is one, e.g. `feat: ... (spec 001)`.
- SemVer version computed by MinVer from `vX.Y.Z` tags; tags and `CHANGELOG.md` are created by release-please. Never create tags or edit `CHANGELOG.md` by hand.

## Workflow
- **Plan first** for non-trivial tasks (3+ steps or architectural decisions); use the kit's `/spec` and `/plan`.
- **Verify before done**: `dotnet build` and `dotnet test`, then `get_diagnostics` via Roslyn MCP.
- Stop and re-plan when implementation goes sideways.

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
