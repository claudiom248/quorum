# Plan 006: Web Fetch Tool

**Spec:** `docs/specs/006-web-fetch.md` (Draft 2026-10-29)
**Architecture:** Modular Monolith; Vertical Slice inside each module.
**Mode:** `/tdd`: every acceptance criterion becomes a failing test first.
**Status:** Implemented (PR #132)
**Branch:** `feat/006-web-fetch`
**Affected:** Agents (Domain, Persistence, Tools, Features/Tools, Chat), Shared (Networking), `Quorum.Web.Client` (Pages, Api, Components), `tests/Quorum.Integration.Tests`.

## Decisions
| Topic | Choice |
|---|---|
| HTML extraction | **SmartReader** (v0.11.1, port managed di Mozilla Readability, basato su AngleSharp). Estrae il contenuto principale rimuovendo nav/ads/footer, fornisce titolo, autore, data, tempo di lettura. 126K+ downloads, attivamente mantenuto. Dipendenza nuova: `AngleSharp ≥ 1.5.2` (transitiva). |
| PDF extraction | **PdfPig** (riuso). Già presente nel modulo Conversations (`PdfText.Extract`). Stessa logica di estrazione testo page-by-page con reading order. **Estratto in `Quorum.Shared`** in questo plan, così Agents e Conversations lo condividono. |
| Cache | **HybridCache** (pattern esistente in `LookupCache`). In-memory con stampede protection, tag-based eviction, TTL configurabile. Nessuna cache distribuita per l'MVP. |
| Token counting | `CharactersPerToken = 4` (pattern esistente in `ContextBreakdown`). Approssimazione sufficiente per il budgeting. |
| Robots.txt | **Custom parser** (~80 righe): segue la specifica Google 2025 con matching by line length, wildcards `*` e `$`, e l'RFC 9309 per Allow/Disallow. Rispettato quando `WebConfiguration.RespectRobotsTxt = true`. Fail-open in caso di errore di parsing o di fetch del robots.txt. |
| HTTP client | `IHttpClientFactory` con named client `"quorum-web-fetch"` (pattern `SearxngProvider.HttpClientName`). Timeout per-fetch, non sul client. |
| User-Agent | Configurabile in `WebConfiguration` (default `"Quorum/1.0"`). |
| Delay | Configurabile in `WebConfiguration` (default 1000ms). Per-conversazione, non globale. |
| Token budget | Per pagina: default 4000 token (≈16000 caratteri). Totale per invocazione: default 20000 token. Entrambi configurabili nel tool e nel global config. |
| Max URL | Default 5 per invocazione, configurabile. |
| Source citation | `MessageSource` con `KnowledgeBase = "web"` (come web search), `Title`, `Url`. Stessa `SourceNumbering` condivisa. |
| Audit | Automatico tramite `ToolAuditEntry` (nessun lavoro aggiuntivo). |
| Config shape | **Una tabella sola `web_configuration`** con i campi search (spec 005), i default del web fetch e le web properties. Un unico endpoint `/api/settings/web` (GET/PUT). Sostituisce il vecchio `/api/settings/web-search` (Emendamento 1 della spec 005). |
| UI layout | Settings > Web Tools: tabs "Web Search", "Web Fetch", "Web Properties". Ogni tab mostra la sua parte della stessa riga e salva l'intera config (il client UI preserva i campi delle altre tabs). |
| Tool origin | `ToolOrigin.WebFetch` nel enum. Prefix `web_fetch__`, audit server `web_fetch`. |

## Acceptance criteria map
| AC | Step |
|---|---|
| AC-1 (config globale + tool) | 2, 3, 20, 21 |
| AC-2 (tool invocation) | 12, 17, 24, 25 |
| AC-3 (HTML extraction) | 14 |
| AC-4 (PDF extraction) | 8, 13 |
| AC-5 (unsupported content type) | 13 |
| AC-6 (token budget per pagina) | 18 |
| AC-7 (token budget totale) | 18 |
| AC-8 (cache hit) | 15 |
| AC-9 (robots.txt block) | 13, 19 |
| AC-10 (delay tra fetch) | 17 |
| AC-11 (timeout) | 13 |
| AC-12 (citazione fonti) | 18, 25 |
| AC-13 (UI tab Web Fetch) | 29, 31 |
| AC-14 (UI tab Web Properties) | 29, 31 |
| AC-15 (UI lista tool) | 30 |

## Steps

### Domain & Persistence

1. **NuGet**: aggiungere `SmartReader` e `Robots.Net` in `Directory.Packages.props`. `AngleSharp` è dipendenza transitiva di SmartReader.

2. **`Domain/WebConfiguration.cs` (unificato)**: singleton globale della piattaforma con **tutte** le configurazioni web non tool-specific. Sostituisce `WebSearchConfiguration` (spec 005, vedi Emendamento 1) e accorpa i default del web fetch e le web properties.
   - `SingletonId = new("00000000-0000-0000-0000-000000000001")` (riusa il vecchio ID della spec 005)
   - `SearchUrl`, `SearchTimeoutSeconds` (spec 005)
   - `FetchTimeoutSeconds` (default 30, min 5, max 120)
   - `FetchMaxUrlsPerInvocation` (default 5, min 1, max 10)
   - `FetchTokenBudgetPerPage` (default 4000, min 500, max 20000)
   - `FetchTotalTokenBudget` (default 20000, min 2000, max 100000)
   - `UserAgent` (default `"Quorum/1.0"`, max 128 chars)
   - `FetchDelayMs` (default 1000, min 0, max 10000)
   - `RespectRobotsTxt` (default true)
   - `CacheEnabled` (default true)
   - `CacheTtlMinutes` (default 10, min 1, max 60)
   - Factory `CreateDefault(...)`, metodo `Change(...)`.
   - Sostituisce `WebSearchConfiguration.cs`.

3. **`Domain/WebFetchTool.cs`**: entity con binding (come `WebSearchTool`).
   - `ToolId` (PK, 1:1 con Tool), `TenantId`
   - `TokenBudgetPerPage`, `TotalTokenBudget`, `MaxUrlsPerInvocation`, `Timeout`
   - `Scope`, `ScopeTenantIds`, `ScopeAssistantIds`
   - Factory `Create(...)`, metodo `Apply(WebFetchToolConfiguration, now)`.
   - Record `WebFetchToolConfiguration` per i dati validati.

4. **`Domain/Tool.cs`**: aggiungere `ToolOrigin.WebFetch` e factory `CreateWebFetch(tenantId, name, description, configurationHash, policy)`.

5. **`Persistence/AgentsDbContext.cs`**: sostituire `DbSet<WebSearchConfiguration>` con `DbSet<WebConfiguration>`; aggiungere `DbSet<WebFetchTool>`. Configurazione EF: tabella `web_configuration` (sostituisce `web_search_configuration`), tabella `web_fetch_tools`. Singleton rows con fixed ID, query filter su `WebFetchTool` per `TenantId`.

6. **Migration `ConsolidateWebConfiguration`** *(fatta nel Ciclo 1)*: crea `web_configuration` con tutti i campi, copia `Url`/`TimeoutSeconds` dalla vecchia `web_search_configuration`, imposta i default per i campi fetch e properties, drop la vecchia tabella. `Down` ripristina.

7. **Migration `AddWebFetchTools`**: crea la tabella `web_fetch_tools` (1:1 con `tools`, stesso pattern di `web_search_tools`).

### Shared

8. **Estrazione `PdfText` in `Quorum.Shared`**: spostare `PdfText` da `Conversations/Extraction/PdfText.cs` a `Quorum.Shared/Extraction/PdfText.cs`. `PdfPig` è aggiunto in `Quorum.Shared.csproj`. Il modulo Conversations e il modulo Agents referenziano già Shared.

### Tool implementation

9. **`Tools/WebFetch/WebFetchBinding.cs`**: record da `WebFetchTool` (come `WebSearchBinding`).

10. **`Tools/WebFetch/WebFetchToolDefinition.cs`**: JSON schema con parametro `urls` (array di stringhe, max items = MaxUrlsPerInvocation). Metodo `Build(description, maxUrls)`.

11. **`Tools/WebFetch/WebFetchToolHash.cs`**: SHA-256 hash della configurazione (come `WebSearchToolHash`).

12. **`Tools/WebFetch/IWebFetchProvider.cs`**: interfaccia provider.
    - `Task<WebFetchResponse> FetchAsync(WebFetchRequest request, CancellationToken ct)`
    - `WebFetchRequest(Url, TimeoutSeconds, UserAgent, RespectRobotsTxt)`
    - `WebFetchResponse(Outcome, ContentType, Title, Content, FetchedAt)`
    - `WebFetchOutcome: Ok, TimedOut, Unreachable, BlockedByRobots, UnsupportedContentType, Failed`

13. **`Tools/WebFetch/HttpWebFetchProvider.cs`**: implementazione con `IHttpClientFactory`.
    - Named client `"quorum-web-fetch"` (nessun timeout sul client, bound per-request).
    - HEAD request per Content-Type prima del download (skip se non HTML/PDF).
    - GET con timeout per-fetch.
    - User-Agent dal request.
    - Robots.txt check con `Robots.Net` (cache del robots.txt per-origine con TTL).
    - PDF: scarica bytes, passa a `PdfText.Extract` (ora in Shared).
    - HTML: passa a `HtmlContentExtractor`.

14. **`Tools/WebFetch/HtmlContentExtractor.cs`**: wrapper su SmartReader.
    - `static Result<ExtractedContent> Extract(string html, string url)`
    - `ExtractedContent(Title, TextContent, ReadingTime)`
    - Fallback: se SmartReader non estrae contenuto, provare con AngleSharp puro (solo text content).

15. **`Tools/WebFetch/WebFetchCache.cs`**: wrapper su `HybridCache`.
    - `Task<WebFetchResponse?> GetCachedAsync(string url, CancellationToken ct)`
    - `Task CacheAsync(string url, WebFetchResponse response, TimeSpan ttl, CancellationToken ct)`
    - Key: `"web-fetch:{sha256(url)}"`, tag: `"web-fetch"`.
    - Rispetta `WebConfiguration.CacheEnabled`.

16. **`Tools/WebFetch/WebFetchRuntime.cs`**: record runtime (come `WebSearchRuntime`).
    - `WebFetchRuntime(IWebFetchProvider Provider, WebFetchCache Cache, SourceNumbering Numbering, int TimeoutSeconds, int TokenBudgetPerPage, int TotalTokenBudget, int MaxUrlsPerInvocation, int ResultLimit, TimeSpan FetchDelay, bool RespectRobotsTxt, string UserAgent, bool CacheEnabled, TimeSpan CacheTtl, Guid ConversationId)`

17. **`Tools/WebFetch/WebFetchQuorumTool.cs`**: estende `QuorumTool`.
    - `RunAsync`: parse `urls` array dall'input JSON.
    - Per ogni URL: check cache → fetch → extract → truncate to token budget.
    - Delay tra fetch (configurabile).
    - Token budget totale: tronca pagine in ordine fino al budget.
    - Return `ToolOutput` con testo e `ToolSource` per citazioni.
    - Gestione errori per-URL (risultato parziale).

18. **`Tools/WebFetch/WebFetchResultFormat.cs`**: render risultati.
    - Notice: "Content from the web. Cite sources by number..."
    - Per URL: `[n] Title · Url` + contenuto estratto (troncato).
    - Budget: `resultLimit - overhead`, come `WebSearchResultFormat`.
    - Gestione truncation: messaggio onesto se contenuto troncato.
    - Gestione errori per-URL: messaggio nell'output.

19. **`Tools/WebFetch/RobotsCache.cs`**: cache per-origine del robots.txt con parser custom.
    - `Task<bool> IsAllowedAsync(string url, string userAgent, CancellationToken ct)`
    - Cache in-memory del robots.txt per (scheme+host+port) con TTL 1 ora.
    - Parser: specifiche Google 2025 — matching by line length (il match più lungo vince, Allow > Disallow a pari lunghezza), wildcards `*` e `$` (fine-stringa), gruppi User-Agent multipli (`User-agent: googlebot`, `User-agent: g*bot`).
    - Fall-back: se il parsing fallisce o il fetch del robots.txt fallisce, si permette il fetch (fail-open).

### Integration

20. **`Features/Tools/WebFetchToolEndpoints.cs`**: CRUD tool web fetch (come `WebSearchToolEndpoints`).
    - `GET /api/tools/web-fetch` → lista
    - `POST /api/tools/web-fetch` → crea
    - `GET /api/tools/web-fetch/{id}` → dettaglio
    - `PUT /api/tools/web-fetch/{id}` → aggiorna
    - `DELETE /api/tools/web-fetch/{id}` → elimina
    - Validazione con `WebFetchToolValidation`.

21. **`Features/Tools/WebSettingsEndpoints.cs` (sostituisce `WebSearchSettingsEndpoints.cs`)** *(fatto nel Ciclo 1)*: un unico endpoint `/api/settings/web` (GET/PUT) che legge e scrive l'intera riga di `WebConfiguration` — i campi search (spec 005), i default fetch e le web properties. Il vecchio `/api/settings/web-search` è rimosso (Emendamento 1 della spec 005).

22. **`Features/Tools/WebFetchToolValidation.cs`**: validazione richieste tool web fetch.
    - Name: 1-40 chars, pattern `[a-z0-9][a-z0-9_-]*`
    - Description: 1-1000 chars
    - TokenBudgetPerPage: 500-20000
    - TotalTokenBudget: 2000-100000
    - MaxUrlsPerInvocation: 1-10
    - Timeout: 5-120
    - Scope: valid scope rules

23. **`Tools/ToolDefinitions.cs`**: aggiungere `WebFetchPrefix = "web_fetch__"`, `WebFetchAuditServer = "web_fetch"`, metodo `WebFetch(string toolName)`.

24. **`Tools/ToolPool.cs`**: aggiungere join su `db.WebFetchTools` nel pool query. Scope rule per `WebFetchTool`. Aggiungere `WebFetchTools()` in `ToolVisibility`.

25. **`Tools/ActiveTools.cs`**: caricare `WebFetchBinding` per i tool web fetch. Aggiungere `WebFetchBinding?` al record `ActiveTool`. Generare definizione con `WebFetchToolDefinition.Build(...)`.

26. **`Tools/QuorumTool.cs`**: aggiungere case `ToolOrigin.WebFetch` in `QuorumToolFactory.Create`. Aggiungere `WebFetchRuntime?` parametro.

27. **`Chat/AssistantChat.cs`**: creare `WebFetchRuntime` se l'assistant ha tool web fetch.
    - Leggere `WebConfiguration` dal DB (la stessa riga che già serve per la ricerca).
    - Passare runtime al `toolFactory.Create(...)`.
    - Aggiungere `WebFetchQuorumTool` al check per `SearchCitationNotice`.

28. **`AgentsModule.cs`**: registrare servizi.
    - `services.AddScoped<IWebFetchProvider, HttpWebFetchProvider>()`
    - `services.AddScoped<WebFetchCache>()`
    - `services.AddScoped<RobotsCache>()`
    - `services.AddServerHttpClient(HttpWebFetchProvider.HttpClientName)`

### UI

29. **`Api/WebFetchApiModels.cs`** e **`Api/QuorumApi.WebFetch.cs`**: DTOs, limits e API client methods per i tool web fetch. La config globale è già in `Api/WebApiModels.cs` e `Api/QuorumApi.Web.cs` (fatti nel Ciclo 1).
    - `WebFetchToolListItem`, `WebFetchToolDetail`, `WebFetchToolRequest`, `CreatedWebFetchTool`
    - `WebFetchToolLimits` (costanti)
    - Methods: `ListWebFetchToolsAsync`, `GetWebFetchToolAsync`, `CreateWebFetchToolAsync`, `UpdateWebFetchToolAsync`, `DeleteWebFetchToolAsync`

30. **`Pages/WebFetchTools.razor`** e **`WebFetchToolForm.cs`**: list page con drawer form (come `WebSearchTools.razor`).
    - Lista tool con nome, descrizione, scope, stato.
    - Drawer con form: name, description, token budget, timeout, max URLs, scope, policy.
    - Route: `/tools/web-fetch`.

31. **`Pages/Settings.razor`**: aggiungere le tabs "Web Fetch" e "Web Properties" (la tab "Web Search" esiste già).
    - Tab "Web Fetch": `fetchTimeoutSeconds`, `fetchMaxUrlsPerInvocation`, `fetchTokenBudgetPerPage`, `fetchTotalTokenBudget`.
    - Tab "Web Properties": `userAgent`, `fetchDelayMs`, `respectRobotsTxt`, `cacheEnabled`, `cacheTtlMinutes`.
    - Ogni tab carica l'intera `WebConfigurationInfo` e la rimanda integralmente nel PUT, così le altre tabs non perdono i loro campi.

32. **`Components/NavMenu.razor`**: aggiungere "Web fetch tools" sotto il group Tools.

### Tests

33. **`tests/.../Fixtures/FakeWebServer.cs`**: fake HTTP server per test fetch.
    - Serve HTML con contenuto noto, PDF, contenuti non supportati.
    - Supporto robots.txt configurabile.
    - Delay configurabile per test timeout.

34. **`tests/.../WebFetch/WebFetchConfigurationTests.cs`** *(fatto nel Ciclo 1)*: test dei campi fetch e properties della `WebConfiguration` unificata.

35. **`tests/.../WebFetch/WebFetchToolConfigurationTests.cs`**: test CRUD tool.
    - Create, list, get, update, delete, scope, validazione.

36. **`tests/.../WebFetch/WebFetchToolCallTests.cs`**: test invocazione tool.
    - AC-2: fetch singolo URL HTML, risultato con contenuto.
    - AC-3: estrazione HTML rimuove nav/ads.
    - AC-4: fetch PDF, estrazione testo.
    - AC-5: URL con contenuto non supportato → errore per-URL.
    - AC-9: robots.txt blocca → errore per-URL.
    - AC-10: delay tra fetch verificato.
    - AC-11: timeout → errore per-URL.
    - AC-12: fonti citate con MessageSource.

37. **`tests/.../WebFetch/WebFetchToolLimitsTests.cs`**: test token budget.
    - AC-6: contenuto > token budget per pagina → troncato.
    - AC-7: somma > token budget totale → pagine troncate in ordine.
    - Max URL superato → errore.

38. **`tests/.../WebFetch/WebFetchCacheTests.cs`**: test cache.
    - AC-8: cache hit → nessuna seconda richiesta HTTP.
    - Cache disabled → sempre fetch.
    - TTL scaduto → nuovo fetch.

### Verification

39. `dotnet build -c Release` e `dotnet format --verify-no-changes`. Suite completa dei test.

## Implementation notes (what differs from the steps above)

- **Nullable overrides, resolved at call time** (steps 3, 16, 22). A web fetch tool stores `null` where the administrator left a field empty, and `ActiveTools` resolves `tool ?? platform row ?? platform default` for every answer, so the Settings → Web Fetch tab reaches the tools that never overrode a number (AC-1, AC-13). Migration `MakeWebFetchToolOverridesOptional` makes the four columns nullable and turns the numbers the first validator baked in back into inheritance.
- **The tool's own numbers win** (steps 16, 17). `WebFetchQuorumTool` takes the budgets, the limit and the timeout from its binding; `WebFetchRuntime` keeps only what is platform-wide (delay, User-Agent, robots, cache, the tenant's result cap, and the timeout ceiling).
- **Robots.Net was not added** (steps 1, 19): `RobotsCache` holds the custom parser the Decisions table chose — matching by line length, `*` and `$`, fail-open on any error. `Directory.Packages.props` gained `SmartReader` only.
- **The renderer also clamps to the tenant's result cap** (step 18): past `ToolLimits.MaxResultCharacters` the invoker discards the whole result and blames arguments the model never chose, so `WebFetchResultFormat` stops at the cap itself.
- **The plain-text fallback strips the page's noise** (step 14): Readability calls a short page unreadable, so the fallback picks the content container (`article`, `main`, `.post`) and removes nav, header, footer, aside and ads before reading it (AC-3).
- **Not done on purpose**: `WebFetchCache.ClearAsync` has no caller. AC-14 asks for the toggle, not for evicting what was remembered — and with the toggle off the tool never reads the memory. Wiring the eviction into `PUT /api/settings/web` is a product decision, not a gap in this plan.

