# Plan 011: Service Token for Knowledge Base Refresh

**Spec:** `docs/specs/011-service-token-refresh.md` (Approved 2026-10-28)
**Architecture:** Modular Monolith; Vertical Slice inside each module.
**Mode:** `/tdd`: every acceptance criterion becomes a failing test first.
**Status:** Draft
**Affected:** Tenancy (Contracts, Domain, Persistence, Features), Host (Infrastructure, Program.cs), KnowledgeBases (Features/Lookups), `tests/Quorum.Integration.Tests`.

## Decisions
| Topic | Choice |
|---|---|
| Token format | `svc_` + 32 byte random (`RandomNumberGenerator`) codificati URL-safe base64. Il prefisso aiuta il riconoscimento a vista; 256 bit di entropia. |
| Storage | Solo l'hash SHA-256 del token nel DB. Il plaintext è tornato una sola volta alla generazione e mai persistito. Hard delete alla revoca. |
| Trasporto | `Authorization: Bearer <token>`. |
| Auth mechanism | Middleware nel Host (`ServiceTokenAuthenticationMiddleware`) dopo `UseAuthentication()`. Skip se l'user è già autenticato (cookie). Valida con `ITenantServiceTokenValidator` (contratto Tenancy), inietta claim `TenantId` + `ServiceToken`. 401 secco se il token è invalido, nessun fallback cookie. |
| Policy | `KnowledgeBaseRefresh` con `RequireAssertion` (Admin role OR `QuorumClaimTypes.ServiceToken` claim). L'endpoint di refresh esistente cambia policy da `KnowledgeBaseManage` a `KnowledgeBaseRefresh`; lo status endpoint è nuovo con la stessa policy. |
| Confini | `ITenantServiceTokenValidator` in `Tenancy.Contracts`. Implementazione `internal` in Tenancy. Il middleware nel Host compone i moduli; KnowledgeBases non sa nulla del token. |
| Tenant filter | Il middleware inietta `QuorumClaimTypes.TenantId` → `HttpTenantContext` la legge → il filtro globale di EF Core funziona automaticamente. La KB di un altro tenant risulta inesistente (404). |
| Antiforgery | `AntiforgeryHeaderMiddleware` controlla `request.Cookies.ContainsKey(AuthCookieName)`. Richieste Bearer-only non hanno il cookie → nessun conflitto. |
| Status computation | Query sulle slice della KB (con tenant filter): `running` se qualsiasi `ComputingSince != null`; `failed` se nessuna in corsa e `LastError != null`; `idle` altrimenti. KB senza slice → `idle`, 0, 0. |
| Test strategy | Integration tests con Testcontainers (come il resto del progetto). Un test per AC. Il token di test è generato con la factory dell'entità. |

## Order and parallel work
- **PR 1** (Tenancy: contracts, domain, persistence, validator, endpoints, policy) è la fondazione.
- **PR 2** (Host: middleware + Program.cs) dipende da PR 1 per il validator.
- **PR 3** (KnowledgeBases: policy switch + status endpoint) dipende da PR 1 per la policy.
- **PR 2 e PR 3 sono indipendenti tra loro** e possono essere sviluppati in parallelo dopo PR 1.
- **PR 4** (integration tests) dipende da PR 2 e PR 3.

## Acceptance criteria map
| AC | PR |
|---|---|
| AC-1, AC-2, AC-3 | 1 |
| AC-4, AC-5, AC-6, AC-7 | 3, 4 |
| AC-8, AC-9, AC-10 | 3, 4 |
| AC-11, AC-12, AC-13 | 2, 4 |

## Steps

### PR 1 — Tenancy: contracts, domain, persistence, validator, endpoints, policy (`feat/011-service-token-tenancy`)

1. `QuorumClaimTypes.ServiceToken = "quorum:service_token"` in `Tenancy.Contracts`. `ITenantServiceTokenValidator` in `Tenancy.Contracts`: `Task<Guid?> ValidateAsync(string tokenHash, CancellationToken ct)`.

2. Entità `ServiceToken` in `Tenancy/Domain/ServiceToken.cs`: `Id`, `TenantId`, `TokenHash`, `CreatedAt`. Factory `Create(tenantId, now)` → `(ServiceToken, string plainText)` con `RandomNumberGenerator.GetBytes(32)`, URL-safe base64, prefisso `svc_`, hash SHA-256. Metodo `MatchesHash(plainText)` con confronto constant-time (`CryptographicOperations.FixedTimeEquals` sugli hash).

3. `TenancyDbContext`: `DbSet<ServiceToken> ServiceTokens`, configurazione tabella `service_tokens`, indice unico su `TokenHash`. Migrazione `AddServiceToken`.

4. `TenantServiceTokenValidator` (`internal sealed`) in `Tenancy/Features/ServiceTokens/`: calcola SHA-256 del token ricevuto, query `SingleOrDefaultAsync(t => t.TokenHash == hash)`, ritorna `TenantId` o `null`.

