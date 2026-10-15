# Quorum - Brainstorming

Data: 2026-10-15. Nome scelto: **Quorum** (alternative scartate: Chorus, Conclave, Hearth, Weft, Tessera, Plexus per conflitti con prodotti esistenti). Marchio e dominio da verificare prima del lancio.

## Visione
Piattaforma di orchestrazione agentica **self-hosted ed estensibile via configurazione**. L'utente semplice ha una chat pronta con tool base e un proprio modello self-hosted; l'azienda la estende con RAG, tool di business e agenti specializzati.

## Differenziatori
- Facilita di installazione.
- Topologie multi-agente definibili da config **e** da editor visuale (stessa rappresentazione): ogni agente risponde se pertinente, un nodo di sintesi produce la risposta unica.
- Agenti, tool, skill e prompt configurabili; provider di ricerca web sostituibile con preset suggeriti.
- Tool RAG su documentazione aziendale/personale/team.
- Conversation store plug and play (default, Postgres, AWS, ...).
- Guardrail di classificazione, embedding e semantic cache (opt-in).

## Modello commerciale
- Licenza per utente, e per team/azienda. Hosting a carico del cliente (BYOK per le chiavi LLM).
- Verifica licenza offline con chiave firmata (Ed25519); online opzionale.

## Decisioni
| Tema | Decisione |
|---|---|
| MVP | Chat con preset |
| Forma | Backend/API + piu client |
| Tenancy | Una installazione, piu tenant |
| Standard | MCP per i tool, formati aperti |
| Licensing | Chiave firmata offline, online opzionale; edizioni Community (non commerciale, 5 utenti, 1 tenant), Trial (30 giorni, poi sola lettura), Commerciale |
| UI | Blazor WebAssembly sopra l'API pubblica; editor a grafo come isola JS futura |
| Architettura moduli | Vertical Slice dentro ogni modulo (MVP) |
| API per terzi | Spec futura: endpoint compatibile OpenAI + API key per applicazione |
| Versioni e rilasci | Trunk-based, Conventional Commits, MinVer, release-please su GitHub (`docs/release-process.md`) |
| Sorgente | Source-available (candidata PolyForm Noncommercial + licenza commerciale) |
| Stack | .NET + dotnet-claude-kit |
| Database piattaforma | Solo PostgreSQL, bundled nel compose, senza vettori |
| Vector store RAG | Astratto: Qdrant (default) o pgvector su server/db dedicato |
| Conversation store | Astratto, default su Postgres |
| Autenticazione | Account locali + OIDC generico; SAML/LDAP via broker |
| Installazione | `docker compose up` + wizard con setup token |
| Sviluppo | Keycloak e Postgres in `deploy/docker-compose.dev.yml` |

Dettagli in `docs/product.md`.

## Rischi
- Ambito molto ampio: tenere l'MVP verticale.
- Un solo modello di grafo per config e UI, altrimenti divergono.
- Plugin .NET (assembly esterni): per l'MVP preferire provider integrati + MCP/HTTP.
- Sicurezza dei tool eseguiti sull'infrastruttura del cliente.

## Domande aperte
- Preset base esatti (assistente ricerca web, interprete documenti, conoscenza del modello).
- Modello dati multi-tenant e isolamento.
- Prima UI (web) e tecnologia client.
- Scelta provider LLM/astrazione (Microsoft.Extensions.AI, LiteLLM opzionale).
- Posizionamento rispetto a Open WebUI, LibreChat, Dify.