## Risks
- **SmartReader qualità estrazione**: alcuni siti con layout complessi potrebbero non essere estratti bene. Mitigazione: fallback su AngleSharp puro per text content se SmartReader restituisce vuoto.
- **Robots.Net affidabilità**: alcuni robots.txt non standard potrebbero non essere parsati correttamente. Mitigazione: fail-open (permesso il fetch).
- **PDF di grandi dimensioni**: un PDF con centinaia di pagine potrebbe superare il token budget. Mitigazione: estrazione page-by-page con stop quando il budget è raggiunto.
- **Cache stampede**: HybridCache ha stampede protection nativa, ma il primo fetch di una URL popolare potrebbe essere lento. Mitigazione: il delay tra fetch e il timeout bounding limitano l'impatto.
- **Migration conflict**: se un'altra migration è stata aggiunta al modulo Agents nel frattempo. Mitigazione: rebasing sul main più recente prima di migrare.
- **Breaking change API `/api/settings/web-search`**: rimosso, sostituito da `/api/settings/web`. Non è ancora in produzione (PR #129 appena mergiata, non deployata). Spec 005 aggiornata con Emendamento 1.
- **UI last-write-wins**: ogni tab della UI fa PUT dell'intera config; se due admin salvano tabs diverse in parallelo, l'ultimo vince e perde i campi dell'altro. Accettato per l'MVP, coerente con il pattern attuale. Da rivedere se serve concurrency.

## Open Questions
None.
