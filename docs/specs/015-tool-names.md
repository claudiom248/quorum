# Spec 015: Tool names

**Status:** Approved (2026-11-03), implemented on `feat/ledger-styles`
**Date:** 2026-11-03

Amends: spec 003 (D-4, rag tool name), spec 004 (D-16, AC-20), spec 005 and 006 (web tool names), spec 007 (D-12, A1), spec 012 (D-8) and spec 013 (D-18, AC-26).

## Problem

The model and the person read different names for one tool. The admin lists show `search_contracts` while the model is offered `rag__search_contracts`; a web fetch tool the admin calls `web_fetch` becomes `web_fetch__web_fetch` for the model. The origin prefix (`builtin__`, `rag__`, `lookup__`, `web_search__`, `web_fetch__`) was a namespace for the model, and it reached the screens. The name rule also allowed lower-case letters only, which rejected a name such as `WebFetch` without a reason: the model APIs accept `[a-zA-Z0-9_-]{1,64}`.

## Decisions

- **D-1**: A tool made in Quorum (built-in, rag, lookup, web search, web fetch) is called by **its own name**, by the model and in every screen: lists, drawers, notices, the tool picker of an agent, the ledger and the confirmation. There is no origin prefix. This replaces spec 004 D-16 for these origins.
- **D-2**: An **MCP tool** keeps `<server>__<tool>` for the model (spec 004 D-16): it comes from outside, and two servers may offer a tool of one name. People see the same string with the separator drawn as `server · tool`. This is the only place where the two differ.
- **D-3**: The name of a tool made in Quorum is 1 to 40 characters: **letters of any case**, digits, `-` and `_`, starting with a letter or a digit, and **never two underscores in a row**, because `__` is how an MCP tool carries its server. A space is not allowed (the model APIs refuse it). Names already stored (lower-case) stay valid. The slug of an agent keeps its own rule (spec 013 D-18).
- **D-4**: The operations of a **lookup tool** are named with the operation as a suffix: `<name>_entries`, `<name>_documents`, `<name>_count` (full manifest, spec 007 A1) and `<name>_describe`, `<name>_query` (lean manifest, spec 012 D-8). They replace `lookup_entries__<name>` and its sisters.
- **D-5**: The names the model sees share **one space per tenant**: the tools of every Quorum origin (built-in names included), the functions of the lookup tools (D-4) and the slugs of the agents, which name the delegation tools (spec 013 D-18). Two of them may not be equal, **whatever the case**, because the model could not tell them apart. This is checked when a tool or an agent is saved: the answer is `409 Conflict` with code `tool.name_taken`. The per-kind rules stay (`rag.name_taken`, `lookup_tool.name_taken`, `web_search.name_taken`, `web_fetch.name_taken`).
- **D-6**: Data saved before this spec may hold two tools that the model would take for one (for example a rag and a lookup tool of the same name). When the tools of an agent are loaded for an answer, the oldest keeps the name and the other is not offered, so the request never carries two functions of one name.
- **D-7**: The name no longer says what a tool is, so the API says it: the tool listing of an agent (`GET /api/agents/{id}/tools`) carries `origin` (`builtin`, `rag`, `lookup`, `web_search`, `web_fetch` or `mcp`) and `server` (MCP tools), and the `tool_call` event carries `isDelegation`, because a sub-agent is called by its slug, as a tool is by its name. The admin groups the tools of an agent as the menu does: built-in, search tools (rag and lookup), web tools (fetch and search), then one group per MCP server.
- **D-8**: The **audit log** is technical and unchanged: each entry records the kind of tool as its server (`rag`, `lookup`, `web_search`, `web_fetch`, `agent`; none for a built-in tool) and the tool name. Its page shows the technical label (`rag__<name>`).
- **D-9**: The help text of the forms says what the model reads: the same name, and for a lookup tool the name followed by the operation.
- **D-10**: A **built-in tool can be renamed** by the administrator like any other tool, on the Built-in tools page. Its default is the name it comes with (`calculator`, `current_datetime`, `ask_user`). The new name follows D-3 and D-5 (one name space), and the old one is free for another tool once the tool is renamed. The row keeps the catalog name as its identity (it ties the row to the code that runs the tool, to the provisioning and to the audit log), so the new name is stored apart (`tools.CustomName`) and an empty name, or the default one, gives the default back. `PUT /api/tools/{id}` takes an optional `name` (omitted keeps it); the tool listing returns the name in use as `name` and the default one as `defaultName`. The audit log keeps recording the default name (D-8). The notice that tells the model to use `ask_user` names the tool as the model is offered it. There is no alias: the name in use is the name everywhere.
- **D-11**: The Built-in tools page has no search and no filter of the tools to review: the built-in tools are few and are never pending review.

## Acceptance Criteria

- **AC-1** Given a built-in, rag, lookup, web search or web fetch tool of the agent, When the request to the model is built, Then the tool is offered by its own name, with no prefix.
- **AC-2** Given an MCP tool of the server `files` named `read`, When the request is built, Then the model is offered `files__read`, and the screens show `files · read`.
- **AC-3** Given a lookup tool `contracts`, When it is offered, Then its functions are `contracts_entries`, `contracts_documents` and `contracts_count` (full), or `contracts_describe` and `contracts_query` (lean).
- **AC-4** Given a name with capital letters such as `WebFetch`, When a web fetch tool is saved, Then it is accepted and the model is offered `WebFetch`.
- **AC-5** Given a name with a space, or with two underscores in a row, or longer than 40 characters, When a tool is saved, Then the request is rejected with a validation error.
- **AC-6** Given a rag tool `contracts` and a lookup tool of the same name, or a tool named `Contracts_entries` next to a lookup tool `contracts`, When the second is saved, Then it is refused with `409` and the code `tool.name_taken`.
- **AC-7** Given a tool and an agent whose slug equals the name of the tool, whatever the case, When the second is saved, Then it is refused with `409` (or a validation error on the slug of an agent).
- **AC-8** Given two stored tools that the model would take for one, When the tools of an agent are loaded, Then only the oldest is offered.
- **AC-9** Given the tool listing of an agent, Then every tool carries its `origin`, and an MCP tool its `server`; the `tool_call` event of a delegation carries `isDelegation`.
- **AC-10** Given an agent's tool list in the admin, Then the tools are grouped as in D-7 and each is shown by the name the model reads; inside the group of an MCP server a tool is shown by its own name, since the group already names the server.
- **AC-11** Given a built-in tool renamed `Calc`, Then every list (tools, agent tool picker, ledger, confirmation) shows `Calc`, the listing keeps `defaultName`, and an empty name or the default one gives the default back.
- **AC-12** Given a rename, Then the state and the policy of the tool are untouched, a name that breaks D-3 or D-5 is refused with `400`, and a tool that is not a built-in one is refused when sent a `name`.
- **AC-13** Given an active built-in tool renamed `Calc`, When the model is offered the tools, Then it sees `Calc`, a call to `Calc` runs the tool, and the `ask_user` notice names `ask_user` as it is offered.
- **AC-14** Given the Built-in tools page, Then it shows no search and no filter of the tools to review.

## Deferred Decisions

| Decision | Chosen fallback | Revisit trigger |
|---|---|---|
| Showing an MCP tool exactly as the model reads it (`files__read`) | Shown as `files · read` (D-2). | Feedback that people compare the screens with the model's requests |
| Renaming a tool without breaking prompts that quote its name | A prompt that names a tool by its old name is the admin's to update; the new names apply from the next answer. | A request for aliases |

## Open Questions

None.
