# Quorum - Product Definition

Status: evolving draft. Last updated: 2026-10-15.

## Target customer
- **First customer**: a small company with a server powerful enough to host a local LLM, without a dedicated DevOps team.
- **Second profile**: the founder as a tester (dogfooding the installation).
- **End users**: potentially every employee who works at a PC; the number is unknown and drives the per-user price.

## Promise (draft)
> Quorum is the AI assistant a small company installs on its own server in an hour, uses with its own local model and adapts to its own processes, without its data ever leaving.

## Glossary
Names that reach the public API are hard to change later, so these terms are fixed now and used consistently in docs, code and API.

- **Assistant**: what a user or an integration talks to; the published entry point. It has a name, a description, who may use it, and a topology behind it. Integrations (chat UI, customer-support tools, the future OpenAI-compatible API) always address an assistant, so its topology can change without affecting callers.
- **Agent**: a building block inside a topology, with its own system prompt, model, tools, skills and permissions (e.g. "Contracts expert", "Web researcher", "Synthesis node"). The same agent can be reused by several assistants.
- **Topology**: the graph of agents behind an assistant (fan-out to the relevant agents, a synthesis node producing the single answer). Defined in declarative config and editable in the visual graph editor, which uses the same schema.
- **Tenant**: an isolated organization within one installation; every application entity carries its `TenantId`.
- **Model endpoint**: an OpenAI-compatible endpoint: base URL including the version segment (e.g. `http://ollama:11434/v1`), model name, optional API key (encrypted at rest) and context token budget.
- **Setup mode / setup token**: the state the app stays in until the first-run wizard completes; the wizard is protected by a setup token printed to the logs and kept in memory only.

**MVP note**: in spec 001 an assistant has a trivial topology with one implicit agent, so the system prompt and model endpoint live on the `Assistant` entity. When topologies arrive they move to agents, and the assistant references a topology.

## MVP use cases
- Answer questions on documents uploaded by the company (RAG). Formats supported in the MVP: **PDF, DOCX, TXT, Markdown**.
- Summarize documents.
- Answer general questions, with web search when needed (explicit and can be disabled).

## Implications of local models
- OpenAI-compatible endpoint (Ollama, vLLM, llama.cpp, LM Studio) in the presets; cloud providers are optional.
- Limited resources: multi-agent topologies also run sequentially, with configurable concurrency limits.
- Presets tested on small models (7-14B), not only on large ones.
- No outbound telemetry; web search is the only outbound traffic and it is explicit.

## Clients and API
- The web UI is only one of the clients: customers must be able to use Quorum "behind the scenes" from their own chats, customer-support tools and integrations, always by addressing an assistant.
- **Future spec**: OpenAI-compatible endpoint (`/v1/chat/completions`) and per-application API keys.
- Web UI in **Blazor WebAssembly**, served by the Host, using only the public API. Graph editor (after the MVP) as an isolated component: evaluate Z.Blazor.Diagrams first, then possibly React Flow / Rete.js via JS interop.

## Installation
- `docker compose up`, with a wizard on first run.
- Services: `postgres` (platform, with healthcheck), `qdrant` (default vector store), `quorum` (app), optional `ollama` via a compose profile.
- The wizard asks which vector store to use (bundled Qdrant, external Qdrant, external pgvector) and which embedding model.
- The app applies migrations at startup and enters "setup mode" until it is configured.
- The wizard is protected by a **setup token** printed to the logs on first run.
- Master key used to encrypt secrets in the database: generated on first run in a dedicated volume, or from an environment variable. Never in the database.

## Data
- **Platform database**: always PostgreSQL (tenants, users, configuration, license, document metadata). No choice in the wizard. It holds no vectors.
- **Conversation store**: abstract and replaceable; default implementation on the same PostgreSQL.
- **Vector store for RAG**: abstract and configurable, separate from the platform database. MVP implementations:
  - **Qdrant** (default in the compose file);
  - **PostgreSQL + pgvector** on its own server/database, not necessarily the platform one.
  - Candidate abstraction: `Microsoft.Extensions.VectorData`, which has connectors for both.
