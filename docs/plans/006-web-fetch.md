# Plan 006: Web Fetch Tool

**Spec:** `docs/specs/006-web-fetch.md` (Draft 2026-10-29)
**Architecture:** Modular Monolith; Vertical Slice inside each module.
**Mode:** `/tdd`: every acceptance criterion becomes a failing test first.
**Status:** Draft
**Branch:** `feat/006-web-fetch`
**Affected:** Agents (Domain, Persistence, Tools, Features/Tools, Chat), Shared (Networking), `Quorum.Web.Client` (Pages, Api, Components), `tests/Quorum.Integration.Tests`.

## Decisions
| Topic | Choice |
|---|---|
| HTML extraction | **SmartReader** (v0.11.1, port managed di Mozilla Readability, basato su AngleSharp). Estrae il contenuto principale rimuovendo nav/ads/footer, fornisce titolo, autore, data, tempo di lettura. 126K+ downloads, attivamente mantenuto. Dipendenza nuova: `AngleSharp ≥ 1.5.2` (transitiva). |
| PDF extraction | **PdfPig** (riuso). Già presente nel modulo Conversations (`PdfText.Extract`). Stessa logica di estrazione testo page-by-page con reading order. |
| Cache | **HybridCache** (pattern esistente in `LookupCache`). In-memory con stampede protection, tag-based eviction, TTL configurabile. Nessuna cache distribuita per l'MVP. |
| Token counting | `CharactersPerToken = 4` (pattern esistente in `ContextBreakdown`). Approssimazione sufficiente per il budgeting. |
| Robots.txt | **Robots.Net** per il parsing. Rispettato quando `WebProperties.RespectRobotsTxt = true`. Fail-open in caso di errore di parsing. |
| HTTP client | `IHttpClientFactory` con named client `"quorum-web-fetch"` (pattern `SearxngProvider.HttpClientName`). Timeout per-fetch, non sul client. |
| User-Agent | Configurabile in `WebProperties` (default `"Quorum/1.0"`). Condiviso con web search. |
| Delay | Configurabile in `WebProperties` (default 1000ms). Per-conversazione, non globale. |
| Token budget | Per pagina: default 4000 token (≈16000 caratteri). Totale per invocazione: default 20000 token. Entrambi configurabili nel tool e nel global config. |
| Max URL | Default 5 per invocazione, configurabile. |
| Source citation | `MessageSource` con `KnowledgeBase = "web"` (come web search), `Title`, `Url`. Stessa `SourceNumbering` condivisa. |
| Audit | Automatico tramite `ToolAuditEntry` (nessun lavoro aggiuntivo). |
| UI layout | Settings > Web Tools: tabs "Web Search", "Web Fetch", "Settings". La tab "Settings" contiene le Web Properties globali (User-Agent, delay, cache, robots.txt). |
| Tool origin | `ToolOrigin.WebFetch` nel enum. Prefix `web_fetch__`, audit server `web_fetch`. |

## Acceptance criteria map
| AC | Step |
|---|---|
| AC-1 (config globale + tool) | 2, 3, 20, 21 |
| AC-2 (tool invocation) | 12, 17, 25, 26 |
| AC-3 (HTML extraction) | 14 |
| AC-4 (PDF extraction) | 8, 13 |
| AC-5 (unsupported content type) | 13 |
| AC-6 (token budget per pagina) | 18 |
| AC-7 (token budget totale) | 18 |
| AC-8 (cache hit) | 15 |
| AC-9 (robots.txt block) | 13, 19 |
| AC-10 (delay tra fetch) | 17 |
| AC-11 (timeout) | 13 |
| AC-12 (citazione fonti) | 18, 26 |
| AC-13 (UI tab Web Fetch) | 30, 32 |
| AC-14 (UI tab Settings/Web Properties) | 30, 32 |
| AC-15 (UI lista tool) | 31 |

## Steps

### Domain & Persistence

1. **NuGet**: aggiungere `SmartReader` e `Robots.Net` in `Directory.Packages.props`. `AngleSharp` è dipendenza transitiva di SmartReader.

2. **`Domain/WebFetchConfiguration.cs`**: singleton globale (come `WebSearchConfiguration`).
   - `SingletonId = new("00000000-0000-0000-0000-000000000002")`
   - `DefaultTimeoutSeconds` (default 30, min 5, max 120)
   - `DefaultMaxUrlsPerInvocation` (default 5, min 1, max 10)
   - `DefaultTokenBudgetPerPage` (default 4000, min 500, max 20000)
   - `DefaultTotalTokenBudget` (default 20000, min 2000, max 100000)
   - Factory `Create(...)`, metodo `Change(...)`.

3. **`Domain/WebFetchTool.cs`**: entity con binding (come `WebSearchTool`).
   - `ToolId` (PK, 1:1 con Tool), `TenantId`
   - `TokenBudgetPerPage`, `TotalTokenBudget`, `MaxUrlsPerInvocation`, `Timeout`
   - `Scope`, `ScopeTenantIds`, `ScopeAssistantIds`
   - Factory `Create(...)`, metodo `Apply(WebFetchToolConfiguration, now)`.
   - Record `WebFetchToolConfiguration` per i dati validati.

