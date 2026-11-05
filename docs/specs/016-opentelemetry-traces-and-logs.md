# Spec 016: OpenTelemetry traces and logs

**Status:** Approved (2026-11-04)
**Date:** 2026-11-04

## Problem

Quorum has Serilog on the console and nothing else. When an answer is slow or wrong nobody can see where the time went (the model, a tool, a delegation, the vector store) or what the model was asked and answered, how many tokens it used and how long it took. The operator of a self-hosted installation has no way to follow an answer from the request to the last model call, and no way to ship that to the tools the customer already uses.

## Scope

### In

- Traces and logs through OpenTelemetry (OTLP); metrics come later.
- Instrumentation compliant with the OpenTelemetry GenAI semantic conventions emitted by Microsoft.Extensions.AI (MEAI) and Microsoft Agent Framework (MAF).
- Serilog logs converge on OpenTelemetry (one OTLP pipeline, logs correlated with `TraceId` and `SpanId`).
- An optional compose profile with a collector and standard consultation tools (no application of our own).
- The `TraceId` on the tool audit entries, with a link to the trace.

### Out

- Metrics (a later spec).
- A custom viewer or demo application.
- Replacing the tool audit log (specs 013 and 015 D-8) and any audit of who changed what in the administrative area (D-12).
- Retention of the backends (theirs, D-4) and a spike: the profile is tried by hand, together (D-16).

## Decisions

### Where the data goes

- **D-1**: Export is **off until an OTLP endpoint is configured**. With no endpoint nothing leaves the installation, in line with the product promise of no outbound telemetry.
- **D-2**: The operator turns **content capture** on explicitly (prompts, answers, tool arguments and results in spans: the `EnableSensitiveData` of MEAI and MAF). It is off by default. When it is on, the arguments and results of tools go through the existing audit redactor and every attribute is cut at the standard limit (`OTEL_ATTRIBUTE_VALUE_LENGTH_LIMIT`). A secret typed in a message is the responsibility of whoever turns the switch on.
- **D-3**: OpenTelemetry is configured at **bootstrap only**: the standard `OTEL_*` variables and one `appsettings` section `Quorum:Telemetry` (content capture, administrative spans, trace link template); a change needs a restart. There is no admin screen and nothing in the database.
- **D-4**: **One** optional compose profile `observability` turns everything on (the compose file already has several profiles, so there are no more). It carries an **OpenTelemetry Collector**, always in the middle: Quorum exports to one OTLP endpoint and the collector forwards. The customer adds exporters by editing the collector file, without touching Quorum. The profile has standard tools only: **Grafana LGTM** (traces in Tempo, logs in Loki) and **OpenLIT** for the GenAI view (token, cost, duration, agent trees). Langfuse is documented as an optional destination behind the collector. The retention of the backends is theirs; the spec says nothing about it.
- **D-5**: The operator points the app at the collector with **two lines in `.env`**, documented: `COMPOSE_PROFILES` includes `observability`, and `OTEL_EXPORTER_OTLP_ENDPOINT` names the collector. Without the second line the app does not export (D-1). Headers for a remote endpoint come from `OTEL_EXPORTER_OTLP_HEADERS` and are never logged.
- **D-6**: The profile publishes the **interfaces on `127.0.0.1` only**; the OTLP ports are not published, they are reachable inside the compose network. The Grafana password and the OpenLIT key come from `.env`, with a warning at startup when they keep their defaults. Remote access goes through a reverse proxy.

### What is traced

