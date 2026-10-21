# Quorum

The AI assistant a small company installs on its own server, runs with its own local model and adapts to its own processes, without its data ever leaving.

Quorum is a self-hosted platform for AI assistants and, later, multi-agent orchestration. One installation serves several tenants; it is licensed per user and hosted by the customer.

> **Status: early.** Quorum is under active development (current release: see [CHANGELOG.md](CHANGELOG.md)). The first-run wizard, sign-in, streaming chat and conversation attachments work today; multi-agent topologies, RAG on company documents and the graph editor are on the roadmap.

## What it does today

- **One-command install** with Docker Compose and a guided first-run setup, protected by a one-time setup token printed in the logs.
- **Any OpenAI-compatible model endpoint**: Ollama, vLLM, llama.cpp, LM Studio or a cloud provider. The API key is optional and encrypted at rest.
- **Streaming chat** with stop and regenerate, the model's reasoning shown live (never stored), and conversations you can rename, continue and delete.
- **Attachments** in a conversation: TXT, Markdown, CSV, PDF (with a text layer) and DOCX, up to 5 MB each. Their text goes into the model's context, and the composer shows how much of the context budget is used.
- **No outbound telemetry.** The only traffic leaving the server is the traffic to the model endpoint you configure.

## Quick start

You need Docker with Compose v2.

```bash
git clone https://github.com/claudiom248/quorum.git
cd quorum
cp .env.example .env            # then set POSTGRES_PASSWORD
docker compose --profile bundled-db up --build -d
docker compose logs quorum      # copy the setup token
```

Open <http://localhost:8080>, paste the setup token and follow the three steps: model endpoint, administrator account, system prompt.

### Connecting a local model

Quorum runs in a container, so `localhost` there is the container itself. To reach a model server running on the host:

| Server | Base URL |
|---|---|
| Ollama | `http://host.docker.internal:11434/v1` |
| LM Studio | `http://host.docker.internal:1234/v1` |
| LiteLLM proxy | `http://host.docker.internal:4000/v1` |

- The server must listen on all interfaces, not only on `127.0.0.1` (for Ollama, `OLLAMA_HOST=0.0.0.0`).
- On Linux, add `extra_hosts: ["host.docker.internal:host-gateway"]` to the `quorum` service.
- Pick a **chat** model. Safety classifiers such as Llama Guard only ever answer `safe` or `unsafe`.
- The context budget you set must not exceed what the model server actually offers (Ollama: `OLLAMA_CONTEXT_LENGTH` or `num_ctx`), or long documents are cut off without an error.

### Configuration

Set these in `.env`:

| Variable | Default | Purpose |
|---|---|---|
| `POSTGRES_PASSWORD` | (required) | Password of the bundled PostgreSQL. |
| `QUORUM_BIND` | `127.0.0.1` | Address the web UI is published on. Use `0.0.0.0` only behind a TLS reverse proxy. |
| `QUORUM_PORT` | `8080` | Port the web UI is published on. |
| `QUORUM_DATABASE` | bundled database | Connection string of your own PostgreSQL, version 15 or later (the dictionary index uses NULLS NOT DISTINCT). Start without the `bundled-db` profile when you set it. |

## Development

Requirements: .NET 10 SDK and Docker (the tests use Testcontainers).

```bash
dotnet tool restore
docker compose -f deploy/docker-compose.dev.yml up -d postgres
dotnet run --project src/Quorum.Host --launch-profile http   # http://localhost:5250, API reference at /scalar
dotnet test
dotnet format --verify-no-changes
```

### Architecture

Quorum is a .NET 10 **modular monolith**: one deployable, with modules that own their features, data and database schema.

```
src/
  Quorum.Host/            # Composition root: modules, middleware, endpoints, serves the web UI
  Quorum.Web.Client/      # Blazor WebAssembly UI, uses only the public API
  Quorum.Shared/          # Thin shared kernel
  Modules/
    Tenancy/              # Tenants, users, sign-in, first-run setup
    Licensing/            # Ed25519-signed license keys, verified offline (planned)
    Agents/               # Assistants, model endpoints, chat with the model
    Conversations/        # Conversations, messages, attachments
tests/
  Quorum.Integration.Tests/
```

- Modules talk to each other only through their `*.Contracts` projects; each has its own EF Core `DbContext` and schema on PostgreSQL.
- Every application entity carries a `TenantId` with a global query filter.
- Inside a module, features are vertical slices: one file per operation.
- Stack: ASP.NET Core Minimal APIs, EF Core, FluentValidation, Serilog, MudBlazor, xUnit v3.

More detail in [CLAUDE.md](CLAUDE.md), product decisions in [docs/product.md](docs/product.md) and feature specs in [docs/specs](docs/specs).

### Contributing

- Trunk-based: short-lived branches from `main`, pull requests merged with squash.
- Commit messages and PR titles follow [Conventional Commits](https://www.conventionalcommits.org/) (`feat:`, `fix:`, `docs:` ...), referencing the spec when there is one, e.g. `feat: ... (spec 002)`.
- Versions and the changelog are produced by release-please and MinVer: never create tags or edit `CHANGELOG.md` by hand.
- Code, comments and docs are written in English.

See [docs/release-process.md](docs/release-process.md) for the full flow.

## Roadmap

- Multi-agent topologies defined in declarative config, then a visual graph editor on the same schema.
- Answers grounded in company documents (RAG) with a pluggable vector store (Qdrant, pgvector).
- MCP-compliant tools with per-agent permissions, human confirmation for destructive actions and an audit log.
- An OpenAI-compatible API and per-application keys, so other tools can talk to Quorum's assistants.

## License

No license has been granted yet: all rights reserved.
