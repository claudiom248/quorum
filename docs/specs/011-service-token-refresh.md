# Spec 011: Service Token for Knowledge Base Refresh

**Status:** Approved (2026-10-28)
**Date:** 2026-10-28

## Problem

Chi alimenta una Knowledge Base con un proprio processo ETL deve poter triggerare immediatamente il refresh dei lookup appena l'ingestion finisce, senza aspettare l'intervallo di refresh schedulato (spec 007 D-10). Oggi l'unico modo è un admin autenticato via cookie dalla UI — l'ETL non ha credenziali utente e non può (né deve) accedere ad altro.

## Scope

### In

- Un nuovo meccanismo di autenticazione via **service token** (Bearer) per l'endpoint di refresh dei lookup e un nuovo endpoint di stato del refresh.
- Il service token è generato dall'admin, legato al tenant, e autorizza solo il refresh e la consultazione dello stato — non altre operazioni.
- Gestione dei token (generazione, lista, revoca) tramite API admin.
- Middleware nel Host che valida il Bearer token tramite il modulo Tenancy e inietta le claim nel contesto.

### Out

- Modifiche all'endpoint di refresh esistente (comportamento invariato: marca le slice come due, sveglia il servizio, torna `202 Accepted`).
- UI per la gestione dei token (solo API in questa spec).
- Rate limiting, audit log, scadenza dei token.
- Webhook o notifiche al completamento del refresh.

## Domain Model

### ServiceToken (modulo Tenancy)

| Campo | Tipo | Descrizione |
|---|---|---|
| Id | Guid | Identificativo del token |
| TenantId | Guid | Il tenant a cui appartiene |
| TokenHash | string | SHA-256 del valore del token |
| CreatedAt | DateTimeOffset | Quando è stato generato |

- Il valore del token è `svc_` + 32 byte random codificati URL-safe base64 (~47 caratteri).
- Il plaintext è mostrato **una sola volta** alla generazione e mai memorizzato.
- La validazione calcola lo SHA-256 del token ricevuto e lo confronta con `TokenHash`.
- **Revoca**: hard delete della riga. Il token diventa immediatamente invalido.
- Multipli token attivi per tenant.
- Nessuna scadenza.

### RefreshStatus (response DTO, modulo KnowledgeBases)

| Campo | Tipo | Descrizione |
|---|---|---|
| Status | string | `"idle"`, `"running"`, `"failed"` |
| LastCompletedAt | DateTimeOffset? | Ultimo `ComputedAt` tra tutte le slice della KB |
| RunningSlices | int | Slice con `ComputingSince` impostato |
| TotalSlices | int | Slice totali della KB |

- `idle`: nessuna slice in computazione.
- `running`: almeno una slice in computazione.
- `failed`: nessuna slice in computazione e almeno una con errore.
- Una KB senza slice torna `idle` con `runningSlices: 0` e `totalSlices: 0`.

## API Contract

Errors use ProblemDetails.

### Gestione token (admin-only, modulo Tenancy)

| Verb | Route | Request | Response | Errors |
|---|---|---|---|---|
| POST | `/api/tenants/me/service-tokens` | - | `{ id, token, createdAt }` (`token` è plaintext, mostrato una sola volta) | 401 |
| GET | `/api/tenants/me/service-tokens` | - | `[{ id, createdAt }]` (solo metadati, il token non c'è) | 401 |
| DELETE | `/api/tenants/me/service-tokens/{id}` | - | 204 | 401; 404 |

### Refresh e status (dual auth: cookie admin OR service token, modulo KnowledgeBases)

| Verb | Route | Request | Response | Errors |
|---|---|---|---|---|
| POST | `/api/knowledge-bases/{id}/lookups/refresh` | - | 202 Accepted | 401 (token invalido); 404 (KB non esiste); 409 (refresh in corso) |
| GET | `/api/knowledge-bases/{id}/refresh-status` | - | `{ status, lastCompletedAt, runningSlices, totalSlices }` | 401; 404 |

- Il service token viaggia come `Authorization: Bearer <token>`.
- L'endpoint di refresh è lo stesso già esistente, esteso per accettare anche il service token.
- L'endpoint di status è nuovo.

## Authorization

### Middleware di autenticazione

Un middleware nel Host intercetta le richieste con header `Authorization: Bearer <token>`:

1. Se non c'è un header `Authorization: Bearer`, passa through (l'autenticazione cookie funziona come oggi).
2. Se c'è, valida il token tramite `ITenantServiceTokenValidator` (contratto esposto da Tenancy).
3. Se il token è valido: inietta `TenantId` e `QuorumClaimTypes.ServiceToken = "true"` come claim nel `ClaimsPrincipal`.
4. Se il token è invalido o revocato: `401 Unauthorized`, nessun fallback su cookie.