- **D-7**: Quorum adds **no span that MEAI or MAF already emit** for chat calls, tool calls and agent runs. It adds a root span for the chat turn and **enriches** the spans of the frameworks with tags (D-9). The tool invoker runs inside the span of the tool call and writes its tags there. A sub-agent is a tool call that holds an agent run, so a delegation is already a nested subtree.
- **D-8**: Besides the root span and the tags, Quorum adds spans for **embeddings and queries to the vector stores** (kind, duration, result count) and for the **extraction of attachments** (format, duration, size, never content). The standard instrumentation of **Npgsql and HttpClient** is on. EF Core command text is not captured. The **time to first token** is an event on the span of the model call. There is no span per streamed chunk.
- **D-9**: The tags carry **GUIDs only**: tenant, user, conversation, assistant and agent, plus the agent slug (a technical identifier) and, for a tool call, its origin, decision and outcome. Never names, emails or texts. Logs carry the same identifiers (spec 013).
- **D-10**: The **administrative operations** (knowledge bases, agents, tools, assistants) get a span per request through the standard ASP.NET Core instrumentation. A **switch in `Quorum:Telemetry`** turns them on; it is off by default and only who configures the installation can change it (D-3). The pipeline of an answer is always traced when export is on.
- **D-11**: Traces are **not sampled by default** (100%); the operator reduces the volume with the standard `OTEL_TRACES_SAMPLER` variables.
- **D-12**: There is **no audit of who changed what** in the administrative area, in this spec or as a follow-up now: the span of the request (D-10) is enough.

### Outcome of a turn

Spec 004 D-8 already ends an answer as `Failed` when it reaches the rounds, the total time or the time of one call. The spans say the same, and say why.

- **D-13**: The root span of the turn carries `quorum.stop_reason` (`completed`, `cancelled`, `rounds`, `total_time` or `error`) and the counts `quorum.rounds` and `quorum.tool_calls`.
- **D-14**: A turn **cancelled by the user** has no error status. A turn that reaches the **rounds or the total time** has status **Error**, because the answer is `Failed`; so does a turn that fails on a model or tool error. A tool that fails or times out has its own span in Error with the Quorum error code; the turn is in Error only if the answer fails.
- **D-15**: To find **a tool called too many times** without capturing content, each tool-call span carries the round it ran in and a **hash of its arguments**; the same hash on several spans of one turn is the same call repeated.
- **D-16**: A sub-agent that fails (limit or error) puts **Error on its own run and on the delegation call**, with its `stop_reason`; the root agent receives the failure as a result and may answer, and the turn stays Ok if the answer completes. Parallel delegations of one round are siblings under it (spec 013 D-38).
- **D-17**: The **wait for a human confirmation** is a child span of the tool call (`quorum.confirmation`, with the decision), so that the duration of the tool is not the five minutes of someone deciding.

### Logs, failures and the audit

- **D-18**: Serilog stays the logger. The **console sink is always on**; a **Serilog OpenTelemetry sink** (the official one) sends the same events by OTLP when an endpoint is configured, with `TraceId` and `SpanId`. No sub-agent text at information level (spec 013).
- **D-19**: Telemetry **never blocks and never fails an answer**. Export is asynchronous with a bounded queue; when the collector or the backend is down, data is dropped and one warning (not one per span) goes to the console log.
- **D-20**: Each **tool audit entry stores the `TraceId`** (nullable, 32 hexadecimal characters; one column, one migration). It is empty when no trace existed (export off) and for older entries. It lives and goes with the entry (same retention).
- **D-21**: `Quorum:Telemetry:TraceUrlTemplate` is an absolute `http(s)` URL with the placeholder `{traceId}` (for example a Grafana Explore link). The audit API returns, for each entry with a `TraceId`, a ready `traceUrl`; the page shows the link. Without a template there is no link; the `TraceId` is shown anyway. The template is checked **at startup only when export is on** (D-1): with no endpoint there are no `TraceId`s to link, so there is nothing to check and no warning. A template that is not absolute, not `http(s)` or lacks `{traceId}` **does not stop the startup**: it is ignored and **one warning** at startup says which rule it breaks, without printing the template (it may hold a token). The sign is the warning and an audit page with the `TraceId` but no link; the operator guide says so.

### Names and testing

