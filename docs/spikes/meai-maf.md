# Spike: Microsoft.Extensions.AI e Microsoft Agent Framework per il livello tool-calling

**Data:** 2026-10-17
**Branch:** `spike/meai-maf`
**Stato:** report per la decisione dell'utente. Nessuna modifica a spec 004, piano 004 o codice di `main`; la PR #30 non è toccata.
**Prototipo:** `spikes/meai-maf/` (non fa parte di `Quorum.slnx`; vedi il suo `README.md` per eseguirlo).

## 1. Raccomandazione

**Adottare Microsoft.Extensions.AI (MEAI) come livello di astrazione, ma solo i tipi: `IChatClient`, `ChatMessage`/`AIContent`, `AIFunction`, `DelegatingChatClient`. Tenere il nostro ciclo di tool-calling, riscritto come middleware `DelegatingChatClient`. Non usare `FunctionInvokingChatClient` (FICC). Non adottare Microsoft Agent Framework (MAF) ora: rivalutarlo nella spec 005 con uno spike dedicato.**

In termini della domanda ("tenere il nostro codice / solo MEAI / MEAI + MAF"): **solo MEAI, nella forma più leggera** (pacchetto `Microsoft.Extensions.AI.Abstractions`, senza `Microsoft.Extensions.AI`, senza `Microsoft.Extensions.AI.OpenAI`, senza OpenAI SDK, senza `Microsoft.Agents.AI`).

Perché, in cinque righe:

