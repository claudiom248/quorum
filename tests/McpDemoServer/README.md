# MCP demo server

A small MCP server (Streamable HTTP, stateless) to try Quorum's MCP tools by hand, for example from the browser. It is not part of the
solution and is not shipped: it lives here only as a test aid.

```bash
dotnet run --project tests/McpDemoServer --urls http://0.0.0.0:3001
```

Register `http://host.docker.internal:3001/mcp` from the Quorum container (or `http://127.0.0.1:3001/mcp` when Quorum runs on the host).

| Tool | Behaviour | Good for |
|---|---|---|
| `get_weather` | Returns fixed demo weather for a city (read-only hint) | An `Auto` tool that succeeds |
| `add_note` | Pretends to save a note | A `Confirm` tool (confirmation dialog) |
| `huge_result` | Returns 50,000 characters | The "result too large" error (default limit 20,000) |
| `always_fails` | Throws an MCP error | The failed-call path |

New tools arrive as `PendingReview` with the `Confirm` policy: enable them and activate them on the assistant first.
