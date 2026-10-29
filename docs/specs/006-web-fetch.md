# Spec 006: Web Fetch Tool

**Status:** Draft (2026-10-29)
**Date:** 2026-10-29

## Problem

La web search (spec 005) restituisce titolo, URL e snippet dei risultati, ma per rispondere a domande complesse l'agente ha bisogno di leggere il contenuto completo delle pagine. Senza un tool di web fetch, l'agente può lavorare solo con gli snippet (che spesso non contengono informazioni sufficienti) o chiedere all'utente di fornire manualmente il contenuto delle pagine.

Gli utenti finali ricevono risposte incomplete o superficiali quando la web search non basta. Gli admin non possono offrire agenti capaci di approfondire i contenuti web. Non esiste un workaround: se gli snippet non bastano, l'agente non può recuperare il contenuto completo delle pagine.

## Scope

### In

- Tool di web fetch invocabile dagli agenti tramite function calling (MEAI `FunctionInvokingChatClient`)
- Fetch del contenuto di una o più URL (max 5 per invocazione)
- Supporto HTML (con estrazione del contenuto principale tramite LlamaIndex.WebReader o libreria simile)
- Supporto PDF (riuso di PdfPig, già presente nel modulo Conversations)
- Gestione errori: timeout (30s per URL), retry con backoff, messaggi di errore al modello
- Politeness: delay configurabile tra fetch (default 1s), User-Agent configurabile (default "Quorum/1.0"), rispetto robots.txt
- Cache in-memory dei risultati (IMemoryCache, pattern esistente), TTL configurabile (default 10 minuti), toggle globale
- Token budget per pagina (default 4000 token) e totale per invocazione (default 20000 token), con truncation e messaggio onesto se il contenuto è troppo grande
- Citazione delle fonti nelle risposte (pattern esistente del RAG, riutilizzando MessageSource)
- UI per la configurazione del tool web fetch (tab "Web Fetch" in Settings > Web Tools)
- UI per le web properties globali (tab "Settings" in Settings > Web Tools): User-Agent, delay, cache, robots.txt

### Out

- Supporto per formati diversi da HTML e PDF (Word, Excel, PowerPoint, ecc.) — rimandato a quando ci sono richieste specifiche
- Cache distribuita (Redis) — rimandata a quando ci sono dati sul volume e sulla condivisione tra istanze
- Rendering JavaScript (headless browser) — rimandato a quando ci sono casi d'uso concreti (la maggior parte dei contenuti testuali è disponibile nell'HTML statico)
- Ottimizzazioni di performance basate su volume atteso (rimandate a quando ci sono dati)

## Domain Model

### Entità

- **WebFetchConfiguration** (globale): configurazione globale del tool web fetch, una sola per piattaforma
  - `DefaultTimeout` (default 30s): timeout per ogni fetch
  - `MaxUrlsPerInvocation` (default 5): massimo numero di URL fetchabili in una singola invocazione
  - `DefaultTokenBudgetPerPage` (default 4000): token budget per pagina
  - `DefaultTotalTokenBudget` (default 20000): token budget totale per invocazione
  - **Nota**: entità globale senza TenantId, configurata dall'admin del tenant default

- **WebFetchTool** (per tenant): configurazione del tool web fetch, simile a WebSearchTool
  - `ToolId`: riferimento al Tool (chiave primaria, relazione 1:1)
  - `TenantId`: il tenant proprietario
  - `TokenBudgetPerPage` (int, default 4000): token budget per pagina (override del default globale)
  - `TotalTokenBudget` (int, default 20000): token budget totale per invocazione (override del default globale)
  - `MaxUrlsPerInvocation` (int, default 5): massimo numero di URL per invocazione (override del default globale)
  - `Timeout` (int, default 30): timeout per ogni fetch (override del default globale)
  - `Scope` (ToolScopeKind): Global, Tenant o Assistant
  - `ScopeTenantIds` (lista Guid): tenant aggiuntivi che possono usare il tool (se Scope = Tenant)
  - `ScopeAssistantIds` (lista Guid): assistenti che possono usare il tool (se Scope = Assistant)

