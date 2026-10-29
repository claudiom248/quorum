# Spec 005: Web Search Tool (SearXNG)

**Status:** Approved (2026-10-29)
**Date:** 2026-10-29

> **Amendment 1 (2026-10-29, folded into spec 006)** — The platform's global web configuration (SearXNG instance URL and search timeout) is no longer stored in its own `web_search_configuration` table nor exposed as `/api/settings/web-search`. It lives in the single `web_configuration` row that spec 006 introduces, and is read/written through `/api/settings/web` as one of its fields (`searchUrl`, `searchTimeoutSeconds`). Every other aspect of this spec — the `WebSearchTool` binding, endpoints, invocation flow, sources, UI — is unchanged.

> **Amendment 2 (2026-10-29, folded into spec 006)** — The screens of AC-9 and AC-10 moved, the behaviour did not. The web search fields are one section of the single **Web** tab of Settings (spec 006, Emendamento 1), and the tool list lives on `/tools/web/search`, the search tab of the one **Web tools** page; `/tools/web-search` and `/tools/web-search/{id}` still arrive there. Nothing else in this spec changes.

## Problem

Gli agenti di Quorum non hanno accesso a informazioni aggiornate in tempo reale. Quando un utente pone domande su eventi recenti, fatti di attualità, o documentazione non presente nella knowledge base, l'agente può rispondere solo con la conoscenza del modello (che ha un cut-off temporale) o con i documenti indicizzati nel RAG.

Entrambi gli utenti finali (che ricevono risposte incomplete o obsolete) e gli admin (che non possono offrire agenti capaci di ricerca web) soffrono di questa limitazione. Non esiste un workaround: se la conoscenza del modello o il RAG non bastano, l'agente non può recuperare informazioni esterne.

## Scope

### In

- Tool di web search invocabile dagli agenti tramite function calling (MEAI `FunctionInvokingChatClient`)
- Provider iniziale: SearXNG (meta-motore di ricerca open source self-hosted)
- Configurazione globale dell'istanza SearXNG a livello di piattaforma (admin del tenant default)
- Configurazione del tool web search per agente: abilitazione/disabilitazione + parametri (top N risultati, motori di ricerca, lingua, safe search, max invocazioni per conversazione) + scope (Global, Tenant, Assistant)
- UI per la configurazione globale SearXNG (tab "Web search" in Settings)
- UI per la gestione dei tool web search (pagina dedicata simile a RagTools)
- Gestione errori: timeout (90s), retry con backoff, messaggi di errore al modello
- Citazione delle fonti nelle risposte (pattern esistente del RAG, riutilizzando MessageSource)
- Orchestrazione RAG vs web search veicolata dal prompt di sistema dell'agente

### Out

- Cache semantica dei risultati di ricerca (rimandata a spec successiva)
- Provider di ricerca alternativi a SearXNG (l'interfaccia sarà astratta, ma l'implementazione iniziale è solo SearXNG)
- Ottimizzazioni di performance basate su volume atteso (rimandate a quando ci sono dati)

## Domain Model

### Entità

- **WebSearchConfiguration** (globale): configurazione globale dell'istanza SearXNG, una sola per piattaforma
  - `Url` (obbligatorio): URL dell'istanza SearXNG
  - `DefaultTimeout` (default 90s): timeout per le richieste
  - **Nota**: entità globale senza TenantId, configurata dall'admin del tenant default
  
- **WebSearchTool** (per tenant): configurazione del tool web search, simile a RagTool
  - `ToolId`: riferimento al Tool (chiave primaria, relazione 1:1)
  - `TenantId`: il tenant proprietario
  - `TopN` (int, default 5): numero massimo di risultati da restituire
  - `SearchEngines` (lista stringhe, opzionale): motori di ricerca abilitati (es. ["google", "bing", "duckduckgo"])
  - `Language` (stringa, opzionale): lingua preferita per i risultati (es. "it", "en")
  - `SafeSearch` (int, default 1): livello di safe search (0=off, 1=moderate, 2=strict)
  - `TimeRange` (stringa, opzionale): filtro temporale (es. "day", "week", "month", "year")
  - `MaxInvocationsPerConversation` (int, default 5): massimo numero di invocazioni del tool per conversazione
  - `Scope` (ToolScopeKind): Global, Tenant o Assistant
  - `ScopeTenantIds` (lista Guid): tenant aggiuntivi che possono usare il tool (se Scope = Tenant)
  - `ScopeAssistantIds` (lista Guid): assistenti che possono usare il tool (se Scope = Assistant)