- **D-22**: The resource is `service.name=quorum` with the version computed by MinVer; instances are told apart with the standard `OTEL_RESOURCE_ATTRIBUTES`. Quorum has **one activity source per module** (`Quorum.Agents`, `Quorum.Conversations`, `Quorum.KnowledgeBases`); the sources of MEAI and MAF are registered by their names.
- **D-23**: The integration tests check the tree of spans and the tags with an **in-memory exporter** (the database stays on Testcontainers). The compose profile is tried by hand, together, in the browser; there is no spike and no automatic test of Grafana or OpenLIT.

## Domain Model

- `ToolAuditEntry` gains `TraceId` (D-20). Nothing else is stored: spans and logs live in the backend, not in the platform database.
- Configuration, read at startup: `Quorum:Telemetry:CaptureContent` (default `false`), `Quorum:Telemetry:AdminRequests` (default `false`), `Quorum:Telemetry:TraceUrlTemplate` (default empty), plus the standard `OTEL_*` variables.

## API Contract

No new endpoint. `GET /api/tools/audit` (spec 004) items gain `traceId` (string or null) and `traceUrl` (string or null, D-21). No other response changes.

## Authorization

Whoever can read the audit (spec 004) sees the `TraceId` and the link; the traces themselves are protected by the backend the operator chose. Quorum has no endpoint that serves telemetry, and the interfaces of the profile listen on localhost (D-6).

## Edge Cases & Failure Modes

- **Export off**: no listener, so no activity: the audit `TraceId` is null (D-20); logs go to the console only.
- **Collector down at start or later**: answers are unaffected, data is dropped, one warning (D-19).
- **Streaming**: the root span lasts the whole answer; a client that disconnects ends it as `cancelled` (D-14). A regeneration is a new turn with its own trace; the conversation GUID ties them.
- **Human confirmation**: the wait is its own span (D-17).
- **Parallel delegations**: siblings under the round, each with its own run (D-16).
- **Content capture on**: tool arguments and results are redacted and cut (D-2); the text of messages is cut but not redacted.
- **Invalid trace link template** (not absolute, not http(s), or no `{traceId}`): ignored with one warning at startup when export is on, nothing when it is off (D-21).
- **Cardinality**: the argument hash and the GUIDs are attributes of spans, not metric labels, since there are no metrics in this spec.

## Non-Functional Requirements

- With export on and no sampling, the instrumentation adds no perceptible delay to an answer; the export path never runs on the request path (D-19).
- Volume: a turn produces a handful of spans plus one per tool call, delegation, embedding and vector query; no span per chunk (D-8).
- The profile suits the same small server as the app: Grafana LGTM and OpenLIT run beside it only when the operator asks (D-4).

## Integrations

- OpenTelemetry Collector, Grafana LGTM (Tempo, Loki) and OpenLIT in the profile; Langfuse documented as an optional destination.
- MEAI and MAF: their instrumentation sits on the pipeline `ChatClientAgent` → `FunctionInvokingChatClient` → context guard → our model adapter; since the adapter is ours it must return the model and the token usage the instrumentation reads.
- Serilog OpenTelemetry sink; ASP.NET Core, Npgsql and HttpClient instrumentation.
- `docs/` gains an operator guide for the profile and the settings.

## Acceptance Criteria