- **WebProperties** (globale): configurazione globale delle web properties, una sola per piattaforma
  - `UserAgent` (default "Quorum/1.0"): User-Agent usato per tutte le richieste web (web search e web fetch)
  - `FetchDelayMs` (default 1000): delay in millisecondi tra fetch consecutivi
  - `RespectRobotsTxt` (default true): se true, rispetta le regole robots.txt dei siti
  - `CacheEnabled` (default true): se true, abilita la cache in-memory per web search e web fetch
  - `CacheTtlMinutes` (default 10): TTL della cache in minuti
  - **Nota**: entità globale senza TenantId, configurata dall'admin del tenant default. Condivisa con web search (spec 005) per User-Agent, delay, cache.

- **WebFetchResult**: risultato di un fetch (effimero, non persistito, cachato in-memory se CacheEnabled)
  - `Url`: URL fetchata
  - `Title`: titolo della pagina (estratto dall'HTML o dal PDF)
  - `Content`: contenuto testuale estratto (troncato al token budget)
  - `ContentType`: tipo di contenuto (es. "text/html", "application/pdf")
  - `FetchedAt`: timestamp del fetch
  - `Truncated`: booleano, true se il contenuto è stato troncato per rispettare il token budget

### Relazioni

- `WebFetchConfiguration` è singola per piattaforma (1:1 con il sistema, senza TenantId)
- `WebFetchTool` ha una relazione 1:1 con `Tool` (ToolId come chiave primaria), simile a WebSearchTool
- `WebProperties` è singola per piattaforma (1:1 con il sistema, senza TenantId). Condivisa con web search per User-Agent, delay, cache.
- `WebFetchResult` è effimero: vive solo nel contesto della conversazione corrente (o nella cache per il TTL configurato)

### Lifecycle

- `WebFetchConfiguration`: creata dall'admin del tenant default, modificabile. Non eliminabile (è una configurazione di piattaforma).
- `WebFetchTool`: creato/modificato/eliminato da chi ha permessi di modifica sul tenant proprietario. Segue il pattern di WebSearchTool per lo scope.
- `WebProperties`: creata dall'admin del tenant default, modificabile. Non eliminabile (è una configurazione di piattaforma).
- `WebFetchResult`: creato dal tool durante l'esecuzione, garbage-collected alla fine della conversazione (o prima, se non serve più). Cachato in-memory per il TTL configurato se CacheEnabled.

## API Contract

### Configurazione globale web fetch (admin del tenant default)

| Verbo | Route | Request | Response | Errori |
|-------|-------|---------|----------|--------|
| GET | `/api/settings/web-fetch` | — | `WebFetchConfigurationResponse` | 401, 403 |
| PUT | `/api/settings/web-fetch` | `UpdateWebFetchConfigurationRequest` | `WebFetchConfigurationResponse` | 400, 401, 403 |

**WebFetchConfigurationResponse:**
```json
{
  "defaultTimeout": 30,
  "maxUrlsPerInvocation": 5,
  "defaultTokenBudgetPerPage": 4000,
  "defaultTotalTokenBudget": 20000
}
```

**UpdateWebFetchConfigurationRequest:**
```json
{
  "defaultTimeout": 30,
  "maxUrlsPerInvocation": 5,
  "defaultTokenBudgetPerPage": 4000,
  "defaultTotalTokenBudget": 20000
}
```

### Web properties globali (admin del tenant default)

| Verbo | Route | Request | Response | Errori |
|-------|-------|---------|----------|--------|
| GET | `/api/settings/web-properties` | — | `WebPropertiesResponse` | 401, 403 |
| PUT | `/api/settings/web-properties` | `UpdateWebPropertiesRequest` | `WebPropertiesResponse` | 400, 401, 403 |

**WebPropertiesResponse:**
```json
{
  "userAgent": "Quorum/1.0",
  "fetchDelayMs": 1000,
  "respectRobotsTxt": true,
  "cacheEnabled": true,
  "cacheTtlMinutes": 10
}
```

**UpdateWebPropertiesRequest:**
```json
{
  "userAgent": "Quorum/1.0",
  "fetchDelayMs": 1000,
  "respectRobotsTxt": true,
  "cacheEnabled": true,
  "cacheTtlMinutes": 10
}
```

### Configurazione tool web fetch (chi può modificare il tenant)

La configurazione del tool web fetch segue il pattern di WebSearchTool: endpoint dedicati per CRUD, non parte della configurazione dell'agente.

| Verbo | Route | Request | Response | Errori |
|-------|-------|---------|----------|--------|
| GET | `/api/tools/web-fetch` | — | `List<WebFetchToolResponse>` | 401, 403 |
| POST | `/api/tools/web-fetch` | `CreateWebFetchToolRequest` | `WebFetchToolCreatedResponse` | 400, 401, 403, 409 |
| GET | `/api/tools/web-fetch/{id}` | — | `WebFetchToolResponse` | 401, 403, 404 |
| PUT | `/api/tools/web-fetch/{id}` | `UpdateWebFetchToolRequest` | `WebFetchToolResponse` | 400, 401, 403, 404 |
| DELETE | `/api/tools/web-fetch/{id}` | — | 204 | 401, 403, 404 |

**WebFetchToolResponse:**
```json
{
  "id": "guid",
  "name": "web_fetch",
  "description": "Fetch and read the full content of web pages",
  "tokenBudgetPerPage": 4000,
  "totalTokenBudget": 20000,
  "maxUrlsPerInvocation": 5,
  "timeout": 30,
  "scope": {
    "kind": "Tenant",
    "tenantIds": [],
    "assistantIds": []
  },
  "state": "Enabled",
  "policy": "Auto",
  "shared": false
}
```

**CreateWebFetchToolRequest / UpdateWebFetchToolRequest:**
```json
{
  "name": "web_fetch",
  "description": "Fetch and read the full content of web pages",
  "tokenBudgetPerPage": 4000,
  "totalTokenBudget": 20000,
  "maxUrlsPerInvocation": 5,
  "timeout": 30,
  "scope": {
    "kind": "Tenant",
    "tenantIds": [],
    "assistantIds": []
  },
  "policy": "Auto"
}
```

### Tool invocation (interno, non esposto via API)

Il tool di web fetch è invocato dal modello tramite function calling. Il contratto è definito dall'interfaccia `AIFunction` di MEAI:

**Input:**
```json
{
  "urls": [
    "https://example.com/article1",
    "https://example.com/article2"
  ]
}
```

**Output (successo):**
```json
{
  "results": [
    {
      "url": "https://example.com/article1",
      "title": "Article Title",
      "content": "Extracted text content...",
      "contentType": "text/html",
      "truncated": false
    },
    {
      "url": "https://example.com/article2",
      "title": "PDF Document",
      "content": "Extracted PDF text...",
      "contentType": "application/pdf",
      "truncated": true
    }
  ]
}
```

**Output (errore):**
```json
{
  "results": [],
  "errors": [
    {
      "url": "https://example.com/article1",
      "error": "Fetch failed: timeout after 30 seconds"
    }
  ]
}
```

**Output (parziale — alcuni URL fetchati, altri falliti):**
```json
{
  "results": [
    {
      "url": "https://example.com/article1",
      "title": "Article Title",
      "content": "Extracted text content...",
      "contentType": "text/html",
      "truncated": false
    }
  ],
  "errors": [
    {
      "url": "https://example.com/article2",
      "error": "Fetch failed: connection refused"
    }
  ]
}
```

## Authorization

| Azione | Chi |
|--------|-----|
| Configurare le web properties globali (User-Agent, delay, cache) | Admin del tenant default (admin di sistema) |
| Configurare i default globali web fetch (timeout, token budget) | Admin del tenant default |
| Creare/modificare/eliminare tool web fetch | Admin del tenant proprietario |
| Attivare/disattivare il tool web fetch su un assistente | Chiunque possa modificare l'assistente (tramite Settings > Tools) |
| Invocare il tool durante una conversazione | Il modello (tramite function calling), se il tool è attivato sull'assistente e rientra nello scope |

## Edge Cases & Failure Modes

1. **URL non raggiungibile entro 30 secondi**: il tool restituisce un errore per quell'URL ("Fetch failed: timeout after 30 seconds"). Se ci sono altri URL nella richiesta, vengono fetchati comunque (risultato parziale).

2. **URL restituisce un errore HTTP (4xx, 5xx)**: il tool restituisce un errore per quell'URL ("Fetch failed: HTTP 404"). Se ci sono altri URL nella richiesta, vengono fetchati comunque.

3. **Contenuto troppo grande per il token budget**: il tool tronca il contenuto al token budget configurato e imposta `truncated: true` nel risultato. Il modello riceve un messaggio ("Content truncated to 4000 tokens").

4. **Token budget totale superato**: se la somma dei contenuti delle pagine fetchate supera il token budget totale (20000 token di default), il tool tronca le pagine in ordine fino a rispettare il budget. Le pagine successive vengono troncate più aggressivamente o omesse.

5. **URL non è HTML o PDF**: il tool restituisce un errore per quell'URL ("Unsupported content type: application/zip"). Altri formati non sono supportati inizialmente.

6. **Robots.txt blocca il fetch**: se `RespectRobotsTxt` è true e il robots.txt del sito blocca l'User-Agent Quorum, il tool restituisce un errore per quell'URL ("Fetch blocked by robots.txt").

7. **Cache hit**: se l'URL è stata fetchata di recente (entro il TTL configurato) e `CacheEnabled` è true, il tool restituisce il risultato cachato invece di fare una nuova richiesta. Il campo `fetchedAt` indica quando il contenuto è stato originariamente fetchato.

8. **Max URL superato**: se il modello fornisce più di `MaxUrlsPerInvocation` URL (default 5), il tool restituisce un errore ("Max URLs exceeded: provided 10, maximum is 5"). Il modello non dovrebbe invocare il tool con troppi URL.

9. **Concorrenza**: più conversazioni possono invocare il tool contemporaneamente. Il delay tra fetch (configurabile) si applica per-conversazione, non globalmente. Se il load è troppo alto, i siti potrebbero rispondere con 429 (Too Many Requests). In questo caso, il tool tratta il 429 come un errore temporaneo e applica retry con backoff.

10. **PDF corrotto o protetto**: se il PDF è corrotto o protetto da password, il tool restituisce un errore per quell'URL ("PDF extraction failed: file is corrupted or password-protected").

11. **HTML senza contenuto testuale**: se l'HTML non contiene contenuto testuale (es. solo immagini, solo JavaScript), il tool restituisce un risultato con contenuto vuoto e un messaggio ("No text content extracted").

## Non-Functional Requirements

- **Volume atteso**: deferred decision (nessun dato disponibile al momento)
- **Latency budget**: nessun budget specifico. Il fetch di una pagina tipicamente richiede 1-5 secondi. Con 5 URL e delay 1s, il fetch totale richiede 5-30 secondi, che è accettabile per un tool di approfondimento.
- **Ottimizzazioni**: cache in-memory per ridurre il load sui siti e migliorare la latenza per URL già fetchate. Altre ottimizzazioni (cache distribuita, prefetching) rimandate a quando ci sono dati sul volume.
- **Scalabilità**: la cache in-memory è per-istanza. Se ci sono più istanze di Quorum, ogni istanza ha la propria cache. Per condividere la cache tra istanze, servirebbe una cache distribuita (Redis), rimandata a quando ci sono dati sul volume.

## Integrations

- **LlamaIndex.WebReader** (o libreria simile): per l'estrazione del contenuto testuale da HTML. Da valutare se usare LlamaIndex.WebReader, HtmlAgilityPack + Readability, o AngleSharp + Readability.
- **PdfPig**: già presente nel modulo Conversations per l'estrazione di testo dai PDF degli allegati. Riusato per il web fetch.
- **IMemoryCache**: pattern esistente per il caching in-memory (già usato in LookupToolResolver e RagToolResolver).
- **Web Properties**: configurazione globale condivisa con web search (spec 005) per User-Agent, delay, cache.
- **Audit log**: automatico tramite `ToolAuditEntry` (pattern esistente per tutti i tool). Registra chi ha invocato il tool, quando, con quali argomenti (redatti), outcome e durata. Nessun lavoro aggiuntivo richiesto.
- **Eventi**: nessun evento pubblicato per l'MVP. Da valutare in futuro se altri moduli devono reagire agli eventi di web fetch.

## Acceptance Criteria

**AC-1**: Given l'admin del tenant default ha configurato le web properties globali (User-Agent, delay, cache), When un admin crea un tool web fetch con parametri specifici (token budget, timeout, max URL, scope), Then il tool è disponibile per il tenant con i parametri specificati.

**AC-2**: Given un assistente con il tool web fetch attivato (tramite Settings > Tools), When il modello decide di invocare il tool con una lista di URL (max 5), Then il tool fetcha il contenuto di ogni URL e restituisce i risultati al modello (array di risultati con url, title, content, contentType, truncated).

**AC-3**: Given un'URL HTML, When il tool fetcha l'URL, Then il tool estrae il contenuto testuale principale (rimuovendo navigation, ads, footer) e lo restituisce al modello.

**AC-4**: Given un'URL PDF, When il tool fetcha l'URL, Then il tool estrae il testo dal PDF (usando PdfPig) e lo restituisce al modello.

**AC-5**: Given un'URL con contenuto non supportato (es. ZIP, Word), When il tool fetcha l'URL, Then il tool restituisce un errore per quell'URL ("Unsupported content type").

**AC-6**: Given il contenuto di una pagina supera il token budget per pagina (4000 token di default), When il tool fetcha l'URL, Then il tool tronca il contenuto al token budget e imposta `truncated: true` nel risultato.

**AC-7**: Given la somma dei contenuti delle pagine fetchate supera il token budget totale (20000 token di default), When il tool fetcha più URL, Then il tool tronca le pagine in ordine fino a rispettare il budget totale.

**AC-8**: Given `CacheEnabled` è true e un'URL è stata fetchata di recente (entro il TTL configurato), When il tool fetcha l'URL, Then il tool restituisce il risultato cachato invece di fare una nuova richiesta.

**AC-9**: Given `RespectRobotsTxt` è true e il robots.txt del sito blocca l'User-Agent Quorum, When il tool fetcha l'URL, Then il tool restituisce un errore per quell'URL ("Fetch blocked by robots.txt").

**AC-10**: Given il delay tra fetch è configurato a 1000ms, When il tool fetcha più URL, Then il tool aspetta almeno 1000ms tra un fetch e l'altro.

**AC-11**: Given un'URL non è raggiungibile entro il timeout configurato (30s di default), When il tool fetcha l'URL, Then il tool restituisce un errore per quell'URL ("Fetch failed: timeout after 30 seconds").

**AC-12**: Given il modello usa i risultati della web fetch per generare una risposta, When la risposta viene inviata all'utente, Then le fonti possono essere citate (riutilizzando MessageSource con KnowledgeBase="web", Title, Url).

**AC-13**: Given l'admin del tenant default accede alla pagina Settings > Web Tools, When seleziona la tab "Web Fetch", Then può configurare i default globali web fetch (timeout, token budget, max URL).

**AC-14**: Given l'admin del tenant default accede alla pagina Settings > Web Tools, When seleziona la tab "Settings", Then può configurare le web properties globali (User-Agent, delay, cache, robots.txt).

**AC-15**: Given l'admin accede alla pagina "/tools/web-fetch", When ci sono tool web fetch configurati, Then vede la lista dei tool con nome, descrizione, scope, stato e può crearne, modificarne o eliminarne.

## Deferred Decisions

1. **Supporto per formati diversi da HTML e PDF**: rimandato a quando ci sono richieste specifiche. Trigger per revisit: se un tenant richiede il fetch di documenti Word, Excel, PowerPoint, implementare l'estrazione per quei formati.

2. **Cache distribuita (Redis)**: rimandata a quando ci sono dati sul volume e sulla condivisione tra istanze. Trigger per revisit: se ci sono più istanze di Quorum e la cache in-memory non è efficiente (troppi cache miss), valutare una cache distribuita.

3. **Rendering JavaScript (headless browser)**: rimandato a quando ci sono casi d'uso concreti. Trigger per revisit: se gli utenti riportano che molte pagine non hanno contenuto testuale nell'HTML statico (es. siti che richiedono JavaScript per il rendering), valutare un headless browser (es. Playwright).

4. **Volume atteso e ottimizzazioni correlate**: rimandato a quando ci sono dati sul volume reale. Trigger per revisit: dopo 3 mesi di utilizzo, analizzare i log e valutare se servono ottimizzazioni (prefetching, caching più aggressivo, load balancing).

5. **Eventi pubblicati**: per l'MVP, nessun evento pubblicato (es. "web fetch performed", "web fetch failed"). Trigger for revisit: se altri moduli devono reagire agli eventi di web fetch (es. un modulo di analytics che conta i fetch), aggiungere gli eventi.

## Open Questions

(nessuna)
