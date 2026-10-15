# Spec 001: Primo avvio e prima risposta

**Status:** Approved (2026-10-15)
**Date:** 2026-10-15

## Problem
Una piccola azienda (o il fondatore come tester) deve poter passare da `docker compose up` a una prima risposta del proprio modello locale in pochi minuti, senza competenze DevOps. E il "primo quarto d'ora" del prodotto e il suo differenziatore principale.

## Scope
### In
- Avvio con `docker compose up` (Postgres + app); migrazioni applicate all'avvio.
- Setup mode con setup token stampato nei log.
- Wizard di primo avvio: creazione dell'amministratore locale; configurazione dell'endpoint del modello compatibile OpenAI (URL, nome del modello, API key opzionale, budget di contesto) con test di connessione ed elenco dei modelli disponibili; system prompt di default mostrato e modificabile.
- Tenant di default creato automaticamente dal setup, senza domande; non visibile in UI finche il multi-tenant non viene attivato (attivazione fuori scope). Il `TenantId` e presente in tutti i dati fin dall'inizio.
- Chiusura del setup mode al termine del wizard.
- Login e logout dell'amministratore.
- Chat con risposta in streaming; conversazione salvata.
- Conversazioni: creazione, invio messaggi e continuazione; elenco e riapertura; rinomina; eliminazione; interruzione e rigenerazione della risposta.
- Pagina impostazioni minima: stessi campi del wizard per modello e system prompt, con test di connessione.
- Backend (API) **e** UI minima per wizard, login, chat e impostazioni, in **Blazor WebAssembly** servita dall'Host, che usa esclusivamente l'API pubblica.

### Out
- Chiave di licenza (vedi Deferred Decisions).
- RAG e vector store, ricerca web, OIDC, preset multi-agente, editor a grafo.
- Utenti oltre l'amministratore; gestione utenti; recupero password.
- Attivazione del multi-tenant.
- Sola lettura alla scadenza della trial.
- API key per client esterni ed endpoint compatibile OpenAI esposto da Quorum.
- Riassunto automatico della cronologia; varianti multiple di una risposta.

## Domain Model
Tutte le entita applicative portano `TenantId`.

| Entita | Modulo | Campi principali | Ciclo di vita |
|---|---|---|---|
| **Tenant** | Tenancy | Id, Name ("Default"), CreatedAt | Creato dal setup; nessuna modifica in questa spec |
| **User** | Tenancy | Id, TenantId, Email, DisplayName, PasswordHash, Role (`Admin`), CreatedAt | Admin creato dal setup |
| **SetupState** | Tenancy | IsCompleted, CompletedAt | Singleton di installazione; passa a completato una sola volta, in modo atomico |
| **Assistant** | Agents | Id, TenantId, Name, SystemPrompt, ModelEndpointId | Assistente di default creato dal setup; system prompt modificabile da wizard e impostazioni |
| **ModelEndpoint** | Agents | Id, TenantId, BaseUrl, ModelName, ApiKey (cifrata), ContextTokenBudget (default 4096), UpdatedAt | Creato dal wizard; modificabile in impostazioni |
| **Conversation** | Conversations | Id, TenantId, UserId, AssistantId, Title, CreatedAt, UpdatedAt | Creata al primo messaggio; rinominabile; **hard delete** con i suoi messaggi |
| **Message** | Conversations | Id, ConversationId, Role (`User`/`Assistant`), Content, Status (`Completed`/`Interrupted`/`Failed`), CreatedAt | Messaggio utente sempre `Completed`; risposta parziale salvata con stato `Interrupted` o `Failed`; l'ultima risposta puo essere sostituita dalla rigenerazione |

- **Setup token**: non persistito. Generato a ogni avvio finche `SetupState.IsCompleted = false`, tenuto solo in memoria, stampato nei log, invalido appena il setup termina.
- **Titolo**: prime parole del primo messaggio utente, massimo 60 caratteri, troncato a parola intera.
- **Contesto inviato al modello**: system prompt + i messaggi piu recenti che stanno in `ContextTokenBudget`; i piu vecchi restano salvati ma non vengono inviati. Nessun riassunto.
- **API key del modello**: cifrata a riposo con la chiave master (fuori dal database); mai restituita dalle API (solo "impostata/non impostata").

## API Contract
Errori in formato ProblemDetails. Tutte le route sotto `/api`.