- **WebSearchResult**: risultato di una ricerca (effimero, non persistito)
  - `Title`: titolo del risultato
  - `Url`: URL della pagina
  - `Snippet`: estratto del contenuto
  - `Engine`: motore di ricerca che ha restituito il risultato
  - `Score`: punteggio di rilevanza (se disponibile)

### Relazioni

- `WebSearchConfiguration` è singola per piattaforma (1:1 con il sistema, senza TenantId)
- `WebSearchTool` ha una relazione 1:1 con `Tool` (ToolId come chiave primaria), simile a RagTool
- `WebSearchResult` è effimero: vive solo nel contesto della conversazione corrente, non viene persistito

### Lifecycle

- `WebSearchConfiguration`: creata dall'admin del tenant default, modificabile. Non eliminabile (è una configurazione di piattaforma).
- `WebSearchTool`: creato/modificato/eliminato da chi ha permessi di modifica sul tenant proprietario. Segue il pattern di RagTool per lo scope.
- `WebSearchResult`: creato dal tool durante l'esecuzione, garbage-collected alla fine della conversazione (o prima, se non serve più)

## API Contract

### Configurazione globale SearXNG (admin del tenant default)

| Verbo | Route | Request | Response | Errori |
|-------|-------|---------|----------|--------|
| GET | `/api/settings/web-search` | — | `WebSearchConfigurationResponse` | 401, 403 |
| PUT | `/api/settings/web-search` | `UpdateWebSearchConfigurationRequest` | `WebSearchConfigurationResponse` | 400, 401, 403 |

**WebSearchConfigurationResponse:**
```json
{
  "url": "https://searxng.example.com",
  "defaultTimeout": 90
}
```

**UpdateWebSearchConfigurationRequest:**
```json
{
  "url": "https://searxng.example.com",
  "defaultTimeout": 90
}
```

### Configurazione tool web search (chi può modificare il tenant)

La configurazione del tool web search segue il pattern di RagTool: endpoint dedicati per CRUD, non parte della configurazione dell'agente.

| Verbo | Route | Request | Response | Errori |
|-------|-------|---------|----------|--------|
| GET | `/api/tools/web-search` | — | `List<WebSearchToolResponse>` | 401, 403 |
| POST | `/api/tools/web-search` | `CreateWebSearchToolRequest` | `WebSearchToolCreatedResponse` | 400, 401, 403, 409 |
| GET | `/api/tools/web-search/{id}` | — | `WebSearchToolResponse` | 401, 403, 404 |
| PUT | `/api/tools/web-search/{id}` | `UpdateWebSearchToolRequest` | `WebSearchToolResponse` | 400, 401, 403, 404 |
| DELETE | `/api/tools/web-search/{id}` | — | 204 | 401, 403, 404 |

