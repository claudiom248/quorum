# MCP tools: administrator guide

How to connect MCP servers to Quorum and decide what the assistants may run. Design decisions are in
[spec 004](specs/004-mcp-tools.md); this page is what an administrator needs.

## What Quorum speaks

Quorum connects to MCP servers over **Streamable HTTP** only. It never starts a process on its own: a stdio server (the usual
`npx ...` or `uvx ...` package) runs outside Quorum, behind a gateway you operate. See [stdio servers](#stdio-servers-through-a-gateway).

## The MCP servers page

Open **Tools → MCP servers** in the sidebar (it needs the administrator role). A server opens in a drawer on the right.

| Task | How |
|---|---|
| Register a server | **Add server**: name, URL, optional headers, who can use it. **Test connection** lists what the server offers before anything is saved |
| See its state | Each server shows *Available* or *Unavailable* (with the last error) and how many tools wait for review |
| Read its tools again | **Refresh** |
| Change it | **Edit**. A changed URL makes the server unavailable until you refresh it |
| Remove it | **Delete**: its tools leave every assistant. The audit log keeps their names |

### Headers

One per line, as `Name: value` (for example `Authorization: Bearer ...`). They are stored encrypted and never shown again: the page
only says that headers are stored. When you edit a server, leave the field empty to keep the stored ones, type new ones to
replace them, or tick **Remove the stored headers**. **Test connection** uses what you typed, not the stored headers.

Credentials belong in the headers, not in the URL: the URL is stored and shown as entered.

### Reviewing tools

A tool a server lists for the first time, or one whose name, description or input schema changed since you approved it, is
**pending review**: no model sees it. It also returns to the *Ask before running* policy.

For each tool pending review choose a policy and **Approve and enable**, or **Keep disabled**. After a refresh, tools that
vanished from the server are removed together with their activations on assistants.

Approving makes a tool available; it still has to be switched on for an assistant in **Settings → Tools**.

The hints a server sends about a tool (read-only, destructive...) are shown as *claimed by the server*. A server can be wrong or
dishonest, so they never change a policy.

### Policy

| Policy | Behaviour |
|---|---|
| Ask before running | The chat shows the tool and its full arguments and waits for the person's approval |
| Run without asking | The tool runs as soon as the model asks. Use it for tools you trust to be harmless |

The built-in tools (calculator, current date and time) are listed under **Tools → Built-in** and are managed the same way.

### Who can use a server

| Choice | Meaning |
|---|---|
| This tenant | Only this tenant. You may add other tenants by id; they see the server read-only |
| Chosen assistants | Only the assistants you tick |
| Every tenant | Every tenant of the installation sees the server and its enabled tools. Review and policy stay with the owner |

A server shared by another tenant appears marked *Shared with you* and cannot be changed here.

Warnings about too many active tools, or tool definitions that take too much of the context, appear in **Settings → Tools** next to the
tools of the assistant.

## Safety notes

- Quorum does not filter addresses and does not follow redirects. Whatever Quorum can reach from its host or container is
  reachable by the address you enter, cloud metadata included: register only servers you trust, and prefer a network where
  Quorum can reach only what it needs.
- A tool result is data for the model, not instructions, but a hostile document or result can still push a model to call tools.
  Keep *Ask before running* for anything with side effects.
- Every call is recorded in the audit log (`GET /api/tools/audit`). Argument values under keys such as `password`, `token` or
  `authorization` are redacted by name: this is a heuristic, not a guarantee.

## stdio servers through a gateway

Run the server behind a stdio-to-HTTP gateway as a separate container, and register the gateway's URL. Example with
[supergateway](https://github.com/supercorp-ai/supergateway) as a compose sidecar (check the flags against the version you pin):

```yaml
services:
  mcp-filesystem:
    image: node:22-slim
    command: >
      npx -y supergateway
      --stdio "npx -y @modelcontextprotocol/server-filesystem /data"
      --outputTransport streamableHttp
      --port 8000
    volumes:
      - ./shared-files:/data:ro
```

Register `http://mcp-filesystem:8000/mcp` in Quorum when both containers share a compose network. The gateway and the server it
runs have the access you give that container, which is the reason Quorum does not run them itself: give it only the volumes and
network it needs, read-only where possible.

## Trying it by hand

[`tests/McpDemoServer`](../tests/McpDemoServer/README.md) is a small server with a harmless tool, a tool that needs
confirmation, a huge result and a failing tool, to see each path in the chat.
