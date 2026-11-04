# Observability: traces of the answers

Quorum exports OpenTelemetry traces and logs (spec 016). Each answer is a trace: the turn, the model calls, the tool calls, the
delegations, the embeddings, the vector queries and the attachments. Nothing leaves the installation unless you turn export on.

## Turn it on with the bundled profile

In `.env`:

```bash
COMPOSE_PROFILES=observability
OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317
GRAFANA_ADMIN_PASSWORD=<a long password>
```

Then start the stack:

```bash
docker compose up -d --build
```

Grafana (Tempo for the traces, Loki for the logs) is at `http://127.0.0.1:3000`. Sign in as `GRAFANA_ADMIN_USER` (default `admin`)
with `GRAFANA_ADMIN_PASSWORD`; without that variable the password is `admin`, so set it. Anonymous access is off. Open Explore,
choose Tempo, and search for `quorum.chat_turn`. The audit log of tool calls can link each call to its trace when you set
`QUORUM_TRACE_URL_TEMPLATE` (see below).

The collector runs in the compose network. No OTLP port is published, and the interfaces listen on `127.0.0.1` only. For access from
other machines, use a reverse proxy with TLS.

## Use your own backend

Without the profile, set `OTEL_EXPORTER_OTLP_ENDPOINT` to your collector or to a backend that accepts OTLP. Headers for a remote endpoint go
in `OTEL_EXPORTER_OTLP_HEADERS` (they are never logged). The standard `OTEL_*` variables apply.

To add a destination to the bundled collector, edit `deploy/observability/otel-collector.yaml`: add an exporter and list it in the
`traces` and `logs` pipelines. Quorum does not change.

## What a trace holds

- `quorum.chat_turn`: the root span of an answer. Tags: `quorum.stop_reason` (`completed`, `cancelled`, `rounds`, `total_time`,
  `error`), `quorum.rounds`, `quorum.tool_calls`, and the identifiers (tenant, user, conversation, assistant, agent).
- The model spans and the agent spans come from the frameworks (Microsoft.Extensions.AI and Microsoft Agent Framework), with token usage.
- `execute_tool` spans: `quorum.tool.origin`, `quorum.tool.round`, `quorum.tool.arguments_hash` (the same hash on repeated
  calls, without the arguments), `quorum.tool.decision` and `quorum.tool.outcome`.
- `quorum.confirmation`: the wait for a person, as a child of the tool call.
- `quorum.embedding`, `quorum.vector_query`, `quorum.attachment_extraction`.

## Content

Prompts, answers, tool arguments and results are **not** recorded by default. To record them, set
`Quorum__Telemetry__CaptureContent=true`. Tool arguments and results are then redacted for the usual sensitive keys and cut at
`OTEL_ATTRIBUTE_VALUE_LENGTH_LIMIT`. Secrets typed in a message are not redacted: turn the switch on only where that is acceptable.

## Other settings

| Setting | Default | Meaning |
|---|---|---|
| `Quorum__Telemetry__AdminRequests` | `false` | A span for each administrative request (agents, tools, knowledge bases). |
| `Quorum__Telemetry__TraceUrlTemplate` / `QUORUM_TRACE_URL_TEMPLATE` | empty | Link from the audit log to a trace; `{traceId}` is replaced. Must be an absolute `http` or `https` URL with `{traceId}`. |
| `OTEL_TRACES_SAMPLER` | always on | Reduce the volume with the standard sampler variables. |

A template that breaks a rule is ignored, and the startup log names the rule (not the template, which may hold a token).

## When the collector is down

Answers complete normally. Spans are dropped when the export queue is full, and the log shows one warning when the export first fails,
not one per span.
