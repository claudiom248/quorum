# Quorum - Definizione di prodotto

Stato: bozza in evoluzione. Ultimo aggiornamento: 2026-10-15.

## Cliente target
- **Primo cliente**: piccola azienda con un server abbastanza potente da ospitare un LLM locale, senza team DevOps dedicato.
- **Secondo profilo**: il fondatore come tester (dogfooding dell'installazione).
- **Utenti finali**: potenzialmente tutti i dipendenti che lavorano al PC; il numero non e noto e determina il prezzo "per utente".

## Promessa (bozza)
> Quorum e l'assistente AI che una piccola azienda installa in un'ora sul proprio server, usa con il proprio modello locale e adatta ai propri processi, senza che i dati escano.

## Casi d'uso dell'MVP
- Rispondere su documenti caricati dall'azienda (RAG). Formati supportati nell'MVP: **PDF, DOCX, TXT, Markdown**.
- Riassumere documenti.
- Rispondere a domande generiche, con ricerca web quando serve (disattivabile e esplicita).

## Implicazioni del modello locale
- Endpoint compatibile OpenAI (Ollama, vLLM, llama.cpp, LM Studio) nei preset; i provider cloud sono opzionali.
- Risorse limitate: topologie multi-agente anche in modalita sequenziale, con limiti di concorrenza configurabili.
- Preset testati su modelli piccoli (7-14B), non solo su modelli grandi.
- Nessuna telemetria verso l'esterno; la ricerca web e l'unico traffico in uscita ed e esplicita.

## Client e API
- La web UI e solo uno dei client: i clienti devono poter usare Quorum "dietro le quinte" dalle proprie chat, strumenti di assistenza clienti e integrazioni.
- **Spec futura**: endpoint compatibile OpenAI (`/v1/chat/completions`) e API key per applicazione.
- Web UI in **Blazor WebAssembly**, servita dall'Host, che usa solo l'API pubblica. Editor a grafo (dopo l'MVP) come componente isolato: prima si valuta Z.Blazor.Diagrams, poi eventualmente React Flow / Rete.js via JS interop.

## Installazione
- `docker compose up`, con un wizard al primo avvio.
- Servizi: `postgres` (piattaforma, con healthcheck), `qdrant` (vector store di default), `quorum` (app), `ollama` opzionale via profilo compose.
- Il wizard chiede quale vector store usare (Qdrant bundled, Qdrant esterno, pgvector esterno) e quale modello di embedding.
- L'app applica le migrazioni all'avvio ed entra in "setup mode" finche non e configurata.
- Il wizard e protetto da un **setup token** stampato nei log al primo avvio.
- Chiave master per cifrare i segreti nel database: generata al primo avvio in un volume dedicato, oppure da variabile d'ambiente. Mai nel database.

## Dati
- **Database di piattaforma**: sempre PostgreSQL (tenant, utenti, configurazioni, licenza, metadati dei documenti). Nessuna scelta nel wizard. Non contiene vettori.
- **Conversation store**: astratto e sostituibile; implementazione di default sullo stesso PostgreSQL.
- **Vector store per il RAG**: astratto e configurabile, separato dal database di piattaforma. Implementazioni MVP:
  - **Qdrant** (default nel compose);
  - **PostgreSQL + pgvector** su server/database propri, non necessariamente quelli di piattaforma.
  - Astrazione candidata: `Microsoft.Extensions.VectorData`, che ha connettori per entrambi.
- **Modello di embedding**: configurabile (di default locale, es. via Ollama). La dimensione dei vettori dipende dal modello: cambiarlo richiede la reindicizzazione dei documenti, quindi il modello usato va salvato insieme a ogni collezione.
- **Isolamento dei tenant nel vector store**: una collezione per tenant, oppure filtro obbligatorio sul `tenant_id` nel payload (da decidere).
- **Postgres esterno**: profilo compose senza il servizio bundled e variabile `QUORUM_DB_CONNECTION`.
- **Bootstrap config** (connection string, chiave master, `QUORUM_BASE_URL`): da env/compose. **Configurazione applicativa** (modello, ricerca, utenti, agenti): nel database, impostata dal wizard.

## Autenticazione
- Account locali (ASP.NET Core Identity): default e accesso di emergenza dell'amministratore (break-glass).
- Un connettore **OIDC generico** configurabile nel wizard: issuer URL, client id/secret, mappatura claim e gruppi -> ruoli, provisioning al primo login.
- SAML e LDAP/AD solo tramite broker esterno (Keycloak, Authentik, Dex), documentato.
- Richiede `QUORUM_BASE_URL` stabile e di norma un reverse proxy con TLS.
- Sviluppo: Keycloak in `deploy/docker-compose.dev.yml`.

## Licenza
- Chiave firmata (Ed25519), verifica offline; attivazione/revoca online opzionale.
- Conteggio dei posti sul provisioning al primo login.
- Tipi di licenza (campo `edition` nella chiave firmata, insieme a posti, tenant e scadenza):

| Edizione | Uso | Note |
|---|---|---|
| **Community** | Gratuita, solo uso non commerciale | **5 utenti, 1 tenant**; non commerciale garantito dai termini di licenza |
| **Trial** | Valutazione | **30 giorni**, chiave con scadenza emessa da noi su richiesta, verificata offline; poi **sola lettura** |
| **Commerciale** | Per utente, e per team/azienda | Posti e tenant indicati nella chiave |

- La trial non e auto-generata dall'app: una trial interna si azzererebbe reinstallando. La chiave trial la emettiamo noi (es. via email) e l'app la verifica offline come le altre.
- Il non commerciale della Community non si puo imporre via software: lo garantiscono i termini di licenza; il software puo solo limitare posti, tenant o funzioni.
- **Sorgente: source-available**. Il codice e consultabile (argomento di fiducia sulla privacy), l'uso e regolato dalla licenza. La protezione commerciale si basa su: aggiornamenti e immagini Docker in registry privato legato alla chiave, supporto, termini di licenza, nome del licenziatario visibile in UI. La chiave privata Ed25519 non entra mai nel repository.
- Licenza del sorgente candidata: **PolyForm Noncommercial 1.0** (uso non commerciale gratuito) + licenza commerciale separata per gli altri usi. Da validare con un legale. Se si accettano contributi esterni serve un CLA, per poter continuare a rilasciare con doppia licenza.
- **Sola lettura** (trial scaduta): si consultano ed esportano conversazioni e documenti; niente nuovi messaggi, chiamate al modello, caricamenti o modifiche alla configurazione. L'amministratore puo sempre installare una nuova chiave.
- **Senza chiave installata**: l'app funziona come **Community implicita** (5 utenti, 1 tenant, solo uso non commerciale). La chiave serve per Trial e Commerciale. (Decisione del 2026-10-15, sostituisce "senza chiave = solo setup mode".)

## Domande aperte
- **[Decisione da prendere prima del rilascio pubblico del codice]** Licenza del sorgente: PolyForm Noncommercial vs Elastic License 2.0 (o altra), con revisione legale; CLA per i contributi esterni.
- Mappatura utente OIDC -> tenant (issuer per tenant, claim come `tid`, dominio email).
- Comportamento al superamento dei posti licenza (proposta: blocca nuovi utenti, non quelli esistenti).
- Limiti di posti/tenant della Trial (proposta: come una commerciale piccola, es. 10 utenti).
- Cosa Quorum NON e (confini dell'MVP).
- Metrica di successo dell'MVP e prezzo indicativo per utente.
- Isolamento multi-tenant nel database (schema per tenant, colonna `tenant_id`, database per tenant).
- Isolamento multi-tenant nel vector store (collezione per tenant o filtro su `tenant_id`).
- Strategia di chunking dei documenti.
- Formato dichiarativo di agenti e topologie.