- **AC-1** Given no `OTEL_EXPORTER_OTLP_ENDPOINT`, When an answer is produced, Then nothing is exported and the audit entries of its tool calls have no `TraceId`.
- **AC-2** Given an endpoint, When an answer with one model call is produced, Then one trace holds the root span of the turn and the span of the model call, with model, duration and token usage.
- **AC-3** Given an answer with a tool call, Then the span of the tool call is a child of the model round, carries origin, decision and outcome, and its audit entry stores the `TraceId` of the trace.
- **AC-4** Given an answer with a delegation, Then the subtree of the delegation call holds the run of the sub-agent and its model calls, and the tags carry the agent slug and the delegation call id.
- **AC-5** Given two delegations in one round with a limit of 2, Then they are siblings and overlap in time.
- **AC-6** Given content capture off (default), Then no span or log holds a prompt, an answer, a tool argument or a tool result.
- **AC-7** Given content capture on, Then tool arguments and results are redacted as in the audit and no attribute exceeds the length limit.
- **AC-8** Given an answer cancelled by the user, Then the root span has no error status and `quorum.stop_reason` is `cancelled`.
- **AC-9** Given an answer stopped by the rounds limit, Then the root span is in Error, `quorum.stop_reason` is `rounds` and `quorum.rounds` equals the limit.
- **AC-10** Given an answer stopped by the total time, Then the root span is in Error and `quorum.stop_reason` is `total_time`.
- **AC-11** Given a model that calls the same tool with the same arguments three times in a turn, Then the three spans carry the same argument hash and different rounds; their arguments are not in the spans.
- **AC-12** Given a tool that times out, Then its span is in Error with the Quorum error code and the turn is in Error only if the answer fails.
- **AC-13** Given a sub-agent that reaches its limit and a root agent that answers anyway, Then the sub-agent run and the delegation call are in Error and the turn is not.
- **AC-14** Given a tool that waits for a confirmation, Then a child span `quorum.confirmation` holds the wait and the decision, and the tool span's own work is not counted in it.
- **AC-15** Given a question that searches a knowledge base, Then the traces hold the embedding span and the query span with the kind of vector store and the result count, and no query text.
- **AC-16** Given an attachment, Then a span holds its format, size and extraction time, and no content.
- **AC-17** Given `Quorum:Telemetry:AdminRequests` off (default), Then a request to create an agent has no span of its own; when on, it has one with route, status and tenant.
- **AC-18** Given an endpoint, Then Serilog events reach OTLP with the `TraceId` and `SpanId` of the span they were written in, and the console still prints them.
- **AC-19** Given an unreachable collector, When answers are produced, Then they complete normally, the console holds one warning, and no log per dropped span.
- **AC-20** Given a `TraceUrlTemplate` and an audit entry with a `TraceId`, Then the API returns the `traceUrl` with the id in place of `{traceId}` and the page shows the link; without a template, only the `TraceId`.
- **AC-21** Given `docker compose --profile observability up` and the two lines of D-5, Then Grafana and OpenLIT answer on `127.0.0.1`, no OTLP port is published, and a chat turn shows up as a trace in both (tried by hand).
- **AC-22** Given the defaults of the Grafana password or the OpenLIT key with the profile on, Then startup logs a warning.
- **AC-23** Given export on and a `TraceUrlTemplate` that is relative, uses another scheme or lacks `{traceId}`, When the app starts, Then it starts, logs one warning naming the broken rule without the template, and the audit API returns a null `traceUrl` with the `traceId` still present.
- **AC-24** Given export off and the same invalid template, When the app starts, Then it logs no warning about the template.

## Deferred Decisions

| Decision | Fallback chosen now | Revisit when |
|---|---|---|
| Metrics (token counters, latency histograms, tool call rates) | None in this spec; the spans carry the numbers | A spec for metrics and alerts |
| Langfuse as a profile of its own | Documented as an optional destination behind the collector | A customer needs cost and quality analysis |
| Buffering telemetry on disk when the collector is down | Drop, with one warning (D-19) | Loss of traces proves costly |

## Open Questions

None.

## Amendments

### 2026-11-05: no bundled backend (replaces D-4, D-5 and D-6; AC-21 and AC-22 no longer apply)

Quorum does not carry an observability profile, a collector or a viewer in its compose file. The backend is any OTLP backend that the
operator runs on its own, like LiteLLM, so that one installation can serve every app and not only Quorum. What stays from D-1: export is
off until an OTLP endpoint is set, now with `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_PROTOCOL` and `OTEL_EXPORTER_OTLP_HEADERS`
passed by the compose file; headers are never logged. `docs/observability.md` explains the setup with OpenObserve as the example.
The reason: Grafana with Tempo and Loki, tried on the real stack, was not what the team wanted to read traces and logs with; one
backend for traces and logs in a single interface, run once for all the apps, is.