- **Embedding model**: configurable (local by default, e.g. via Ollama). The vector dimension depends on the model: changing it requires reindexing the documents, so the model used is stored with each collection.
- **Tenant isolation in the vector store**: one collection per tenant, or a mandatory filter on `tenant_id` in the payload (to be decided).
- **External Postgres**: in `.env`, empty `COMPOSE_PROFILES` (no `bundled-db` service) and set `QUORUM_DB_CONNECTION`. The compose file passes it to the app as `ConnectionStrings__Platform`, falling back to the bundled Postgres (`${QUORUM_DB_CONNECTION:-...}`) with `depends_on` using `required: false`. `.env` is never committed; Docker secrets later.
- **Bootstrap config** (connection string, master key, `QUORUM_BASE_URL`): from env/compose. **Application config** (model endpoints, search, users, assistants, agents): in the database, set by the wizard.

## Authentication
- Local accounts (ASP.NET Core Identity): default and the administrator's emergency (break-glass) access.
- One **generic OIDC** connector configurable in the wizard: issuer URL, client id/secret, claim mapping and groups -> roles, provisioning on first login.
- SAML and LDAP/AD only through an external broker (Keycloak, Authentik, Dex), documented.
- Requires a stable `QUORUM_BASE_URL` and usually a reverse proxy with TLS.
- Development: Keycloak in `deploy/docker-compose.dev.yml`.

## License
- Signed key (Ed25519), offline verification; online activation/revocation optional.
- Seats counted on first-login provisioning.
- License types (`edition` field in the signed key, together with seats, tenants and expiry):

| Edition | Use | Notes |
|---|---|---|
| **Community** | Free, non-commercial use only | **5 users, 1 tenant**; non-commercial use enforced by the license terms |
| **Trial** | Evaluation | **30 days**, expiring key issued by us on request, verified offline; then **read-only** |
| **Commercial** | Per user, and per team/company | Seats and tenants stated in the key |

- The trial is not self-generated by the app: an internal trial would reset on reinstall. We issue the trial key (e.g. by email) and the app verifies it offline like the others.
- Community's non-commercial restriction cannot be enforced in software: the license terms enforce it; the software can only limit seats, tenants or features.
- **Source: source-available**. The code can be inspected (a trust argument for privacy); use is governed by the license. Commercial protection relies on: updates and Docker images in a private registry tied to the key, support, license terms, licensee name visible in the UI. The Ed25519 private key never enters the repository.
- Candidate source license: **PolyForm Noncommercial 1.0** (free non-commercial use) + a separate commercial license for other uses. To be validated by a lawyer. Accepting external contributions requires a CLA to keep dual licensing possible.
- **Read-only** (expired trial): conversations and documents can be viewed and exported; no new messages, model calls, uploads or configuration changes. The administrator can always install a new key.
- **No key installed**: the app runs as **implicit Community** (5 users, 1 tenant, non-commercial use only). A key is needed for Trial and Commercial. (Decided on 2026-10-15, replaces "no key = setup mode only".)

## Open questions
- **[Decision required before the source is publicly released]** Source license: PolyForm Noncommercial vs Elastic License 2.0 (or another), with legal review; CLA for external contributions.
- **[Trademark]** "Quorum" is also the name of ConsenSys' enterprise blockchain platform (GoQuorum): assess with a lawyer.
- OIDC user -> tenant mapping (issuer per tenant, a claim such as `tid`, email domain).
- Behavior when license seats are exceeded (proposal: block new users, not existing ones).
- Trial seat/tenant limits (proposal: like a small commercial license, e.g. 10 users).
- What Quorum is NOT (MVP boundaries).
- MVP success metric and indicative per-user price.
- Tenant isolation in the vector store (collection per tenant or filter on `tenant_id`).
- Document chunking strategy.
- Declarative format for assistants, agents and topologies.

Decided: tenant isolation in the platform database uses a `TenantId` column with an EF Core global filter (plan 001).