| Verbo | Route | Request | Response | Errori |
|---|---|---|---|---|
| GET | `/setup/status` | - | `{ isSetupMode }` | - |
| POST | `/setup/verify-token` | `{ token }` | 204 | 401 token errato; 404 setup completato |
| POST | `/setup/model/test` | token + `{ baseUrl, apiKey? }` | `{ reachable, models[] }` | 401; 404 setup completato; 422 endpoint non raggiungibile |
| POST | `/setup/complete` | token + `{ admin: { email, displayName, password }, model: { baseUrl, modelName, apiKey?, contextTokenBudget }, systemPrompt }` | 204 + cookie di sessione | 400 validazione; 401; 409 setup gia completato |
| POST | `/auth/login` | `{ email, password }` | 204 + cookie | 401 credenziali; 423 account bloccato |
| POST | `/auth/logout` | - | 204 | - |
| GET | `/auth/me` | - | `{ email, displayName, role }` | 401 |
| GET | `/settings/assistant` | - | `{ baseUrl, modelName, hasApiKey, contextTokenBudget, systemPrompt }` | 401 |
| PUT | `/settings/assistant` | `{ baseUrl, modelName, apiKey?, contextTokenBudget, systemPrompt }` | 204 | 400; 401 |
| POST | `/settings/assistant/test` | `{ baseUrl, apiKey? }` | `{ reachable, models[] }` | 401; 422 |
| GET | `/conversations` | - | `[{ id, title, updatedAt }]` (ordinate per `updatedAt` desc) | 401 |
| POST | `/conversations` | - | `{ id }` | 401 |
| GET | `/conversations/{id}` | - | `{ id, title, messages[] }` | 401; 404 |
| PATCH | `/conversations/{id}` | `{ title }` | 204 | 400; 401; 404 |
| DELETE | `/conversations/{id}` | - | 204 | 401; 404 |
| POST | `/conversations/{id}/messages` | `{ content }` | stream SSE di token, evento finale con stato | 400; 401; 404; 409 risposta gia in corso |
| POST | `/conversations/{id}/regenerate` | - | stream SSE che sostituisce l'ultima risposta | 401; 404; 409 |

- Il "token" del setup viaggia in un header dedicato (es. `X-Setup-Token`).
- L'interruzione da parte dell'utente avviene chiudendo lo stream (annullamento della richiesta); il server salva la risposta parziale come `Interrupted`.
- Le conversazioni di un altro utente o tenant rispondono 404 (non 403), per non rivelarne l'esistenza.

