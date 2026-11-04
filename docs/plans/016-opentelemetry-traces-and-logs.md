# Plan 016: OpenTelemetry Traces and Logs

**Spec:** `docs/specs/016-opentelemetry-traces-and-logs.md` (Approved 2026-11-04)
**Architecture:** Modular Monolith. The telemetry wiring lives in the Host (composition root); each module only creates its own `ActivitySource` and tags. One migration (Agents: `TraceId` on the tool audit entry); no new endpoint.
**Mode:** tests per acceptance criterion with an in-memory exporter on the existing `QuorumApp` fixture (D-23); the compose profile (AC-21, AC-22) is tried by hand, together, in the browser, and recorded in `docs/checks/016-observability-profile.md`.
**Status:** Approved (2026-11-04)
**Affected:** `Quorum.Host` (`Infrastructure/Telemetry/**`, `Program.cs`, `appsettings.json`), `Quorum.Modules.Agents` (`Chat/OpenAiChatClient.cs`, `Chat/AnswerPipeline.cs`, `Chat/SubAgentRunner.cs`, `Tools/ToolCallInvoker.cs`, `Embeddings/OpenAiEmbeddings.cs`, `Domain/ToolAuditEntry.cs`, `Features/Tools/ToolAuditEndpoints.cs`, a migration), `Quorum.Modules.KnowledgeBases` (`Features/Search/KnowledgeBaseSearchEngine.cs`), `Quorum.Modules.Conversations` (`Extraction/DocumentTextExtractor.cs`), `Quorum.Web.Client` (`Pages/AuditLog.razor`, `Api/ApiModels.cs`), `Directory.Packages.props`, `docker-compose.yml`, `deploy/observability/**`, `.env.example`, `docs/`, tests and fixtures.

## What the code looks like today

- The answer is `ChatClientAgent` → `FunctionInvokingChatClient` → `ContextGuardChatClient` → `OpenAiChatClient` (our own `IChatClient`), built in `AnswerPipeline.ProduceAsync` and again in `SubAgentRunner`. Nothing calls `UseOpenTelemetry`.
- **`OpenAiChatClient` reports no token usage and no model id**: the request has no `stream_options` and the stream parser never yields `UsageContent`. The `chat` span of MEAI would be empty of tokens until this is added (spec 016 Integrations).
- The producer runs on `Task.Run` and the answer is an async iterator (`AnswerPipeline.RunAsync`), so a root `Activity` has to be started and handed over explicitly.
- `ToolCallInvoker` is the `FunctionInvoker` of the function-invoking client: it runs inside the span of the tool call. The audit entry is created in `ExecuteAsync` and `RecordAsync` through `ToolAuditEntry.Start`; the wait for a confirmation happens before, in the same method.
- A sub-agent is a tool call (`AgentQuorumTool`) handled by `SubAgentRunner`; a `SubAgentAbortException` ends the turn when the edge says so (spec 013 D-13).
- Serilog is configured in `Program.cs` from `appsettings.json` (console sink). The test fixture swaps the logger in `QuorumApp`.
- `FakeModelServer` returns no `usage`.

## Decisions

