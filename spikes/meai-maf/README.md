# Spike: Microsoft.Extensions.AI / Agent Framework

Throwaway prototype behind `docs/spikes/meai-maf.md` (the report, in Italian). **Not production code and not part of `Quorum.slnx`**:
`Directory.Build.props` and `Directory.Packages.props` in this folder stop the repository's settings (central package management, MinVer,
warnings as errors) from applying, so `dotnet build` at the repository root never sees it.

## Run

```bash
cd spikes/meai-maf/Spike
dotnet build
dotnet run --no-build -- all          # x1..x5, no Ollama needed (scripted OpenAI-compatible server in-process)
SPIKE_NETTRACE=1 dotnet run --no-build -- all   # also lists every DNS lookup / socket connect / HTTP request the process made
dotnet run --no-build -- x6           # the recommended shape: own tool loop as an IChatClient middleware
dotnet run --no-build -- real qwen3.8:27b B     # against a real Ollama at http://localhost:11434/v1 (adapters: A0, A, B)
```

## Experiments

| Mode | Question of the report | What it shows |
|---|---|---|
| `x1` | Q1 | Reasoning (`reasoning`, `reasoning_content`) and tool-call fragments through three `IChatClient` adapters (`A0` plain Microsoft.Extensions.AI.OpenAI, `A` the same plus a raw-chunk wrapper, `B` an own client) |
| `x2` | Q2 | Every requirement of spec 004's tool layer through `FunctionInvokingChatClient` (`FunctionInvoker`, `DelegatingChatClient`, options) |
| `x3` | Q3 | Confirmation the MEAI/MAF way (run ends with `ToolApprovalRequestContent`, a new run resumes), with a serialised history and a serialised `AgentSession` |
| `x4` | Q4 | `ModelContextProtocol` client: our `HttpClient` with a `ConnectCallback` URL guard, redirects, static headers, declared capabilities, cancellation, `McpClientTool` as `AIFunction` |
| `x5` | Q5 | MAF sequential and concurrent orchestrations running agents that use our policy/audit pipeline |
| `x6` | Q2/Q3 | The same scenarios as `x2` with the loop of PR #30 re-expressed as a `DelegatingChatClient` (no `FunctionInvokingChatClient`, no side channel), also inside a MAF `ChatClientAgent` |
| `real` | Q1/Q2 | The `x2` pipeline against a real Ollama |

## Files

- `QuorumChatClient.cs`: adapter B, an `IChatClient` over the OpenAI-compatible wire format (what is left of `OpenAiChatStream` + `ToolCallAssembler`).
- `Adapters.cs`: adapter A, a wrapper that re-reads the raw OpenAI-SDK chunk for Ollama's `reasoning` field.
- `ToolPipeline.cs`: tool layer built on `FunctionInvokingChatClient` (`FunctionInvoker`, budget client, event pump).
- `ToolLoopChatClient.cs`: the tool layer as an own loop middleware, with a two-phase executor (prepare, then run).
- `FakeServer.cs`: scripted OpenAI-compatible server.
- `NetTrace.cs`: in-process listener for `System.Net.*` event sources.
