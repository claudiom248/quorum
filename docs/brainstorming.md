# Quorum - Brainstorming

Date: 2026-10-15. Chosen name: **Quorum** (rejected alternatives: Chorus, Conclave, Hearth, Weft, Tessera, Plexus, because of conflicts with existing products). Trademark and domain to be checked before launch.

## Vision
An agentic orchestration platform that is **self-hosted and extensible through configuration**. A simple user gets a ready-made chat with basic tools and their own self-hosted model; a company extends it with RAG, business tools and specialized agents.

## Differentiators
- Ease of installation.
- Multi-agent topologies defined in config **and** in a visual editor (same representation): each agent answers when relevant, a synthesis node produces the single answer.
- Configurable agents, tools, skills and prompts; replaceable web search provider with suggested presets.
- RAG tool over company/personal/team documentation.
- Plug-and-play conversation store (default, Postgres, AWS, ...).
- Classification guardrails, embeddings and semantic cache (opt-in).

## Business model
- Per-user license, and per team/company. Hosting paid by the customer (BYOK for LLM keys).
- Offline license verification with a signed key (Ed25519); online optional.

## Decisions
| Topic | Decision |
|---|---|
| MVP | Chat with presets |
| Shape | Backend/API + multiple clients |
| Tenancy | One installation, multiple tenants |
| Standards | MCP for tools, open formats |
| Licensing | Offline signed key, online optional; Community (non-commercial, 5 users, 1 tenant), Trial (30 days, then read-only), Commercial editions |
| UI | Blazor WebAssembly on top of the public API; graph editor as a future JS island |
| Module architecture | Vertical Slice inside each module (MVP) |
| Third-party API | Future spec: OpenAI-compatible endpoint + per-application API keys |
| Versioning and releases | Trunk-based, Conventional Commits, MinVer, release-please on GitHub (`docs/release-process.md`) |
| Source | Source-available (candidate: PolyForm Noncommercial + commercial license) |
| Stack | .NET + dotnet-claude-kit |
| Platform database | PostgreSQL only, bundled in the compose file, no vectors |
| RAG vector store | Abstract: Qdrant (default) or pgvector on a dedicated server/database |
| Conversation store | Abstract, Postgres by default |
| Authentication | Local accounts + generic OIDC; SAML/LDAP through a broker |
| Installation | `docker compose up` + wizard with a setup token |
| Development | Keycloak, Postgres and Qdrant in `deploy/docker-compose.dev.yml` |

Details in `docs/product.md`.

## Risks
- Very broad scope: keep the MVP vertical.
- A single graph model for config and UI, otherwise they diverge.
- .NET plugins (external assemblies): for the MVP prefer built-in providers + MCP/HTTP.
- Security of tools running on the customer's infrastructure.

## Open questions
- Exact base presets (web search assistant, document interpreter, model knowledge).
- Model provider/abstraction choice (Microsoft.Extensions.AI, optional LiteLLM).
- Positioning against Open WebUI, LibreChat, Dify.