4. **`Domain/WebProperties.cs`**: singleton globale condiviso con web search.
   - `SingletonId = new("00000000-0000-0000-0000-000000000003")`
   - `UserAgent` (default `"Quorum/1.0"`, max 128 chars)
   - `FetchDelayMs` (default 1000, min 0, max 10000)
   - `RespectRobotsTxt` (default true)
   - `CacheEnabled` (default true)
   - `CacheTtlMinutes` (default 10, min 1, max 60)
   - Factory `Create(...)`, metodo `Change(...)`.

5. **`Domain/Tool.cs`**: aggiungere `ToolOrigin.WebFetch` e factory `CreateWebFetch(tenantId, name, description, configurationHash, policy)`.

6. **`Persistence/AgentsDbContext.cs`**: aggiungere `DbSet<WebFetchConfiguration>`, `DbSet<WebFetchTool>`, `DbSet<WebProperties>`. Configurazione EF: tabelle `web_fetch_configuration`, `web_fetch_tools`, `web_properties`. Singleton rows con fixed ID, query filter su `WebFetchTool` per `TenantId`.

7. **Migration**: unica `AddWebFetch` per le 3 tabelle.

### Shared

8. **Estrazione `PdfText` in `Quorum.Shared`**: spostare `PdfText` da `Conversations/Extraction/PdfText.cs` a `Quorum.Shared/Extraction/PdfText.cs` (o namespace simile). Aggiornare i riferimenti nel modulo Conversations. Entrambi i moduli (Agents e Conversations) referenziano già Shared.

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
    - Rispetta `WebProperties.CacheEnabled`.

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

19. **`Tools/WebFetch/RobotsCache.cs`**: cache per-origine del robots.txt.
    - `Task<bool> IsAllowedAsync(string url, string userAgent, CancellationToken ct)`
    - Cache in-memory del robots.txt per (scheme+host+port) con TTL 1 ora.
    - Usa `Robots.Net` per il parsing.

### Integration

20. **`Features/Tools/WebFetchToolEndpoints.cs`**: CRUD tool web fetch (come `WebSearchToolEndpoints`).
    - `GET /api/tools/web-fetch` → lista
    - `POST /api/tools/web-fetch` → crea
    - `GET /api/tools/web-fetch/{id}` → dettaglio
    - `PUT /api/tools/web-fetch/{id}` → aggiorna
    - `DELETE /api/tools/web-fetch/{id}` → elimina
    - Validazione con `WebFetchToolValidation`.

21. **`Features/Tools/WebFetchSettingsEndpoints.cs`**: config globale web fetch.
    - `GET /api/settings/web-fetch` → leggi config
    - `PUT /api/settings/web-fetch` → aggiorna config
    - Singleton upsert (come `WebSearchSettingsEndpoints`).

22. **`Features/Tools/WebPropertiesEndpoints.cs`**: web properties globali.
    - `GET /api/settings/web-properties` → leggi properties
    - `PUT /api/settings/web-properties` → aggiorna properties
    - Singleton upsert.

23. **`Features/Tools/WebFetchToolValidation.cs`**: validazione richieste.
    - Name: 1-40 chars, pattern `[a-z0-9][a-z0-9_-]*`
    - Description: 1-1000 chars
    - TokenBudgetPerPage: 500-20000
    - TotalTokenBudget: 2000-100000
    - MaxUrlsPerInvocation: 1-10
    - Timeout: 5-120
    - Scope: valid scope rules

24. **`Tools/ToolDefinitions.cs`**: aggiungere `WebFetchPrefix = "web_fetch__"`, `WebFetchAuditServer = "web_fetch"`, metodo `WebFetch(string toolName)`.

25. **`Tools/ToolPool.cs`**: aggiungere join su `db.WebFetchTools` nel pool query. Scope rule per `WebFetchTool`. Aggiungere `WebFetchTools()` in `ToolVisibility`.

26. **`Tools/ActiveTools.cs`**: caricare `WebFetchBinding` per i tool web fetch. Aggiungere `WebFetchBinding?` al record `ActiveTool`. Generare definizione con `WebFetchToolDefinition.Build(...)`.

27. **`Tools/QuorumTool.cs`**: aggiungere case `ToolOrigin.WebFetch` in `QuorumToolFactory.Create`. Aggiungere `WebFetchRuntime?` parametro.

28. **`Chat/AssistantChat.cs`**: creare `WebFetchRuntime` se l'assistant ha tool web fetch.
    - Leggere `WebFetchConfiguration` e `WebProperties` dal DB.
    - Passare runtime al `toolFactory.Create(...)`.
    - Aggiungere `WebFetchQuorumTool` al check per `SearchCitationNotice`.