| Topic | Choice |
|---|---|
| Where it lives | `Quorum.Host/Infrastructure/Telemetry/`: `TelemetryOptions` (`Quorum:Telemetry`: `CaptureContent`, `AdminRequests`, `TraceUrlTemplate`), `TelemetrySetup` (extension `AddQuorumTelemetry`, called from `Program.cs` after `AddSerilog`), `QuorumActivityNames` (the three sources, public constants in `Quorum.Shared` so modules and Host agree). `Quorum.Shared` carries names only, no logic. |
| On or off (D-1) | Export is enabled when `OTEL_EXPORTER_OTLP_ENDPOINT` is set (read from configuration, which includes the environment). When it is not, no `TracerProvider` listens, so no activities are created; the code that starts spans uses `ActivitySource.StartActivity` and tolerates null. |
| Exporter (D-19) | OTLP with protocol and headers from the standard variables and the SDK's bounded batch queue, so a slow or dead collector never runs on the request path and data is dropped when the queue is full. The single warning on the console is raised when the exporter first fails to send; how the SDK lets the app see that (self-diagnostics or a wrapper around the exporter) is settled in PR 1 against the real behaviour, before PR 2 starts (see Risks). |
| Resource (D-22) | `service.name=quorum`, `service.version` from `AssemblyInformationalVersion` (MinVer), the rest from `OTEL_RESOURCE_ATTRIBUTES`. |
| Sources (D-22) | `Quorum.Agents`, `Quorum.Conversations`, `Quorum.KnowledgeBases` (each module owns a `static ActivitySource` in an `internal static class`), plus `Experimental.Microsoft.Extensions.AI` and `Experimental.Microsoft.Agents.AI` registered with `AddSource`. The exact framework names are read from the packages in use when the PR is written (the Agent Framework default is documented; the MEAI one is checked in the code). |
| Framework instrumentation (D-7) | `OpenTelemetryChatClient` goes **below** `FunctionInvokingChatClient`, around `ContextGuardChatClient`, so each round is a `chat` span and the function-invoking client finds its source for `execute_tool`; the agent is wrapped with the Agent Framework's OpenTelemetry builder step for `invoke_agent`. `EnableSensitiveData = TelemetryOptions.CaptureContent`. A test asserts the span names before anything else is built on them. If `execute_tool` does not appear, the invoker starts its own span with the same name and attributes (fallback in Risks). |
| Tokens and model (D-7, spec Integrations) | `OpenAiChatClient` sends `stream_options: {"include_usage": true}`, reads the final chunk's `usage` and yields a `UsageContent` (input and output tokens) and sets `ModelId` and `ResponseId` on the updates. A server that does not send usage leaves the tokens empty; it is never an error. |
| Root span (D-13, D-14) | `AnswerPipeline.RunAsync` starts `quorum.chat_turn` (source `Quorum.Agents`) with the GUID tags of D-9, hands it to the producer (`Activity.Current = root` as the first statement on the `Task.Run`), and stops it in the `finally`. Tags `quorum.stop_reason`, `quorum.rounds`, `quorum.tool_calls`. Status is set by one small function, `TurnOutcome`, from the end state of the run (cancelled by the user: unset; `AnswerStop.Rounds`, `AnswerStop.TotalTime` or a failure: Error). |
| Tags on framework spans (D-7, D-9) | One `BaseProcessor<Activity>` (`QuorumTagsProcessor`, registered in Host) copies the Quorum tags (tenant, user, conversation, assistant, agent, agent slug) from the parent on `OnStart`; each child copies from its parent, so the tags reach every span of the turn without touching the frameworks. `SubAgentRunner` sets the slug and the delegation call id on the sub-agent run. |
| Time to first token (D-8) | Inside the `chat` span, `OpenAiChatClient` adds the event `quorum.first_token` on the first content it yields (`Activity.Current`), since it runs inside the span of MEAI. |
| Tool spans (D-7, D-15) | `ToolCallInvoker` writes onto `Activity.Current` (the `execute_tool` span): `quorum.tool.origin`, `quorum.tool.decision`, `quorum.tool.outcome`, `quorum.tool.round`, `quorum.tool.args_hash` (SHA-256 of the raw arguments, first 16 hex characters; never the arguments) and the Quorum error code; the span is set to Error for `Failed`, `TimedOut` and `Denied` is not an error (a refusal is a decision). The round is counted in `AnswerRun`. |
| Confirmation (D-17) | `quorum.confirmation` child span (source `Quorum.Agents`) around `WaitForReplyAsync`, tag `quorum.decision`; the wait is already excluded from the total time by `Deadline.Pause()`, which is why it is the same range. |
| Delegation failure (D-16) | A failed run sets Error on the `invoke_agent` span and on the delegation's tool span with its `stop_reason`. The turn follows the answer: a `SubAgentAbortException` (spec 013 D-13, the edge says so) ends the answer as failed, so the turn is Error; when the edge lets the parent go on, the turn stays Ok. |
| Content capture (D-2) | A second processor, `ContentGuardProcessor`, runs on `OnEnd` and, when `CaptureContent` is on, passes the tool argument and result attributes the framework set (`gen_ai.tool.call.arguments`, `gen_ai.tool.call.result`) through `AuditRedactor` and cuts every string tag at the limit; when it is off, the framework never records them (`EnableSensitiveData=false`). |
| Admin spans (D-10) | ASP.NET Core instrumentation with a `Filter`: static files, `/api/version`, `/_framework` and the health paths are never traced; when `AdminRequests` is off, only the routes of the answer path are traced (`POST /api/conversations/{id}/messages`, `.../regenerate`, `.../tool-calls/*`); when on, every `/api` request. |
| Database and HTTP (D-8) | `AddHttpClientInstrumentation` and the Npgsql source (`Npgsql.OpenTelemetry`); no EF Core command text (`SetDbStatementForText=false` on the Npgsql side, and no EF instrumentation package). |
| Embeddings and vector queries (D-8) | `OpenAiEmbeddings.EmbedAsync` starts `quorum.embedding` (model name, input count, duration); `KnowledgeBaseSearchEngine` starts `quorum.vector_query` per knowledge base in the parallel step (kind, result count, outcome; no query text). One place for all store kinds, so a new connector inherits it. |
| Attachments (D-8) | `DocumentTextExtractor.ExtractAsync` starts `quorum.attachment_extraction` (format, size in bytes, outcome). |
| Logs (D-18) | The console sink stays in `appsettings.json`. When export is on, `AddSerilog` also writes to `Serilog.Sinks.OpenTelemetry` with endpoint, protocol, headers and resource from the same variables, set explicitly in code (the sink is not trusted to read the environment). The sink fills `TraceId` and `SpanId` from `Activity.Current`. `LogContext` gets the GUID properties at the start of the turn (D-9). |
| Audit link (D-20, D-21) | `ToolAuditEntry.TraceId` (`string?`, 32 characters), written from `Activity.Current?.TraceId` in `ToolAuditEntry.Start`; one EF migration in Agents. `TraceLinks` (singleton, built from `TelemetryOptions`) returns the ready `traceUrl`; `ToolAuditEndpoints` adds `traceId` and `traceUrl` to the item DTO. `AuditLog.razor` shows the id and, when there is a link, makes it a link. The template is validated once at startup, only when export is on (AC-23, AC-24): a failure logs one warning that names the rule, not the template, and disables the link. |
| Compose profile (D-4, D-5, D-6) | Services in `docker-compose.yml` under `profiles: [observability]`: `otel-collector` (contrib or core image pinned, config in `deploy/observability/otel-collector.yaml`: OTLP in, exporters to Tempo, Loki and OpenLIT), `lgtm` (Grafana LGTM image, UI on `127.0.0.1`, password from `.env`), `openlit` with its ClickHouse and NATS (UI on `127.0.0.1`, key from `.env`). No OTLP port is published. `.env.example` gains the two lines of D-5, the Grafana password and the OpenLIT key, with the startup warning of AC-22 raised by the app when it sees the defaults. |
| Documentation (spec Integrations) | `docs/observability.md` (operator guide: the two lines, content capture, admin spans, the trace link, Langfuse behind the collector), a README line, `docs/database-schema.md` regenerated for the new column, and a note in `docs/mcp-tools.md`-style tables where the audit response is described. |
| Tests (D-23) | `QuorumApp` registers an in-memory exporter when a test asks for telemetry (`WithTelemetry()`), not for every test, so the suite keeps its speed. `FakeModelServer` gains an optional `usage` block in the stream. Span trees are asserted by name, parent, tags and status; no test sleeps. |