Il middleware non verifica che la KB nella URL appartenga al tenant del token — se ne occupa il tenant filter di EF Core e il check "KB exists" dell'endpoint (la KB di un altro tenant risulta inesistente → 404).

### Policy di autorizzazione

L'endpoint di refresh e quello di status usano una policy dedicata (`KnowledgeBaseRefresh`) con `RequireAssertion` che accetta **in OR**:

- Admin autenticato via cookie (`context.User.IsInRole("Admin")`).
- Service token valido (`context.User.HasClaim(QuorumClaimTypes.ServiceToken)`).

```csharp
options.AddPolicy("KnowledgeBaseRefresh", policy =>
    policy.RequireAssertion(context =>
        context.User.IsInRole("Admin") ||
        context.User.HasClaim(c => c.Type == QuorumClaimTypes.ServiceToken)));
```

Gli endpoint di gestione token (`/api/tenants/me/service-tokens`) restano admin-only (cookie).

### Confini tra moduli

- `ITenantServiceTokenValidator` è un'interfaccia pubblica in `Quorum.Modules.Tenancy.Contracts`.
- L'implementazione vive in Tenancy e accede al DB di Tenancy.
- Il middleware nel Host chiama il validator e compone i due moduli senza che KnowledgeBases sappia del token.

## Edge Cases & Failure Modes

- **KB non esistente o di un altro tenant**: l'endpoint torna 404 (il tenant filter nasconde le KB di altri tenant, il check "exists" fallisce).
- **KB senza lookup**: lo status torna `200` con `idle`, `runningSlices: 0`, `totalSlices: 0`.
- **Refresh già in corso**: `409 Conflict`, sia che la chiamata arrivi dall'admin che dal service token.
- **Token revocato**: la prima chiamata dopo la revoca torna 401. Nessuna sessione in corso viene preservata.
- **Token di un tenant che accede a una KB di un altro tenant**: 404 (come sopra).
- **Generazione token**: il plaintext è mostrato una sola volta. Se l'admin lo perde, deve generarne uno nuovo e revocare il vecchio.

## Non-Functional Requirements

- Il token deve avere almeno 256 bit di entropia (32 byte random).
- La validazione del token usa SHA-256 con confronto in tempo costante.
- Nessun limite di frequenza sulle chiamate (il meccanismo di lease del refresher protegge da computazioni sovrapposte).

## Integrations

- Nessun nuovo servizio esterno. L'ETL è un client HTTP qualsiasi che usa il token.
- `ITenantServiceTokenValidator` è l'unico nuovo contratto tra moduli.

## Acceptance Criteria

**Generazione e gestione token**
- **AC-1** Given un admin autenticato, When genera un service token, Then riceve `{ id, token, createdAt }` con il plaintext del token, e il token è immediatamente utilizzabile.
- **AC-2** Given un admin autenticato, When lista i service token, Then riceve `[{ id, createdAt }]` senza il plaintext.
- **AC-3** Given un admin autenticato, When revoca un service token per id, Then il token è immediatamente invalido (401 alla prossima chiamata) e la riga è rimossa dal database.
- **AC-4** Given un token revocato, When si prova a usarlo, Then si riceve 401.

**Refresh con service token**
- **AC-5** Given un service token valido per il tenant, When chiama `POST /api/knowledge-bases/{id}/lookups/refresh` per una KB del tenant, Then riceve 202 Accepted e il refresh dei lookup viene triggerato.
- **AC-6** Given un service token di un tenant, When chiama il refresh per una KB di un altro tenant, Then riceve 404.
- **AC-7** Given un refresh in corso per una KB, When arriva un'altra richiesta di refresh (admin o service token), Then riceve 409 Conflict.

**Status endpoint**
- **AC-8** Given un service token valido, When chiama `GET /api/knowledge-bases/{id}/refresh-status`, Then riceve `{ status, lastCompletedAt, runningSlices, totalSlices }`.
- **AC-9** Given una KB senza lookup, When lo status è richiesto, Then riceve 200 con `status: "idle"`, `runningSlices: 0`, `totalSlices: 0`.
- **AC-10** Given un cookie admin valido, When chiama lo status endpoint, Then riceve la stessa risposta del service token (dual auth).

**Sicurezza**
- **AC-11** Given una richiesta senza header `Authorization: Bearer`, When arriva al middleware, Then il middleware passa through e l'autenticazione cookie funziona come oggi.
- **AC-12** Given una richiesta con `Authorization: Bearer` e un token invalido, When arriva al middleware, Then riceve 401 senza fallback su cookie.
- **AC-13** Given un service token valido, When chiama un endpoint che non sia refresh o status, Then riceve 401 (il token non autorizza altre operazioni).

## Deferred Decisions

_None._

## Open Questions

_None._