5. `ServiceTokenEndpoints` (`IEndpointGroup`) in `Tenancy/Features/ServiceTokens/`:
   - `POST /api/tenants/me/service-tokens` → genera, torna `{ id, token, createdAt }` (plaintext una volta). Admin-only.
   - `GET /api/tenants/me/service-tokens` → lista `{ id, createdAt }` (no plaintext). Admin-only.
   - `DELETE /api/tenants/me/service-tokens/{id}` → hard delete, 204. Admin-only. 404 se non esiste o è di un altro tenant.
   - AC-1, AC-2, AC-3.

6. `TenancyModule`: registrazione `AddScoped<ITenantServiceTokenValidator, TenantServiceTokenValidator>()`. Policy `KnowledgeBaseRefresh` con `RequireAssertion(context => context.User.IsInRole("Admin") || context.User.HasClaim(QuorumClaimTypes.ServiceToken))`.

### PR 2 — Host: middleware (`feat/011-service-token-middleware`)

7. `ServiceTokenAuthenticationMiddleware` in `Host/Infrastructure/`:
   - Skip se `context.User.Identity?.IsAuthenticated == true` (cookie già autenticato → passa through).
   - Skip se nessun header `Authorization: Bearer`.
   - Estrae il token dal header.
   - Valida con `ITenantServiceTokenValidator` (risolto da DI).
   - Successo: crea `ClaimsIdentity` con `AuthenticationType = "ServiceToken"`, claim `TenantId` e `ServiceToken = "true"`. Assegna `context.User = new ClaimsPrincipal(identity)`.
   - Fallimento: 401 ProblemDetails (`auth.invalid_service_token`), nessun fallback.
   - AC-11, AC-12.

8. `Program.cs`: `app.UseMiddleware<ServiceTokenAuthenticationMiddleware>()` dopo `UseAuthentication()`, prima di `UseAuthorization()`.

### PR 3 — KnowledgeBases: policy switch + status endpoint (`feat/011-refresh-status`)

9. `LookupRefreshEndpoints`: cambia `RequireAuthorization(QuorumPolicies.KnowledgeBaseManage)` → `RequireAuthorization(QuorumPolicies.KnowledgeBaseRefresh)` sul group. L'endpoint di refresh accetta ora sia admin che service token. AC-5, AC-6, AC-7.

10. Nuovo endpoint `GET /api/knowledge-bases/{id}/refresh-status` nello stesso file:
    - Query le slice della KB (con tenant filter): conta totali, in corsa, ultima computazione, errori.
    - `RefreshStatusResponse(string Status, DateTimeOffset? LastCompletedAt, int RunningSlices, int TotalSlices)`.
    - KB non esiste → 404. KB senza slice → `idle`, 0, 0.
    - Policy `KnowledgeBaseRefresh` (dual auth).
    - AC-8, AC-9, AC-10.

### PR 4 — Integration tests (`feat/011-service-token-tests`)

11. Test class `ServiceTokenTests` in `tests/Quorum.Integration.Tests/`:
    - AC-1: POST genera token, risposta contiene plaintext, il token è immediatamente utilizzabile.
    - AC-2: GET lista, nessun plaintext nella risposta.
    - AC-3: DELETE revoca, riga rimossa dal DB.
    - AC-4: Token revocato → 401 sul refresh.
    - AC-5: Refresh con Bearer valido → 202.
    - AC-6: Refresh su KB di un altro tenant → 404.
    - AC-7: Refresh mentre uno è in corso → 409.
    - AC-8: Status con Bearer → 200 con shape corretta.
    - AC-9: Status su KB senza slice → idle, 0, 0.
    - AC-10: Status con cookie admin → stessa risposta.
    - AC-11: Richiesta senza Bearer → cookie funziona come oggi.
    - AC-12: Bearer invalido → 401.
    - AC-13: Bearer su endpoint non refresh/status → 401.

### PR 5 — Build verification (`chore/011-verification`)

12. `dotnet build -c Release` e `dotnet format --verify-no-changes` prima di aprire la PR. Suite completa dei test alla fine.

## Risks
- **AC-13 (token su altri endpoint)**: il middleware autentica con la claim `ServiceToken`, ma la policy degli altri endpoint richiede il role `Admin`. Il service token non ha quel role → 401 automaticamente. Da verificare esplicitamente nel test.
- **Migration conflict**: se un'altra migration è stata aggiunta al modulo Tenancy nel frattempo, il merge del model snapshot può conflictare. Rebasing sul main più recente prima di migrare.
- **Release build**: gli analyzer girano in Release in CI; ogni PR è buildata in Release prima di essere aperta (regola del progetto).
- **Constant-time comparison**: usare `CryptographicOperations.FixedTimeEquals` (non `string.Equals`) per il confronto degli hash. Un confronto non constant-time espone a timing attacks.

## Open Questions
None.