## Authorization
- **UI web**: cookie di autenticazione HttpOnly, Secure, SameSite (stessa origine dell'API); nessun token accessibile da JavaScript. Protezione antiforgery sulle chiamate che modificano dati.
- **Password admin**: minimo 12 caratteri, nessuna regola di complessita forzata; blocco temporaneo dopo 5 tentativi falliti.
- **Ruoli**: in questa spec esiste solo `Admin`, che accede a chat e impostazioni. Ogni conversazione e visibile e modificabile solo dal suo utente e nel suo tenant.
- **API di setup**: accessibili solo in setup mode e solo con un setup token valido; dopo il setup non sono piu disponibili.

## Edge Cases & Failure Modes
- **Streaming interrotto** (errore del modello, timeout, stop dell'utente): si salva il testo ricevuto con stato `Interrupted` (stop/annullamento) o `Failed` (errore/timeout); la UI lo mostra con la possibilita di rigenerare.
- **Rigenerazione**: sostituisce l'ultima risposta dell'assistente (anche interrotta o fallita); nessuna variante conservata.
- **Messaggio inviato mentre una risposta e in corso nella stessa conversazione**: rifiutato con 409.
- **Modello non raggiungibile durante la chat**: messaggio utente salvato, risposta `Failed` con errore leggibile in UI.
- **Modello non raggiungibile durante il setup**: il test di connessione fallisce con un messaggio chiaro; il setup si puo comunque completare con un avviso (il modello puo essere ancora in download o spento) e l'admin corregge dalle impostazioni.
- **Setup concorrente**: il completamento e atomico; vince il primo, il secondo riceve 409.
- **Riavvio durante il setup**: nuovo token nei log; il vecchio non vale piu; nessun dato parziale persistito prima di `/setup/complete`.
- **Eliminazione**: conferma esplicita in UI; cancellazione definitiva di conversazione e messaggi. Eliminazione durante una risposta in corso: la risposta viene annullata.
- **Messaggio piu lungo del budget di contesto** (insieme al system prompt): rifiutato con 400 e indicazione della lunghezza massima.
- **Stima dei token**: caratteri / 4, con margine di sicurezza del 20% sul budget; sostituibile in futuro senza cambiare l'API.
- **Database non raggiungibile all'avvio**: l'app attende (il compose usa l'healthcheck di Postgres) e poi termina con un errore chiaro nei log se non lo raggiunge.

## Non-Functional Requirements
- Nessun obiettivo di prestazione fissato in questa spec: si misurano tempo di primo avvio, overhead prima del primo token e utenti concorrenti, e si fissano i target in una spec successiva.
- Nessuna telemetria verso l'esterno.
- Log strutturati; il setup token e l'unico segreto stampato nei log, mai password o API key.
- Streaming via **Server-Sent Events**.

## Integrations
- **Endpoint del modello compatibile OpenAI**: `GET /v1/models` per test ed elenco dei modelli; `POST /v1/chat/completions` con `stream: true` per la chat. Verificato almeno con Ollama.
- **PostgreSQL**: database di piattaforma.
- Nessun evento pubblicato verso l'esterno; eventuali integration event tra moduli restano interni.

## Acceptance Criteria
**Setup**
- **AC-1** Given un'installazione nuova, When l'app si avvia, Then applica le migrazioni, entra in setup mode e stampa un setup token nei log.
- **AC-2** Given il setup mode, When si chiama un'API di setup senza token o con token errato, Then risponde 401.
- **AC-3** Given un token valido e un endpoint Ollama raggiungibile, When si testa la connessione, Then risponde `reachable: true` con l'elenco dei modelli.
- **AC-4** Given un endpoint non raggiungibile, When si testa la connessione, Then risponde 422 con un messaggio comprensibile.
- **AC-4b** Given un endpoint non raggiungibile, When si completa il setup con dati validi, Then il setup riesce e la UI mostra un avviso che invita a verificare il modello dalle impostazioni.
- **AC-5** Given dati validi, When si completa il setup, Then vengono creati tenant di default, admin, assistente con system prompt e configurazione del modello (API key cifrata), il setup mode termina e l'admin risulta autenticato.
- **AC-6** Given il setup completato, When si chiama qualsiasi API di setup, Then risponde 404 (409 per `/setup/complete`).
- **AC-7** Given due richieste di completamento concorrenti, When arrivano insieme, Then una sola ha successo e l'altra riceve 409.
- **AC-8** Given una password admin di meno di 12 caratteri, When si completa il setup, Then risponde 400.
- **AC-9** Given l'app riavviata durante il setup, When riparte, Then stampa un nuovo token e il precedente non e piu valido.

**Autenticazione**
- **AC-10** Given l'admin, When fa login con credenziali corrette, Then riceve un cookie HttpOnly e `/auth/me` restituisce i suoi dati.
- **AC-11** Given 5 tentativi di login falliti, When si tenta di nuovo, Then risponde 423 anche con la password corretta, fino allo scadere del blocco.
- **AC-12** Given un utente non autenticato, When chiama API di chat o impostazioni, Then risponde 401.

**Chat**
- **AC-13** Given l'admin autenticato, When invia il primo messaggio in una nuova conversazione, Then riceve la risposta in streaming SSE e messaggio e risposta vengono salvati come `Completed`.
- **AC-14** Given il primo messaggio, When la conversazione viene creata, Then il titolo sono le prime parole del messaggio, massimo 60 caratteri, troncate a parola intera.
- **AC-15** Given una conversazione lunga, When si invia un messaggio, Then al modello arrivano il system prompt e solo i messaggi piu recenti che stanno nel budget di contesto.
- **AC-15b** Given un messaggio che da solo (con il system prompt) supera il budget di contesto stimato, When viene inviato, Then risponde 400 indicando la lunghezza massima e nulla viene salvato.
- **AC-16** Given una risposta in corso, When l'utente la interrompe, Then il testo ricevuto e salvato con stato `Interrupted`.
- **AC-17** Given il modello che va in errore durante lo streaming, When la risposta si interrompe, Then il testo ricevuto e salvato con stato `Failed` e la UI mostra l'errore.
- **AC-18** Given un'ultima risposta qualsiasi, When si chiede la rigenerazione, Then la nuova risposta sostituisce la precedente.
- **AC-19** Given una risposta in corso, When si invia un altro messaggio nella stessa conversazione, Then risponde 409.
- **AC-20** Given piu conversazioni, When si chiede l'elenco, Then sono ordinate per ultimo aggiornamento, e si possono riaprire e continuare.
- **AC-21** Given una conversazione, When viene rinominata, Then il nuovo titolo e salvato (validazione: non vuoto, max 60 caratteri).
- **AC-22** Given una conversazione, When viene eliminata, Then conversazione e messaggi non esistono piu nel database.
- **AC-23** Given una conversazione di un altro utente o tenant, When la si richiede, Then risponde 404.

**Impostazioni**
- **AC-24** Given l'admin, When modifica endpoint, modello, budget o system prompt, Then i messaggi successivi usano la nuova configurazione.
- **AC-25** Given un'API key impostata, When si leggono le impostazioni, Then la chiave non viene restituita (solo `hasApiKey: true`).

**Installazione**
- **AC-26** Given le immagini gia scaricate, When si esegue `docker compose up`, Then Postgres e l'app si avviano e il wizard e raggiungibile dal browser.

## Deferred Decisions
| Decisione | Fallback scelto ora | Quando rivederla |
|---|---|---|
| Verifica della chiave di licenza | Nessun passo di licenza; senza chiave l'app e Community implicita (regola di prodotto in `product.md`); inserimento chiave e limiti fuori scope | Spec dedicata al Licensing |
| Obiettivi di prestazione | Nessun target; solo misura | Dopo le prime misure sull'hardware di riferimento |

## Open Questions
_Nessuna._