29. **`AgentsModule.cs`**: registrare servizi.
    - `services.AddScoped<IWebFetchProvider, HttpWebFetchProvider>()`
    - `services.AddScoped<WebFetchCache>()`
    - `services.AddScoped<RobotsCache>()`
    - `services.AddServerHttpClient(HttpWebFetchProvider.HttpClientName)`

### UI

30. **`Api/WebFetchApiModels.cs`** e **`Api/QuorumApi.WebFetch.cs`**: DTOs, limits e API client methods.
    - `WebFetchConfigurationInfo`, `WebFetchToolListItem`, `WebFetchToolDetail`, `WebFetchToolRequest`, `CreatedWebFetchTool`
    - `WebPropertiesInfo`
    - `WebFetchToolLimits` (costanti)
    - Methods: `GetWebFetchConfigurationAsync`, `UpdateWebFetchConfigurationAsync`, `GetWebPropertiesAsync`, `UpdateWebPropertiesAsync`, `ListWebFetchToolsAsync`, `GetWebFetchToolAsync`, `CreateWebFetchToolAsync`, `UpdateWebFetchToolAsync`, `DeleteWebFetchToolAsync`

31. **`Pages/WebFetchTools.razor`** e **`WebFetchToolForm.cs`**: list page con drawer form (come `WebSearchTools.razor`).
    - Lista tool con nome, descrizione, scope, stato.
    - Drawer con form: name, description, token budget, timeout, max URLs, scope, policy.
    - Route: `/tools/web-fetch`.

32. **`Pages/Settings.razor`**: modificare tabs.
    - Rinominare tab "Web search" → spostare in "Web Tools" group.
    - Aggiungere tab "Web Fetch" per config globale web fetch.
    - Aggiungere tab "Web Properties" per User-Agent, delay, cache, robots.txt.

33. **`Components/NavMenu.razor`**: aggiornare navigazione.
    - Sostituire "Web search tools" con "Web fetch tools" o riorganizzare in "Web Tools" group.

### Tests

34. **`tests/.../Fixtures/FakeWebServer.cs`**: fake HTTP server per test fetch.
    - Serve HTML con contenuto noto, PDF, contenuti non supportati.
    - Supporto robots.txt configurabile.
    - Delay configurabile per test timeout.

35. **`tests/.../WebFetch/WebFetchConfigurationTests.cs`**: test config globale e web properties.
    - GET/PUT settings, singleton behavior, validazione.

36. **`tests/.../WebFetch/WebFetchToolConfigurationTests.cs`**: test CRUD tool.
    - Create, list, get, update, delete, scope, validazione.

37. **`tests/.../WebFetch/WebFetchToolCallTests.cs`**: test invocazione tool.
    - AC-2: fetch singolo URL HTML, risultato con contenuto.
    - AC-3: estrazione HTML rimuove nav/ads.
    - AC-4: fetch PDF, estrazione testo.
    - AC-5: URL con contenuto non supportato → errore per-URL.
    - AC-9: robots.txt blocca → errore per-URL.
    - AC-10: delay tra fetch verificato.
    - AC-11: timeout → errore per-URL.
    - AC-12: fonti citate con MessageSource.

38. **`tests/.../WebFetch/WebFetchToolLimitsTests.cs`**: test token budget.
    - AC-6: contenuto > token budget per pagina → troncato.
    - AC-7: somma > token budget totale → pagine troncate in ordine.
    - Max URL superato → errore.

39. **`tests/.../WebFetch/WebFetchCacheTests.cs`**: test cache.
    - AC-8: cache hit → nessuna seconda richiesta HTTP.
    - Cache disabled → sempre fetch.
    - TTL scaduto → nuovo fetch.

### Verification

40. `dotnet build -c Release` e `dotnet format --verify-no-changes`. Suite completa dei test.

## Risks
- **SmartReader qualità estrazione**: alcuni siti con layout complessi potrebbero non essere estratti bene. Mitigazione: fallback su AngleSharp puro per text content se SmartReader restituisce vuoto.
- **Robots.Net affidabilità**: alcuni robots.txt non standard potrebbero non essere parsati correttamente. Mitigazione: fail-open (permesso il fetch).
- **PDF di grandi dimensioni**: un PDF con centinaia di pagine potrebbe superare il token budget. Mitigazione: estrazione page-by-page con stop quando il budget è raggiunto.
- **Cache stampede**: HybridCache ha stampede protection nativa, ma il primo fetch di una URL popolare potrebbe essere lento. Mitigazione: il delay tra fetch e il timeout bounding limitano l'impatto.
- **Migration conflict**: se un'altra migration è stata aggiunta al modulo Agents nel frattempo. Mitigazione: rebasing sul main più recente prima di migrare.
- **PdfPig cross-module dependency**: `PdfText` è nel modulo Conversations, il tool è in Agents. **Decisione**: estrarre `PdfText` in `Quorum.Shared` (dove già vivono utility condivise). Entrambi i moduli referenziano Shared.

## Open Questions
None.