## Order and parallel work

- **PR 1** `feat/016-telemetry-foundation` — packages in `Directory.Packages.props`, `TelemetryOptions`, `TelemetrySetup`, the resource, sources, the `QuorumTagsProcessor`, ASP.NET Core / HttpClient / Npgsql instrumentation with the admin filter, the Serilog OTLP sink and the never-blocking behaviour, the test fixture support (`WithTelemetry()`, in-memory exporter). Nothing else depends on the choice of exporter, so everything after builds on this.
- **PR 2** `feat/016-chat-turn-spans` — the model and the turn: usage and model id in `OpenAiChatClient`, `UseOpenTelemetry` on the chat client and on both agent builders, the root span, `TurnOutcome`, stop reasons and counts, the first-token event, `LogContext` properties. Depends on PR 1.
- **PR 3** `feat/016-tool-spans-and-audit-link` — tool tags and the argument hash, the confirmation span, delegation errors, `ContentGuardProcessor`, `TraceId` on the audit entry with its migration, the audit API fields, the UI link, `TraceLinks` and its startup check. Depends on PR 2 (it writes onto the spans PR 2 makes visible).
- **PR 4** `feat/016-pipeline-spans` — embeddings, vector queries, attachment extraction. Depends only on PR 1 and touches files no other PR touches, so it can run in parallel with PR 2 and PR 3.
- **PR 5** `feat/016-observability-profile` — the compose profile, the collector file, `.env.example`, the startup warning for default credentials, the operator guide, the browser check document, plan and spec status updates. Needs no code from the others, so it can run in parallel; its check (AC-21) is done last, together, once PR 2 and PR 3 are in.

## Acceptance criteria map

