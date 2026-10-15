# Quorum — Modular Monolith

Piattaforma self-hosted ed estensibile per assistenti AI e orchestrazione multi-agente, distribuita con licenza per utente (team/aziende) e ospitata dal cliente.

Stato: **scaffold iniziale della solution creato, nessuna feature implementata**. Decisioni di prodotto in `docs/brainstorming.md`.

## Project Context

.NET 10 modular monolith: un solo deployable (Host), moduli con confini rigorosi. Ogni modulo possiede feature, dati e logica di dominio. I moduli comunicano tramite integration events, mai con chiamate dirette o tabelle condivise.

## Tech Stack

- **.NET 10** / C# 14, ASP.NET Core Minimal APIs (`IEndpointGroup` per feature)
- **EF Core** — un DbContext per modulo, uno schema per modulo, **PostgreSQL**
- **Wolverine** — messaging tra moduli con transactional outbox (da introdurre con la prima feature)
- **FluentValidation**, **Serilog**
- **xUnit v3** + **Testcontainers**
- Plugin `dotnet-claude-kit` abilitato in `.claude/settings.json`; il server Roslyn MCP arriva dal plugin.

## Architecture

```
src/
  Quorum.Host/                       # Composition root: registra i moduli, middleware, mapping endpoint
  Quorum.Shared/                     # Shared kernel sottile: contratti eventi, Result, estensioni
  Modules/
    Tenancy/Quorum.Modules.Tenancy/            # Tenant, utenti, ruoli
    Licensing/Quorum.Modules.Licensing/        # Chiavi firmate Ed25519, verifica offline
    Agents/Quorum.Modules.Agents/              # Agenti, tool (MCP), skill, prompt, topologie
    Conversations/Quorum.Modules.Conversations/# Conversazioni e store plug and play
tests/
  Quorum.Integration.Tests/          # Test cross-modulo con Testcontainers
```

Ogni modulo espone una sola estensione di registrazione pubblica (`Add<Module>Module`); handler, consumer e DbContext restano `internal`.

- **Architettura interna dei moduli**: Vertical Slice (un file per operazione in `Features/`). Evolvere a Clean/DDD solo nel modulo il cui dominio lo richiede.
- **UI**: Blazor WebAssembly servita dall'Host; consuma solo l'API pubblica, come qualsiasi client esterno.

## Principi di prodotto
- Installazione in un comando (Docker / compose).
- Config-first: agenti, tool, skill, prompt e topologie sono definiti in file dichiarativi; l'editor visuale a grafo legge e scrive lo stesso schema.
- Tool conformi a MCP, formati aperti.
- Una installazione, piu tenant (tenant ID nello schema dati fin dall'inizio).
- Provider astratti e sostituibili: LLM, embedding, ricerca web, vector store, conversation store.
- Licenza: chiave firmata (Ed25519) verificata offline; attivazione/revoca online solo opzionale.
- Sicurezza dei tool: permessi per agente/ruolo, conferma umana per azioni distruttive, audit log.

## MVP
Chat con preset su backend/API: 1 provider LLM, pochi tool base, topologia da config, conversation store di default. Editor a grafo, plugin store e semantic cache dopo.

## Coding Standards
- C# 14: primary constructors, collection expressions, record, pattern matching.
- File-scoped namespaces, nessuna region, commenti solo per il "perche".
- Naming: PascalCase pubblico, `_camelCase` per campi privati, suffisso `Async`.
- Namespace dei moduli: `Quorum.Modules.<Module>`.
- Lingua della documentazione: italiano. Codice e identificatori: inglese.

## Commands

```bash
dotnet build
dotnet run --project src/Quorum.Host
dotnet test
dotnet format --verify-no-changes
```

## Git e versioni
Dettagli in `docs/release-process.md`.
- Trunk-based: branch brevi da `main`, PR in squash merge.
- Commit e titoli di PR in **Conventional Commits** (`feat:`, `fix:`, `perf:`, `docs:`, `refactor:`, `test:`, `build:`, `ci:`, `chore:`; `!` per i breaking change); citare la spec quando presente, es. `feat: ... (spec 001)`.
- Versione SemVer calcolata da MinVer sui tag `vX.Y.Z`; tag e `CHANGELOG.md` li crea release-please. Non creare tag ne modificare `CHANGELOG.md` a mano.

## Workflow
- **Plan first** per task non banali (3+ passi o decisioni architetturali); usa `/spec` e `/plan` del kit.
- **Verify before done**: `dotnet build` e `dotnet test`, poi `get_diagnostics` via Roslyn MCP.
- Fermarsi e ripianificare se l'implementazione devia.

## Anti-patterns
Non generare codice che:
- Definisce endpoint in `Program.cs` (usare `IEndpointGroup` con auto-discovery).
- Usa `DateTime.Now` (usare `TimeProvider`), `new HttpClient()` (usare `IHttpClientFactory`), `async void`, `.Result`/`.Wait()`.
- Restituisce entita di dominio dagli endpoint (mappare a DTO) o usa `Results.Ok()` (usare `TypedResults`).
- Crea repository sopra EF Core o usa database in-memory nei test (usare Testcontainers).
- Cattura `Exception` generica o usa interpolazione di stringhe nei log.
- Referenzia DbContext o tipi interni di un altro modulo, condivide tabelle tra moduli o chiama handler di altri moduli.
- Mette logica di business in `Quorum.Shared` o crea un unico DbContext globale.
- Pubblica eventi senza transactional outbox.