1. **La dipendenza arriva comunque.** `ModelContextProtocol.Core` 2.2.0, già scelto dal piano 004 (PR 6), dipende da `Microsoft.Extensions.AI.Abstractions`: `McpClientTool` è un `AIFunction`. I nostri tipi `ModelMessage`/`ToolDefinitions` duplicherebbero tipi che entrano nel progetto lo stesso.
2. **Il principio "provider astratti e sostituibili" (CLAUDE.md) oggi non è realizzato:** `OpenAiChatStream` è un client unico per un solo formato di rete. `IChatClient` è l'astrazione standard; il ciclo e gli strumenti restano quelli, per un secondo provider si scrive solo l'adapter.
3. **Tutti i requisiti della spec 004 sono raggiungibili sopra `IChatClient`** (prova: esperimento `x6`, 13 scenari ricalcati su `x2`, tutti verdi, più l'uso dentro un `ChatClientAgent` di MAF).
4. **FICC è raggiungibile ma costa più di quanto fa risparmiare** (sezione 4): per ottenere la semantica che la spec chiede servono un thread di "pump" con canale per gli eventi, un workaround sul significato di "giri", nessun gancio per i tool sconosciuti, e il risparmio è un ciclo di circa 150 righe.
5. **MAF non serve a 004 e non risolve ciò che 005 deve possedere** (sezione 7); si aggancia in seguito senza riscritture, perché un agente MAF accetta il nostro `IChatClient` così com'è (`UseProvidedChatClientAsIs`, provato in `x5`/`x6`).

**Cosa non cambia affatto:** entità, permessi, catalogo nativo, API di attivazione, `ToolPool`, redazione audit, validatore degli argomenti, budget del contesto, endpoint, `FakeModelServer` e quasi tutti i test di integrazione di PR #30 (sono a scatola nera sull'SSE).

**Confermo la decisione D-9 della spec** (pausa dello stream dentro una richiesta, stato in memoria) per 004; l'alternativa "senza stato" di MEAI/MAF funziona (provata) ma sposta complessità e cambia gli AC 32-40 (sezione 5).

## 2. Cosa è stato fatto e con quali limiti

Prototipo .NET 10 in `spikes/meai-maf/Spike`, pacchetti: `Microsoft.Extensions.AI` 10.11.0, `Microsoft.Extensions.AI.OpenAI` 10.11.0 (porta OpenAI SDK 2.14.0), `Microsoft.Agents.AI` 1.25.0, `Microsoft.Agents.AI.Workflows` 1.25.0, `ModelContextProtocol` e `.AspNetCore` 2.2.0.

| Esperimento | Contro cosa gira | Esito (una riga) |
|---|---|---|
| `x1` | server OpenAI-compatibile scriptato (sei forme di stream) | reasoning e frammenti di tool-call per tre adapter |
| `x2` | scriptato | 13 requisiti della spec con FICC |
| `x3` | scriptato | conferma stile MEAI/MAF, resume con sessione serializzata |
| `x4` | server MCP reale in-process su loopback | SDK MCP con il nostro `HttpClient` e la guardia URL |
| `x5` | scriptato | orchestrazioni MAF sequenziale e concorrente con la nostra pipeline |
| `x6` | scriptato | ciclo proprio come middleware, stesse situazioni di `x2` |
| `real` | **Ollama reale** `http://localhost:11434/v1`, `qwen3.8:27b` e `qwen2.5-coder:14b` | reasoning, tool-call, risultati, streaming |

**Limiti dichiarati** (cose non verificate):
- Il tool-calling reale è stato provato con Ollama (`qwen3.8:27b`). `qwen2.5-coder:14b`, pur con capability `tools`, risponde scrivendo la chiamata come JSON nel testo invece di emettere `tool_calls`: identico con tutti gli adapter, quindi è un limite del modello (il rischio "qualità dei modelli" del piano), non del client. Non ho provato vLLM, LiteLLM o llama.cpp reali: per quelli valgono solo le forme scriptate di `x1`.
- La guardia URL è provata con `127.0.0.2` come sostituto di `169.254.169.254` (qualsiasi macchina risponde su 127.0.0.2). Non ho riprovato le liste complete del piano (IPv4-mapped IPv6 ecc.): la questione era se l'SDK accetta il nostro `ConnectCallback`, ed è sì.
- "Nessuna telemetria in uscita" è dimostrato **dinamicamente** per i percorsi esercitati (sezione 8), non scandendo gli assembly.
- Le stime di lavoro nella sezione 6 sono stime mie, non misure.

## 3. Q1 - `IChatClient` su un endpoint OpenAI-compatibile (Ollama): reasoning, tool-call, risultati

**Risposta: l'adapter pronto (`Microsoft.Extensions.AI.OpenAI`) non basta. Il più economico che funziona è un `IChatClient` nostro di circa 135 righe, che è quanto resta di `OpenAiChatStream` + `ToolCallAssembler`.**

Ollama invia `delta.reasoning` (verificato con la richiesta grezza, `mode raw`). Esito per forma di stream (`x1`, A0 = adapter MEAI.OpenAI così com'è; A = A0 più un wrapper che rilegge il chunk grezzo; B = `IChatClient` nostro):

| Forma di stream | A0 | A | B |
|---|---|---|---|
| `reasoning_content` (stile LiteLLM) | ok | ok | ok |
| `reasoning` (Ollama) | **perso** | ok | ok |
| tool-call a frammenti per `index` (stile OpenAI) | ok | ok | ok |
| tool-call intero, `arguments` stringa (Ollama) | ok | ok | ok |
| tool-call intero, `arguments` come **oggetto**, senza `id` | **eccezione** | **eccezione** | ok (id generato) |
| due chiamate parallele entrambe con `index` 0 e id diversi | **la seconda sparisce, argomenti `null`** | idem | ok |

- Su Ollama reale (`qwen3.8:27b`, pipeline `x2` con `real`): con A e B il reasoning arriva (180-311 caratteri nelle tre esecuzioni), con A0 sono 0; le chiamate `native__calculator` e `native__current_datetime` vengono eseguite e il testo finale è corretto. Lo streaming non è bufferizzato (primo delta di testo a 64 s, risposta completa a 75 s, i delta si distribuiscono nel mezzo).
- A funziona leggendo `StreamingChatCompletionUpdate.Patch`, che nell'OpenAI SDK 2.14 è marcata `SCME0001` ("solo valutazione, può cambiare"): 15 righe, ma fragile, e **non risolve le due forme in cui l'SDK sbaglia** (le stesse che il piano 004 elenca come rischio "formati diversi per server").
- B regge tutte le forme perché riusa le regole di `ToolCallAssembler`. Pro: nessuna dipendenza dall'OpenAI SDK (circa 6 MB pubblicati in più rispetto a solo MEAI); contro: manteniamo noi il parsing.
- Non provato: `OllamaSharp` (client Ollama nativo `/api/chat` con `IChatClient`). Coprirebbe solo Ollama, non vLLM/LiteLLM, quindi non sostituisce B.

## 4. Q2 - Requisiti della spec con `FunctionInvokingChatClient`

Costruito in `ToolPipeline.cs`, provato in `x2` (tutti i casi contro un server scriptato; `T5`-`T12` includono tempi reali).

| Requisito | Punto di estensione | Esito |
|---|---|---|
| Audit `Started` **prima** della chiamata, fail-closed (D-28, AC-44) | `FunctionInvoker` | **ok** (T4: l'audit fallisce, lo strumento non gira, il modello riceve l'errore) |
| Timeout per chiamata (D-8, AC-23) | `FunctionInvoker` + `WaitAsync` | **ok**, anche per uno strumento che ignora il token (T5, T5b) |
| Limite di tempo totale per tenant (AC-24) | `CancellationTokenSource` esterno | ok (T6), **ma non si può mettere in pausa** durante una conferma (vedi sotto) |
| Limite di giri per tenant (D-8, AC-22) | `MaximumIterationsPerRequest` | **semantica diversa** (T7) |
| Tetto al risultato, errore e non troncamento (D-10, AC-26) | `FunctionInvoker` | ok (T8) |
| Controllo stato/policy al momento della chiamata (AC-25 e bordo "tool cambiato") | `FunctionInvoker` | ok (T13) |
| Nomi con prefisso `native__` / `<server>__` (D-16) | `DelegatingAIFunction` che sovrascrive `Name` | ok (T1: il modello vede i nomi con prefisso) |
| Una chiamata alla volta, nell'ordine emesso (D-22, AC-27) | `AllowConcurrentInvocation = false` (default) | ok (T9) |
| Eventi SSE `tool_call` / `tool_result` | `FunctionInvoker` + canale | **ok solo con un "pump"** (vedi sotto) |
| Budget di contesto per ogni giro (D-27, AC-21) | `DelegatingChatClient` sotto FICC | ok (T12) |
| Chiamata a **tool sconosciuto** (AC-25, AC-41) | nessuno | **no** (T2) |
| Validazione degli argomenti (AC-25) | nostra, nel `FunctionInvoker` | ok; con tipi sbagliati `AIFunctionFactory` lancia `JsonException` (T3b): la validazione resta nostra |

I tre punti dolenti:

1. **Giri.** Con `MaximumIterationsPerRequest = N`, FICC esegue N giri di strumenti e poi fa una **richiesta in più al modello senza strumenti** (T7: 3 esecuzioni e 4 richieste, l'ultima con `tools` assenti). La spec e PR #30 vogliono "al massimo N chiamate al modello, e le chiamate chieste nell'ultimo giro non girano, la risposta fallisce". Si può imitare (N = giri − 1 e un rilevatore di `FunctionCallContent` nell'ultimo risultato) ma è un adattamento fragile a un comportamento interno.
2. **Tool sconosciuto.** FICC risponde con la stringa fissa `Error: Requested function "x" not found.` senza passare dal `FunctionInvoker`: nessuna riga di audit (AC-41 la richiede anche per le chiamate rifiutate), nessun elenco degli strumenti disponibili (PR #30 lo mette nel messaggio). I soli membri `protected` di FICC sono `InvokeFunctionAsync` e `CreateResponseMessages`, non servono a questo. Workaround: osservare lo stream degli aggiornamenti dall'esterno e scrivere l'audit lì.
3. **Eventi durante l'attesa.** Con FICC il consumatore dello stream non può scrivere sull'SSE mentre l'invoker aspetta una conferma (sta dentro il suo `MoveNext`). Serve eseguire FICC su un task di sfondo e fondere gli aggiornamenti con gli eventi dell'invoker in un `Channel` (`AnswerRunner.RunAsync`, circa 40 righe) per far arrivare `tool_call requiresConfirmation` al browser *mentre* si aspetta. Funziona (T11a-c) ma è meccanismo in più, con la gestione di errori e cancellazione duplicata tra i due task.

Inoltre il timer totale di PR #30 conta anche il tempo di attesa dell'utente: con totale 5 minuti e conferma fino a 5 minuti, una conferma lenta consuma il totale. È un buco della spec indipendente da FICC (sezione 9).

**Alternativa provata: il ciclo proprio come middleware `DelegatingChatClient`** (`ToolLoopChatClient.cs`, esperimento `x6`). È il ciclo di PR #30 espresso su tipi MEAI: il metodo legge un giro dal client interno, tiene gli aggiornamenti di testo/reasoning, raccoglie le `FunctionCallContent`, e per ognuna emette *prima* un aggiornamento `FunctionCallContent` con le proprietà `quorum.callId`, `quorum.requiresConfirmation`, `quorum.expiresAt`, poi esegue, poi emette un `FunctionResultContent` con `quorum.status`. Sono aggiornamenti ordinari nello stesso iteratore: **niente canale, niente task di sfondo**. L'esecutore ha due fasi (`PrepareAsync`: controlli al momento della chiamata e validazione; `RunAsync`: attesa conferma, audit, esecuzione con timeout, tetto), che è proprio quello che PR 3 deve comunque introdurre per pubblicare `tool_call` prima di attendere.

Risultati `x6` (tutti verdi): U1 percorso felice; U2 tool sconosciuto con **audit e messaggio nostri**; U4 audit che fallisce non esegue; U5 timeout con strumento sordo; U6 tempo totale (fallisce con "total time limit"); **U7 giri: esattamente 3 richieste con MaxRounds=3, chiamate dell'ultimo giro non eseguite, risposta fallita, come PR #30**; U8 risultato troppo grande; U10 stop utente → audit `Cancelled`; U11a/b/c conferma approvata/negata/scaduta; **U14 l'attesa della conferma non conta nel tempo totale** (totale 500 ms, utente risponde dopo 900 ms: la risposta riesce); U12 budget del contesto; **U20 lo stesso middleware dentro un `ChatClientAgent` di MAF** (`UseProvidedChatClientAsIs`): gli aggiornamenti arrivano come `FunctionCallContent`/`FunctionResultContent` con le nostre proprietà, e policy/audit girano.

Dimensione: `ToolLoopChatClient.cs` (ciclo + esecutore a due fasi + timer pausabile) 227 righe nel prototipo; per confronto, in PR #30 `ToolLoop.cs` 150 righe + `ToolExecutor.cs` 273 righe. Il guadagno di FICC sarebbe quindi limitato a sostituire il ciclo, che è la parte che la spec rende più specifica.

## 5. Q3 - Conferma umana: due modelli a confronto

**Modello A (spec D-9, attuale):** lo stream resta aperto fino a 5 minuti, lo stato è in memoria (`PendingInteractions`), `POST .../decision` sblocca.
**Modello B (MEAI/MAF):** la run termina con un `ToolApprovalRequestContent`; la decisione arriva in una **nuova** richiesta che riprende con la cronologia (MEAI) o con la sessione (MAF).

Provato (`x3`): B funziona con MEAI puro (`ApprovalRequiredAIFunction`; stato da conservare: il messaggio dell'assistente con la chiamata e la richiesta, 444 byte di JSON, deserializzato in una "seconda richiesta" e ripreso; negazione: il modello riceve "Tool call invocation was rejected by the approver and must not be retried.") e con MAF (`ChatClientAgent` + `AgentSession`, sessione serializzata in 620 byte con `_pendingApprovalRequests` e la cronologia, ripresa su sessione deserializzata: lo strumento gira solo dopo la risposta). Due fatti da tenere presenti:

- **Round misti.** Se un giro contiene una chiamata `auto` e una `confirm`, FICC trasforma **tutte** in richieste di approvazione e non esegue nulla (3b: `executed=[]`, tre richieste); dopo l'approvazione eseguono nell'ordine emesso. L'applicazione dovrebbe approvare in automatico le `auto` (in MAF c'è `ToolApprovalAgent` con `AutoApprovalRules`, che coincide con la voce differita "conferme ricordate", D-20).
- **I contatori ripartono.** Giri e tempo totale di FICC valgono per richiesta: con B vanno ripresi dallo stato salvato, altrimenti D-8 non vale più sull'intera risposta.

| Aspetto | A (D-9) | B (stateless) |
|---|---|---|
| UI Blazor | un solo stream SSE; il dialogo si apre su `tool_call` con `requiresConfirmation`; un `POST` | due stream per una risposta (quello che termina, poi quello della ripresa); la UI deve avere lo stato "in attesa di conferma" anche al ricaricamento della pagina |
| Riavvio o ricarica pagina | la conferma si perde = negata (AC-39: "nulla è girato, risposta non completata") | **sopravvive** se lo stato è persistito (nuova tabella, es. `PendingApproval`: tenant, conversazione, utente, chiamata, messaggi serializzati, scadenza) |
| Lock di conversazione | tenuto fino a 5 minuti (accettato dalla spec) | rilasciabile mentre si aspetta, ma si deve comunque impedire un nuovo messaggio che cambi la storia |
| Scadenza | la gestisce la richiesta (timeout = negato, audit `TimedOut`) | **serve un job che scade le attese** e scrive l'audit, perché nessuna richiesta è viva |
| Più istanze dell'app | no (memoria) | sì |
| Audit | invariato | invariato, ma il "decision" viene registrato alla ripresa |
| Keep-alive `ping` ogni 15 s (piano) | necessario | non serve |
| Complessità | bassa: l'invoker aspetta un `TaskCompletionSource` (provato con FICC e con il ciclo proprio) | media/alta: persistenza, job di scadenza, stato "awaiting" nel messaggio, ripresa dei contatori |

**Raccomandazione: A per la spec 004.** Il prodotto è a istanza singola (CLAUDE.md, spec "Multiple application instances" differita), il costo di B è reale, e A si realizza identico con il ciclo proprio o con FICC. Per non chiudere la porta a B: il contratto (`tool_call` con `callId`, `POST .../decision`) è già lo stesso; B cambierebbe solo il comportamento dello stream. **Trigger di rivalutazione:** istanze multiple, o conferme che devono durare più di pochi minuti (approvazioni da un altro dispositivo).

Cosa cambierebbe nella spec **se si sceglie A** (consigliato): nulla in D-9 e AC-32..40; una precisazione (sezione 9): il tempo di attesa della conferma non conta nel limite totale di D-8. Cosa cambierebbe **se si sceglie B**: D-9; AC-32, AC-35, AC-39, AC-40; "Edge Cases" (conferma pendente, riavvio, stop utente durante una chiamata, conversazione eliminata mentre pendente); nuova entità `PendingApproval` e job di scadenza; "Module boundaries" (`IToolInteractions`); il piano 004 passi 12-15 e il rischio "Lock held while waiting".

## 6. Cosa cambia in PR #30 e nel piano 004

**Raccomandazione sulla sequenza:** mergiare PR #30 così com'è (funziona, i suoi 610 righe di test del ciclo sono a scatola nera e bloccano il comportamento) e fare **una PR di refactor "2b" prima di PR 3** (non dopo): PR 3 deve comunque spezzare l'esecutore in due fasi, quindi conviene farlo una volta sola sulla forma nuova. Il refactor è guardato dai test esistenti, che devono passare invariati.

| Parte di PR #30 | Destino |
|---|---|
| `OpenAiChatStream` + `ToolCallAssembler` | diventano `OpenAiChatClient : IChatClient` (adapter B del prototipo); il parsing e l'assemblaggio restano, cambia l'involucro (output = `ChatResponseUpdate`; reasoning = `TextReasoningContent`) |
| `ModelMessage` (SystemMessage, UserMessage, AssistantMessage, ToolMessage) | sostituito da `ChatMessage`; la serializzazione di rete si sposta nell'adapter |
| `ToolLoop` | diventa `ToolLoopChatClient : DelegatingChatClient`; la logica (giri, tempo totale, contesto) resta |
| `ToolExecutor` | resta nella sostanza (risoluzione, validazione, policy, audit, timeout, tetto); si divide in `Prepare`/`Run`; il controllo "tool sconosciuto" resta suo |
| `INativeTool`, `ActiveTool`, `ToolDefinitions` | `INativeTool` può diventare (o essere avvolto da) un `AIFunction`; il JSON mostrato al modello e contato nel budget deve essere **una sola fonte** con quello che serializza l'adapter |
| `AnswerChunk`, `ToolEvent` in `Agents.Contracts` | **restano**: i contratti pubblici non devono esporre tipi MEAI a Conversations; la traduzione `ChatResponseUpdate` → `AnswerChunk` sta dentro Agents |
| entità, permessi, catalogo nativo, attivazione, redazione audit, `ToolArgumentValidator`, `ContextWindow`, endpoint, migrazioni, `AnswerStream`, `FakeModelServer` e test | invariati |
| Pacchetti | aggiungere `Microsoft.Extensions.AI.Abstractions` (MIT, 2 MB circa) a `Quorum.Modules.Agents` |

Stima mia: un paio di giorni di lavoro per la 2b (non misurata).

**Piano 004, righe da emendare** (sezione 9 elenca i testi): tabella "Decisions" righe *Model messages*, *One model round*, *Execution and policy*, *MCP client*; passi 6, 7, 13, 20, 26; nuova PR "2b". Il passo 20 (prototipo MCP) è in gran parte già fatto da questo spike (vedi "MCP (Q4)" qui sotto).

### MCP (Q4)

Provato in `x4` contro un server MCP reale in-process (SDK `ModelContextProtocol.AspNetCore`) con il client `ModelContextProtocol` 2.2.0:

| Requisito | Esito |
|---|---|
| L'SDK usa **il nostro `HttpClient`** (handler `SocketsHttpHandler` con `ConnectCallback`) | **sì**; connessione a `127.0.0.2` rifiutata dalla guardia; hostname che "risolve" a un indirizzo bloccato rifiutato (3b); hostname risolto **dal nostro callback** (non dal DNS) si connette all'IP validato (3c): il callback decide davvero al momento della connessione |
| Redirect verso altro host non seguito (D-18, AC-4) | **sì** con `AllowAutoRedirect = false` (errore `307`); controllo di prova: un `HttpClient` normale lo segue, quindi il test è significativo |
| Header statici (D-21) | sì (`AdditionalHeaders`; il server ha visto `Authorization: Bearer ...`) |
| Capacità dichiarate dal client (D-29) | **nessuna**: `sampling=False roots=False elicitation=False` |
| `McpClientTool` come `AIFunction`: nome, descrizione, `JsonSchema`, annotazioni | sì; l'hash di definizione (D-14) si calcola su nome + descrizione + schema canonico, le annotazioni (`ReadOnlyHint`) restano a parte |
| Prefisso e policy/audit del nostro esecutore su uno strumento MCP | sì (`PrefixedFunction` su `McpClientTool`; timeout, tetto e audit come per i nativi) |
| Cancellazione: il timeout per chiamata arriva al server | sì (il server ha osservato la cancellazione) |
| Server non raggiungibile | `HttpRequestException` con `SocketException` alla radice; `ConnectionTimeout` configurabile (10 s della spec) |
| Connessione per risposta | `McpClient.CreateAsync` per risposta funziona; costo della handshake non misurato |

Due attenzioni: **`AIFunction.InvokeAsync` su un `McpClientTool` è lossy** (un risultato testuale torna come `TextContent`; un errore del server torna come JSON grezzo del `CallToolResult` con `isError: true`, non come eccezione). L'esecutore deve usare **`McpClientTool.CallAsync`**, che restituisce `CallToolResult { IsError, Content }` (provato: `IsError=True` per lo strumento che fallisce, `text=hey` per `echo`). E restano **nostri**, perché l'SDK non li conosce: registro dei server, scope e attivazione, revisione con hash, controllo URL in fase di registrazione (`tool.url_blocked` prima della connessione), gestione `Unavailable`, mappatura dei nomi. Il passo 20 del piano ("prototipo contro i nostri requisiti") è quindi soddisfatto: **si usa l'SDK, non serve il client minimo di ripiego.**

## 7. Q5 - Sovrapposizione con la spec 005 (topologie, multi-agente)

Letto: documentazione MAF su workflow, orchestrazioni, checkpoint e workflow dichiarativi; provato in `x5` (`AgentWorkflowBuilder.BuildSequential` e `BuildConcurrent`, tre agenti, uno con la nostra pipeline di policy e audit passata come client "così com'è": policy e audit girano dentro il workflow; l'output sequenziale fluisce da un agente al successivo; il concorrente produce tre flussi e un aggregato).

**Cosa adotteremmo (se 005 lo conferma):**
- i costruttori di orchestrazione come motore di esecuzione: sequenziale, concorrente, handoff, group chat, Magentic (GA dalla 1.0 del 2026-04-03 secondo il blog di rilascio; i workflow sono a grafo con "supersteps");
- `RequestPort`/richieste esterne e approvazione di strumenti come modello di "pausa" per i casi multi-agente;
- i checkpoint (`CheckpointManager`, `ICheckpointStore<T>`, `FileSystemJsonCheckpointStore`) per riprendere dopo un riavvio.

**Cosa possederemmo comunque:**
- **Lo schema dichiarativo** (agenti, strumenti, topologie in file; lo stesso schema per l'editor grafico, principio di prodotto). I workflow dichiarativi di MAF sono YAML con espressioni PowerFx, il pacchetto .NET è ancora pre-release (comandi di installazione con `--prerelease`) e i suoi nodi agente passano da `InvokeAzureAgent` e da un progetto Foundry per il C#: non è il nostro schema e non si appoggia a modelli locali. Va costruito noi e *tradotto* in un `Workflow` MAF a runtime.
- **Il nodo di sintesi e "ogni agente risponde quando rilevante"** (`docs/product.md`): è un grafo con archi condizionali, un fan-out limitato e un aggregatore: realizzabile con `WorkflowBuilder`, ma è nostro il significato.
- **Concorrenza limitata** ("risorse limitate: le topologie vanno anche in sequenza, limiti configurabili", `docs/product.md`): il fan-out concorrente di MAF parte in parallelo per default; il limite lo mettiamo noi.
- **Multi-tenant e permessi per agente**, endpoint del modello per agente, la pipeline di strumenti (policy, conferme, audit, budget) e la persistenza (D-4), il mapping degli eventi del workflow in SSE.
- **Il checkpoint store** su PostgreSQL con `TenantId` (la documentazione stessa avverte che lo store dei checkpoint è un confine di fiducia e che l'identità degli esecutori dipende da `Id` e `Name` dell'agente: **rinominare un agente in una topologia modificata dall'editor invalida i checkpoint**, un attrito con "config-first").

**Conclusione per 005:** MAF è un candidato ragionevole come motore, ma la decisione va presa con uno spike sui requisiti veri di 005 (formato dichiarativo, concorrenza limitata, checkpoint tenant-aware). Adottare MEAI ora non la ostacola e la agevola: il nostro `IChatClient` con ciclo di strumenti si passa a un agente MAF senza modifiche (provato).

## 8. Q6 - Licenze, dimensioni, dipendenze, stabilità, principi (self-hosted, offline, nessuna telemetria)

**Licenze** (dai nuspec dei pacchetti restaurati): MEAI, `Microsoft.Agents.AI`, `Microsoft.Agents.AI.Workflows`, OpenAI SDK, `System.ClientModel`, ML.Tokenizers: **MIT**. `ModelContextProtocol*`: **Apache-2.0**. `Google.Protobuf` (arriva da `Microsoft.ML.Tokenizers`, quindi da MAF): **BSD-3-Clause**. Nulla di copyleft o a pagamento. Come per PdfPig, la regola del piano resta di controllare le licenze transitive a ogni aggiunta.

**Dimensioni** (pubblicazione Release di progetti minimi, rispetto a una console vuota di 0,18 MB; dll aggiunte comprese le astrazioni `Microsoft.Extensions.*` che un host ASP.NET ha già):

| Insieme | MB pubblicati | dll aggiunte |
|---|---|---|
| `Microsoft.Extensions.AI` | 2,1 | 8 con il progetto (Abstractions, AI, Tensors e `Microsoft.Extensions.*` che un host ASP.NET ha già) |
| `Microsoft.Extensions.AI` + `.OpenAI` (porta OpenAI SDK) | 8,3 | 17 |
| `Microsoft.Agents.AI` (MAF core) | 4,4 | 17: aggiunge `Evaluation`, `Compliance.Abstractions`, `VectorData.Abstractions`, `ML.Tokenizers` (+ `Google.Protobuf`), `FileSystemGlobbing`, `ObjectPool` |
| `ModelContextProtocol.Core` | 2,3 | 5: **dipende da `Microsoft.Extensions.AI.Abstractions`** |
| `Microsoft.Agents.AI.Workflows` | n.m. | in più `OpenTelemetry.Api` 1.18.0 |

**La raccomandazione porta solo `Microsoft.Extensions.AI.Abstractions`** (già trascinato da MCP.Core): contiene `IChatClient`, `DelegatingChatClient`, `AIFunction`, `AIFunctionFactory`, `DelegatingAIFunction`, `ApprovalRequiredAIFunction`, `TextReasoningContent` (verificato per riflessione). `ChatClientBuilder` e FICC stanno in `Microsoft.Extensions.AI`, che non serve.

**Offline e nessuna telemetria:** per i percorsi esercitati (tutti gli esperimenti `x1`-`x5`, con MEAI, OpenAI SDK, MAF, MCP e server in-process) il listener `NetTrace` ha registrato 54 eventi di rete distinti: richieste solo a `127.0.0.1`, `127.0.0.2` e ai tre hostname di test definiti da me (`ok.test`, `rebind.test`, `localhost`), una sola risoluzione DNS (`localhost`) e connessioni socket solo a loopback. **Nessuna richiesta verso host non configurati.** MAF/MEAI emettono telemetria solo come `ActivitySource` locale se si installa un ascoltatore o `UseOpenTelemetry()`; `Microsoft.Agents.AI.Workflows` dipende da `OpenTelemetry.Api` (solo API, senza esportatori). Non ho scandito gli assembly alla ricerca di URL.

**Stabilità API:**
- MEAI 10.x: i tipi che servono (chat, funzioni, contenuti, middleware, approvazione) **non sono `[Experimental]`**; lo sono realtime, voce, immagini, file ospitati, `RoutingChatClient`, `AIFunctionNameAttribute`: non li usiamo.
- `Microsoft.Extensions.AI.OpenAI`: la via d'uscita per il reasoning (`Patch`) è marcata `SCME0001`.
- MAF: 1.0 GA il 2026-04-03 con impegno di retrocompatibilità, 1.25.0 oggi (circa una minor a settimana). Molte parti sono `[Experimental]` (`AgentSessionStore`, `LoopAgent`, provider di file e di task in background, `MagenticDefaultPrompts`, `WorkflowSessionCheckpointRecovery` ecc.); lo stesso blog di rilascio elenca come preview Skills, AG-UI, DevUI, integrazioni Foundry. I workflow dichiarativi sono pre-release. `Microsoft.Agents.AI` 1.25.0 fissa MEAI >= 10.10.0: l'aggiornamento di MEAI e MAF va fatto insieme.
- Nel nostro caso (solo `Abstractions`) il rischio di API è basso; con MAF, medio, da pesare in 005.

**Conflitti con CLAUDE.md:** nessuno per la raccomandazione. Per MAF: il principio "config-first con lo stesso schema per l'editor" (identità degli esecutori legata a `Id`/`Name`, workflow dichiarativi PowerFx/Foundry) e "risorse limitate" (concorrenza di default) richiedono lavoro nostro, non sono conflitti di licenza o telemetria.

## 9. Emendamenti proposti (da decidere; non applicati)

**Spec 004** (richiedono la decisione dell'utente, tutti piccoli):

1. **D-8 / AC-24**: aggiungere "il tempo che la risposta passa in attesa di una persona (conferma, `ask_user`) non conta nel limite di tempo totale". Oggi il totale di PR #30 include l'attesa: con totale 5 min e conferma fino a 5 min i due limiti si pestano. (`x6` U14 mostra l'implementazione.)
2. **D-8 / AC-22**: precisare "un giro = una chiamata al modello; le chiamate chieste nell'ultimo giro consentito non vengono eseguite e la risposta fallisce" (è il comportamento di PR #30; con FICC cambierebbe).
3. **"Integrations"**: dichiarare che l'endpoint OpenAI-compatibile è usato **tramite un adapter `IChatClient`** interno al modulo Agents (provider astratti), e che i tipi MEAI non attraversano `Agents.Contracts`.
4. **"Integrations" / MCP**: chiudere la scelta "SDK ufficiale o client minimo" a favore di `ModelContextProtocol.Core`, con la nota "esecuzione tramite `CallAsync`, non `AIFunction.InvokeAsync`".
5. **"Deferred Decisions"**: aggiungere "Conferme senza stato / riprendibili (resume in nuova richiesta, stato persistito)" con trigger "istanze multiple o attese di conferma oltre pochi minuti" (alternativa B della sezione 5).
6. **AC-39** (nessuna modifica di sostanza): resta valida con A; annotare che con B cambierebbe.

**Piano 004:**

1. Tabella *Decisions*, *Model messages*: `ChatMessage` di MEAI al posto della gerarchia `ModelMessage`.
2. *One model round*: `OpenAiChatClient : IChatClient`; `ToolLoopChatClient` come `DelegatingChatClient`.
3. *Execution and policy*: esecutore a due fasi (`Prepare`, `Run`); `tool_call` con `requiresConfirmation` pubblicato tra le due.
4. *MCP client*: sostituire "PR 6 inizia con un prototipo..." con il risultato di questo spike (SDK sì, `CallAsync`).
5. Passi 6, 7 (e 13): riscrivere in base ai punti precedenti; passo 20: ridotto a un test di integrazione, non più un prototipo di scelta; passo 26: `McpToolInvoker` su `CallAsync`.
6. Nuova **PR 2b** (refactor a `IChatClient`) tra PR #30 e PR 3.
7. *Risks*: aggiungere "Qualità del tool-calling dei modelli locali: `qwen2.5-coder:14b` dichiara `tools` ma scrive la chiamata come testo" (mitigazione: nota nel manuale del passo 29; il flag `SupportsTools` da solo non basta come segnale).

**Fuori da 004:** `CLAUDE.md` "Tech Stack": aggiungere `Microsoft.Extensions.AI.Abstractions` (astrazione del provider LLM). Spec/piano 001: nota che `OpenAiChatStream` diventa l'adapter `IChatClient` (il piano 001 lo prevedeva già).

## 10. Cosa resta da fare dopo la decisione

- Se "solo MEAI": PR 2b come da sezione 6, poi PR 3 con modello A e l'esecutore a due fasi.
- Se "tenere il nostro codice": nessun lavoro; accettare che l'adapter e il ciclo restano doppioni dei tipi MEAI che MCP.Core introduce.
- Se "MEAI + FICC": possibile (`x2` lo dimostra), ma serve il pump e i workaround di sezione 4; sconsigliato.
- Se "MEAI + MAF" ora: non necessario a 004; valutare in 005.
- Riproduzione: `spikes/meai-maf/README.md`.
