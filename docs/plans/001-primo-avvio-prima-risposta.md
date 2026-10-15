# Piano 001: Primo avvio e prima risposta

**Spec:** `docs/specs/001-primo-avvio-prima-risposta.md` (Approved 2026-10-15)
**Architettura:** Modular Monolith; Vertical Slice dentro ogni modulo.
**Modalita:** `/tdd`: ogni criterio di accettazione diventa prima un test che fallisce.
**Stato:** Approvato (2026-10-15)

## Decisioni
| Tema | Scelta |
|---|---|
| Comunicazione tra moduli | Progetti `Quorum.Modules.<X>.Contracts` (interfacce e DTO pubblici) per le chiamate sincrone; nessun Wolverine/outbox in questa spec |
| Isolamento dei tenant | Colonna `TenantId` con filtro globale EF Core |
| Risposta gia in corso (409) | Lock in memoria per conversazione (una sola istanza dell'app) |
| Accesso ai dati | EF Core + Npgsql, un DbContext e uno schema per modulo (`tenancy`, `agents`, `conversations`), migrazioni all'avvio |
| Utenti e login | ASP.NET Core Identity nel modulo Tenancy, cookie HttpOnly, lockout dopo 5 tentativi |
| Cifratura segreti | ASP.NET Core Data Protection con chiavi in un volume dedicato |
| Client del modello | Microsoft.Extensions.AI (`IChatClient`) con provider OpenAI su URL configurato; `GET /v1/models` via `IHttpClientFactory` |
| Streaming | `TypedResults.ServerSentEvents`; lato WASM fetch in streaming |
| UI | Blazor Web App, render mode globale InteractiveWebAssembly senza prerender (`Quorum.Web.Client` ospitato dall'Host) |
| Pacchetti | Central Package Management |
| Test | xUnit v3 + Testcontainers (Postgres); `IChatClient` finto per streaming ed errori |

## Passi

### PR 1 - Fondamenta (`feat/001-fondamenta`)
1. CPM e Shared kernel: `Directory.Packages.props`; `Result`, `IEndpointGroup` con auto-discovery, `ITenantContext`.
2. Progetti Contracts per Tenancy, Agents, Conversations.
3. Host: ProblemDetails, Serilog, OpenAPI + Scalar, Data Protection su volume, migrazioni all'avvio, `/api/version`; `AppFixture` con Testcontainers.

### PR 2 - Setup (`feat/001-setup`)
4. Tenancy: Tenant, User (Identity), SetupState; DbContext con filtro su `TenantId`; migrazione iniziale.
5. Setup mode e token (status, verify-token, chiusura API di setup). AC-1, AC-2, AC-9.
6. Agents: ModelEndpoint, Assistant, API key cifrata, test di connessione ed elenco modelli. AC-3, AC-4.
7. Completamento del setup atomico (transazione su connessione condivisa, vincolo su SetupState), login automatico. AC-4b, AC-5, AC-6, AC-7, AC-8.

### PR 3 - Autenticazione, impostazioni, chat (`feat/001-chat`)
8. Login, logout, `/auth/me`, lockout, antiforgery. AC-10, AC-11, AC-12.
9. Impostazioni del modello e system prompt. AC-24, AC-25.
10. Conversations: entita, CRUD, titolo, 404 su conversazioni altrui. AC-14, AC-20, AC-21, AC-22, AC-23.
11. Chat in streaming: `IAssistantChat` in Agents (system prompt, budget caratteri/4 con margine 20%, rifiuto messaggi troppo lunghi); SSE, stati `Interrupted`/`Failed`, 409, rigenerazione. AC-13, AC-15, AC-15b, AC-16, AC-17, AC-18, AC-19.

### PR 4 - UI e installazione (`feat/001-ui`)
12. Client Blazor WASM (wizard, login, conversazioni e chat, impostazioni); immagine container via SDK; `docker-compose.yml` con profilo `bundled-db`; `.env.example`. AC-26. Chiusura con `/verify`.

## Rischi
- Transazione tra piu DbContext: connessione Npgsql condivisa. Ripiego: assistente creato prima, SetupState chiuso per ultimo, completamento idempotente.
- Streaming in Blazor WASM: richiede `SetBrowserResponseStreamingEnabled`; prototipo minimo all'inizio del passo 11.
- Modelli locali piccoli: lentezza ed errori a meta stream; provare con Ollama reale oltre al client finto.
- Migrazioni all'avvio: sicure con una sola istanza; con piu repliche servira un lock.