**WebSearchToolResponse:**
```json
{
  "id": "guid",
  "name": "web_search",
  "description": "Search the web for current information",
  "topN": 10,
  "searchEngines": ["google", "bing"],
  "language": "it",
  "safeSearch": 1,
  "timeRange": "week",
  "maxInvocationsPerConversation": 3,
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

**CreateWebSearchToolRequest / UpdateWebSearchToolRequest:**
```json
{
  "name": "web_search",
  "description": "Search the web for current information",
  "topN": 10,
  "searchEngines": ["google", "bing"],
  "language": "it",
  "safeSearch": 1,
  "timeRange": "week",
  "maxInvocationsPerConversation": 3,
  "scope": {
    "kind": "Tenant",
    "tenantIds": [],
    "assistantIds": []
  },
  "policy": "Auto"
}
```

### Tool invocation (interno, non esposto via API)

Il tool di web search è invocato dal modello tramite function calling. Il contratto è definito dall'interfaccia `AIFunction` di MEAI:

**Input:**
```json
{
  "query": "ultime notizie su intelligenza artificiale"
}
```

**Output (successo):**
```json
{
  "results": [
    {
      "title": "AI News",
      "url": "https://example.com/ai-news",
      "snippet": "Le ultime novità sull'intelligenza artificiale...",
      "engine": "google",
      "score": 0.95
    }
  ]
}
```

**Output (errore):**
```json
{
  "error": "Web search unavailable: SearXNG did not respond within 90 seconds"
}
```

**Output (nessun risultato):**
```json
{
  "results": [],
  "message": "No results found for query 'xyz'"
}
```

## Authorization

| Azione | Chi |
|--------|-----|
| Configurare l'istanza SearXNG a livello globale | Admin del tenant default (admin di sistema) |
| Creare/modificare/eliminare tool web search | Admin del tenant proprietario |
| Attivare/disattivare il tool web search su un assistente | Chiunque possa modificare l'assistente (tramite Settings > Tools) |
| Invocare il tool durante una conversazione | Il modello (tramite function calling), se il tool è attivato sull'assistente e rientra nello scope |

## Edge Cases & Failure Modes

1. **SearXNG non risponde entro 90 secondi**: il tool restituisce un messaggio di errore al modello ("Web search unavailable: SearXNG did not respond within 90 seconds"). Il modello può decidere di riprovare o di rispondere senza i risultati della ricerca.

2. **SearXNG restituisce zero risultati**: il tool restituisce un array vuoto con un messaggio ("No results found for query 'xyz'"). Il modello può decidere di riformulare la query o di rispondere senza i risultati della ricerca.

3. **Timeout o errori di rete**: il tool effettua retry con backoff esponenziale (max 3 tentativi). Se tutti i tentativi falliscono, restituisce un messaggio di errore al modello.

4. **Max invocazioni raggiunto**: se il modello ha già invocato il tool N volte (dove N è `MaxInvocationsPerConversation`), il tool restituisce un errore ("Max invocations reached: N"). Il modello non può invocare il tool nuovamente nella stessa conversazione.

5. **Configurazione SearXNG mancante**: se l'admin del tenant global non ha configurato l'istanza SearXNG, il tool non può essere abilitato sugli agenti. L'API di update dell'agente dovrebbe restituire un errore se si tenta di abilitare il tool senza una configurazione valida.

6. **Concorrenza**: più conversazioni possono invocare il tool contemporaneamente. SearXNG gestisce la concorrenza, ma se il load è troppo alto, potrebbe rispondere con 429 (Too Many Requests). In questo caso, il tool tratta il 429 come un errore temporaneo e applica retry con backoff.

7. **Query troppo lunghe**: SearXNG ha un limite sulla lunghezza della query (tipicamente 1000 caratteri). Se la query supera il limite, il tool la tronca o restituisce un errore. Decisione: troncare silenziosamente a 1000 caratteri.

## Non-Functional Requirements

- **Volume atteso**: deferred decision (nessun dato disponibile al momento)
- **Latency budget**: nessun budget specifico. SearXNG tipicamente risponde in 1-5 secondi, che è accettabile per un tool di ricerca.
- **Ottimizzazioni**: nessuna ottimizzazione specifica (caching, rate limiting interno) per l'MVP. Da valutare in futuro quando ci sono dati sul volume.
- **Scalabilità**: l'istanza SearXNG è singola per piattaforma. Se il load aumenta, l'admin può scalare SearXNG orizzontalmente (load balancer) o verticalmente (più risorse).

## Integrations

- **SearXNG**: unica integrazione esterna per questa feature. Il tool chiama l'API di SearXNG (tipicamente `/search?q=...&format=json`) per eseguire le ricerche.
- **Eventi**: nessun evento pubblicato per l'MVP. Da valutare in futuro se serve tracciamento o audit.
- **Side effects**: audit log base (chi ha usato la web search, quando, con quale query) può essere aggiunto in futuro, ma non è richiesto per l'MVP.

## Acceptance Criteria

**AC-1**: Given l'admin del tenant default ha configurato l'istanza SearXNG (URL), When un admin crea un tool web search con parametri specifici (top N, motori, lingua, safe search, max invocazioni, scope), Then il tool è disponibile per il tenant con i parametri specificati.

**AC-2**: Given un assistente con il tool web search attivato (tramite Settings > Tools), When il modello decide di invocare il tool con una query, Then il tool chiama SearXNG e restituisce i risultati al modello (array di risultati con title, url, snippet, engine, score).

**AC-3**: Given SearXNG non risponde entro 90 secondi, When il tool tenta di eseguire una ricerca, Then il tool restituisce un messaggio di errore al modello ("Web search unavailable: SearXNG did not respond within 90 seconds").

**AC-4**: Given SearXNG restituisce zero risultati per una query, When il tool esegue la ricerca, Then il tool restituisce un array vuoto con un messaggio ("No results found for query 'xyz'") al modello.

**AC-5**: Given il modello ha invocato il tool N volte (dove N è `MaxInvocationsPerConversation`), When tenta di invocarlo un'altra volta, Then il tool restituisce un errore ("Max invocations reached: N") al modello.

**AC-6**: Given il modello usa i risultati della web search per generare una risposta, When la risposta viene inviata all'utente, Then le fonti possono essere citate (riutilizzando MessageSource con KnowledgeBase="web", Title, Url).

**AC-7**: Given l'admin del tenant default non ha configurato l'istanza SearXNG, When un admin tenta di creare un tool web search, Then l'API restituisce un errore 400 ("Web search configuration missing").

**AC-8**: Given un assistente con il tool web search attivato e un prompt di sistema che specifica la priorità RAG vs web search, Then il modello rispetta la priorità indicata nel prompt (es. "usa prima il RAG, poi la web search se necessario").

**AC-9**: Given l'admin del tenant default accede alla pagina Settings, When seleziona la tab "Web search", Then può configurare l'URL dell'istanza SearXNG e il timeout default.

**AC-10**: Given l'admin accede alla pagina "/tools/web-search", When ci sono tool web search configurati, Then vede la lista dei tool con nome, descrizione, scope, stato e può crearne, modificarne o eliminarne.

## Deferred Decisions

1. **Cache semantica dei risultati di ricerca**: rimandata a una spec successiva. Per ora, i risultati sono effimeri e non vengono cachati. Trigger per revisit: se il volume di ricerche cresce e ci sono molte query duplicate, valutare una cache semantica (es. embedding delle query e caching dei risultati per query simili).

2. **Volume atteso e ottimizzazioni correlate**: rimandato a quando ci sono dati sul volume reale. Trigger per revisit: dopo 3 mesi di utilizzo, analizzare i log e valutare se servono ottimizzazioni (caching, rate limiting interno, load balancing).

3. **Provider di ricerca alternativi**: l'interfaccia sarà astratta (per il principio di Quorum "provider astratti e sostituibili"), ma l'implementazione iniziale è solo SearXNG. Trigger per revisit: se un tenant richiede un provider diverso (es. Google Custom Search, Bing Search API), implementare un adapter per quel provider.

4. **Audit log dettagliato**: per l'MVP, nessun audit log specifico per la web search. Trigger per revisit: se serve tracciare chi usa la web search e con quali query (es. per compliance o billing), aggiungere un audit log.

5. **Eventi pubblicati**: per l'MVP, nessun evento pubblicato (es. "web search performed", "web search failed"). Trigger for revisit: se altri moduli devono reagire agli eventi di web search (es. un modulo di analytics che conta le ricerche), aggiungere gli eventi.

## Open Questions

(nessuna)