| AC | PR | How it is verified |
|---|---|---|
| AC-1 | 1, 3 | Integration: no endpoint, no listener, an answer with a tool call; the audit entry has a null `TraceId` (the audit half lands with PR 3) |
| AC-2 | 2 | Integration with the in-memory exporter and `FakeModelServer` usage: root + `chat` span with model, duration, tokens |
| AC-3 | 3 | Integration: `execute_tool` is a child of the round span with the Quorum tags; the audit entry's `TraceId` equals the trace |
| AC-4, AC-5 | 3 | Integration with two delegations and a limit of 2: subtree, slug and call id tags, sibling spans that overlap |
| AC-6 | 2, 3 | Integration: no message text, argument or result in any span or log with capture off (turn in PR 2, tools in PR 3) |
| AC-7 | 3 | Integration: capture on, a tool argument with a sensitive key and a very long result; the exported attributes are redacted and cut |
| AC-8, AC-9, AC-10 | 2 | Integration: user cancel, rounds limit, total time (the limits already have fakes in the 004 tests): status and `stop_reason` of the root |
| AC-11, AC-12 | 3 | Integration: the same call three times (same hash, different rounds, no argument in the span); a timed-out tool |
| AC-13 | 3 | Integration: a failing sub-agent with a continue-edge and with an abort-edge (the turn is Ok, then Error) |
| AC-14 | 3 | Integration: a tool with `Confirm` policy and a reply; `quorum.confirmation` holds the wait, the tool span's duration does not |
| AC-15, AC-16 | 4 | Integration: a knowledge-base search on the Qdrant fixture (embedding span, vector span, kind and count, no query text); an attachment upload |
| AC-17 | 1 | Integration: create an agent with `AdminRequests` off and on |
| AC-18 | 1 | Integration: an event written inside a span reaches a recording log exporter with the same `TraceId` and `SpanId`; the console sink still has it (`LogCapture`) |
| AC-19 | 1 | Integration: endpoint pointing at a closed port; answers complete, one warning, no per-span log |
| AC-20, AC-23, AC-24 | 3 | Integration: audit API with and without a template; a table of invalid templates; export off with an invalid template logs nothing about it |
| AC-21, AC-22 | 5 | By hand, together, in the browser (`docs/checks/016-observability-profile.md`); AC-22 also has an integration test on the warning |

## Verification

Per PR: `dotnet build` (Debug and Release, since CI builds Release), the tests of the components the PR touches, and the full `dotnet test` (Docker) only at the end of the PR; `dotnet format --verify-no-changes`; `get_diagnostics`. PR 2 and PR 3 also run the existing chat and tool-calling tests, since the pipeline they touch is the hot path. PR 5 closes the plan: status flipped to Implemented in the plan and the spec.

## Risks

- **`execute_tool` may not appear from `FunctionInvokingChatClient`** unless the inner client chain exposes the instrumentation's source. The first test of PR 2 asserts the span names; if the span is missing, the invoker starts the span itself with the same name and attributes, and the rest of the plan is unchanged.
- **`Activity.Current` does not flow out of an async iterator, and the producer runs on `Task.Run`.** The root is started in `RunAsync`, handed to the producer explicitly and stopped in the `finally`; the tests assert the parent of the first model span.
- **Servers that reject `stream_options`.** It is a standard OpenAI field accepted by the usual local servers (Ollama, vLLM, llama.cpp, LM Studio), but not guaranteed. If one answers `400`, the adapter's existing error path reports it; the mitigation is to retry once without it only if a real server needs that.
- **Redacting framework-captured content needs the end-of-span processor.** Whether `gen_ai.tool.call.*` is written as span attributes or as events depends on the package version; AC-7 is the test that decides, and it runs on the versions pinned in PR 1.
- **One warning when the collector is down (D-19).** The OpenTelemetry exporter does not raise an event per dropped batch in a form the app can turn into one warning; the plan does not promise more than a single warning when the exporter reports its first export failure through the SDK's self-diagnostics hook. AC-19 is written against that, and the check is made on the real behaviour in PR 1 before PR 2 starts.
- **A sub-agent edge that aborts the turn** (spec 013 D-13) makes the turn Error although D-16 reads "the turn stays Ok": the plan follows the edge, as D-14 says the turn is Error only when the answer fails, and AC-13 covers both.
- **Package and image versions** are chosen when each PR is written and pinned (`Directory.Packages.props`, compose image tags), checked against nuget.org and the registries then, not here.
- **The profile weighs on a small server.** It is opt-in; the guide says what it needs and that the three services can be stopped without touching the app.
