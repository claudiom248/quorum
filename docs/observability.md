# Observability: traces and logs of the answers

Quorum exports OpenTelemetry traces and logs (spec 016). Each answer is a trace: the turn, the model calls, the tool calls, the
delegations, the embeddings, the vector queries and the attachments. Nothing leaves the installation unless you turn export on.

Quorum does not ship a backend. It exports OTLP to any backend you run on its own, the way LiteLLM runs next to it: one
installation of the backend can serve every app on the machine, not only Quorum.

## Turn it on

Run an OTLP backend (see the example below), then in `.env`:

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=http://host.docker.internal:5080/api/default
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
OTEL_EXPORTER_OTLP_HEADERS=Authorization=Basic <base64 of user:password>
```

The endpoint and the protocol depend on the backend. `host.docker.internal` is how the container reaches a backend published on the
host. Headers go in `OTEL_EXPORTER_OTLP_HEADERS` and are never logged. The standard `OTEL_*` variables apply, among them
`OTEL_TRACES_SAMPLER` to reduce the volume.

Then restart Quorum:

```bash
docker compose up -d
```

Without `OTEL_EXPORTER_OTLP_ENDPOINT` Quorum exports nothing. Logs follow the general endpoint; a backend that takes the traces
signal only (`OTEL_EXPORTER_OTLP_TRACES_ENDPOINT`) gets no logs.

## Example: OpenObserve

OpenObserve shows traces and logs in one interface and keeps them on disk. A compose project of its own, next to Quorum's:

```yaml
services:
  openobserve:
    image: public.ecr.aws/zinclabs/openobserve:v0.70.0
    restart: unless-stopped
    ports:
      - "127.0.0.1:5080:5080"
    environment:
      ZO_ROOT_USER_EMAIL: ${ZO_ROOT_USER_EMAIL}
      ZO_ROOT_USER_PASSWORD: ${ZO_ROOT_USER_PASSWORD}
      ZO_DATA_DIR: /data
    volumes:
      - openobserve_data:/data

volumes:
  openobserve_data:
```

Use the open source image (`public.ecr.aws/zinclabs/openobserve`); the quick start of the project names the enterprise one. Set the
user and password in a `.env` beside the file. The endpoint above is then `http://host.docker.internal:5080/api/default` and the
header is `Authorization=Basic` followed by the base64 of `user:password`. Open `http://127.0.0.1:5080` and sign in.

To link each tool call of the audit log to its trace, set the template in `.env`. OpenObserve opens a trace only with its time range,
so the template has `{from}` and `{to}`:

```bash
QUORUM_TRACE_URL_TEMPLATE=http://127.0.0.1:5080/web/traces/trace-details?stream=default&trace_id={traceId}&from={from}&to={to}&org_identifier=default
```

Any other OTLP backend works the same way: set its endpoint, protocol and headers.

## What a trace holds

- `quorum.chat_turn`: the root span of an answer. Tags: `quorum.stop_reason` (`completed`, `cancelled`, `rounds`, `total_time`,
  `error`), `quorum.rounds`, `quorum.tool_calls`, and the identifiers (tenant, user, conversation, assistant, agent).
- The model spans and the agent spans come from the frameworks (Microsoft.Extensions.AI and Microsoft Agent Framework), with token usage.
- `execute_tool` spans: `quorum.tool.origin`, `quorum.tool.round`, `quorum.tool.arguments_hash` (the same hash on repeated
  calls, without the arguments), `quorum.tool.decision` and `quorum.tool.outcome`.
- `quorum.confirmation`: the wait for a person, as a child of the tool call.
- `quorum.embedding`, `quorum.vector_query`, `quorum.attachment_extraction`.
- `quorum.background.*`: work outside any request (the lookup warm-up and refresh, the audit retention), each a trace of its own.

## Content

Prompts, answers, tool arguments and results are **not** recorded by default. To record them, set
`Quorum__Telemetry__CaptureContent=true`. Tool arguments and results, and the messages that carry them again, are then redacted for
the usual sensitive keys and cut at `OTEL_ATTRIBUTE_VALUE_LENGTH_LIMIT`. Secrets typed in a message are not redacted: turn the switch
on only where that is acceptable. The SQL text and the details of the database connection are never exported.

## Other settings

| Setting | Default | Meaning |
|---|---|---|
| `Quorum__Telemetry__AdminRequests` | `false` | A span for each administrative request (agents, tools, knowledge bases). |
| `Quorum__Telemetry__TraceUrlTemplate` / `QUORUM_TRACE_URL_TEMPLATE` | empty | Link from the audit log to a trace. `{traceId}` is replaced; `{from}` and `{to}` too, when present: Unix time in microseconds, one hour before the start and one hour after the end of the call, for the backends that open a trace only with its time range. Must be an absolute `http` or `https` URL with `{traceId}`. |

A template that breaks a rule is ignored, and the startup log names the rule (not the template, which may hold a token).

## When the backend is down

Answers complete normally. Spans are dropped when the export queue is full, and the log shows a warning when the export fails, at most
once a minute.
