# Spec 018: Chat Assistants and Tool Selection

**Status:** Approved (2026-11-11)
**Date:** 2026-11-11

Builds on spec 013 (assistants and agents) and spec 004 (tools). The public OpenAI-style API (`/v1/chat/completions`, `/v1/responses`) stays a separate, later spec (`docs/deferred.md`); this one covers the application API that conversations already use and the admin UI.

## Problem

Today every conversation talks to the default assistant: `POST /api/conversations` takes no parameters and there is no way to choose. All tools of the root agent are always on, and only an administrator can change them. Two needs follow:

- Operators build several assistants and want to decide which of them can be talked to by people (or by applications using the API), and, per assistant, whether the people may tune its tools.
- The administration portal has a chat that must stay a **debug** tool: it must reach any assistant, show every tool, and swap the model, regardless of what is exposed to consumers.

How the end users will consume the platform (a dedicated chat UI, an API client) is not decided yet; the Chat page of the administration portal is the debug chat, not the user chat. This spec therefore defines the consumer API and the admin API, and changes the portal Chat page into the debug chat.

## Decisions

- **D-1**: An assistant has a flag **`exposed`**: only exposed assistants can be chosen by consumers.
- **D-2**: An assistant has a flag **`allowToolSelection`**. When off, consumers see no tools of the assistant at all (neither optional nor required ones) and cannot change them. When on, they see the tools of the root agent as in D-4 and D-5.
- **D-3**: Each tool attached to an agent (`agent_tools`) has a **mode**: `Required` (default) or `Optional`. The mode lives on the attachment, so it belongs to the agent and is shared by every assistant whose root it is; what differs between assistants is D-2.
- **D-4**: Required tools are **always shown, locked** (when D-2 is on): the consumer sees them and cannot turn them off. Optional tools are shown **on by default** and the consumer can turn them off, never on after they were off at creation (the selection is fixed, D-8).
- **D-5**: Only the tools of the **root agent** are selectable. Tools of sub-agents are never shown or selectable, and delegation edges are not tools.
- **D-6**: A tool is identified in the API by its **name as the model sees it** (`ToolPool.ExposedName`, unique per agent, spec 015), not by its id.
- **D-7**: The consumer chooses with `disabledTools`: the names of optional tools to turn off. Required, unknown or not-attached names are a validation error. Tools not listed stay on.
- **D-8**: For a consumer the selection is **fixed at creation**; changing it means a new conversation. The assistant too is chosen at creation and never changes.
- **D-9**: `assistantId` is **mandatory** in `POST /api/conversations`; omitting it is `400`. The "default assistant" remains only as the target of the first-run wizard and the settings facade (spec 013 D-35), not as a fallback of the conversation API.
- **D-10**: Consumer and admin routes are separate. Consumer routes are the current ones, filtered (D-11). Admin routes live under `/api/admin/...`, require `agents.manage` and are what the portal debug chat uses.
- **D-11**: Consumer routes: `GET /api/assistants` lists only exposed assistants with the public fields; `GET /api/assistants/{id}/tools` returns the tool list of D-4; `POST /api/conversations` refuses a non-exposed assistant. Everything that works on an existing conversation (messages, regenerate, attachments, tool confirmations and answers, rename, delete) stays on the **same routes** for consumer and debug conversations; the conversation itself carries its mode and overrides.
- **D-12**: A **debug conversation** is created only through `POST /api/admin/conversations`. It can use any assistant of the tenant, exposed or not, and carries optional overrides: a **model endpoint** (replacing the endpoint of the root agent for that conversation, inherited by sub-agents that inherit the root endpoint, spec 013 D-12) and the **tools to turn off**, including required ones. The admin tool list (`GET /api/admin/assistants/{id}/tools`) always returns every tool of the root agent with its mode, regardless of D-2.
- **D-13**: In a debug conversation the overrides **can be changed between messages** (`PATCH /api/admin/conversations/{id}/overrides`), to compare models and tool sets. Consumer conversations cannot be patched.
- **D-14**: The overrides are **stored on the conversation** and returned by `GET /api/conversations/{id}` (`debug: { modelEndpointId, disabledTools }`, absent for a consumer conversation). A consumer conversation also returns its `disabledTools`.
- **D-15**: Debug conversations are listed only by `GET /api/admin/conversations`; `GET /api/conversations` lists only consumer conversations.
- **D-16**: An override endpoint must exist in the tenant. If the root agent has tools (after the selection) or sub-agents and the override endpoint has `SupportsTools = false`, the request is refused (same rule as spec 013 D-19).
- **D-17**: **Live conversations are not interrupted** by later configuration changes. An assistant that stops being exposed lets existing conversations continue; at each message the **current** classification applies: a tool that became required cannot stay disabled (it is on again), and a tool detached from the agent disappears from the selection. The conversation keeps `disabledTools` as chosen; the runtime computes the effective set.
- **D-18**: The assistant of the first-run wizard / settings facade (spec 013 D-35) cannot be de-exposed (`409`), so there is always one assistant a consumer can start with. [Migration sets all existing assistants to exposed.]
- **D-19**: **Migration**: existing assistants are `exposed = true`, `allowToolSelection = false`; every existing `agent_tools` row is `Required`; new assistants default to `exposed = false`, `allowToolSelection = false`; new attachments default to `Required`. Installations behave as before the update.
- **D-20**: The admin UI gets: `exposed` and `allowToolSelection` on the assistant form; a `Required`/`Optional` selector per tool in the agent tools editor; on the Chat page, an assistant selector (all assistants), a model-endpoint override and a tool panel listing every root tool with a toggle (including required ones) that applies to the next message. The Chat page uses only the admin routes.
- **D-21**: The tool audit entries are unchanged; the debug overrides are traceable through the conversation (D-14) only.

## Scope

### In

- `exposed` and `allowToolSelection` on the assistant; `Required`/`Optional` mode on `agent_tools`; migration of existing data.
- Consumer routes: filtered assistant list, tool list of an assistant, `assistantId` and `disabledTools` on conversation creation, selection returned with the conversation.
- Admin routes: all assistants, full tool list, debug conversation creation and listing, override changes between messages.
- Runtime: the effective tool set of a turn (root tools minus disabled, required always on) and the endpoint override.
- Admin UI: assistant flags, tool mode editor, Chat page turned into the debug chat.

### Out

- The consumer chat UI and the public OpenAI-style API (`/v1/...`); per-application API keys.
- Selecting tools of sub-agents; selecting or overriding sub-agent models.
- Changing assistant or selection of a consumer conversation after creation.
- Persistent per-user tool preferences across conversations.
- Per-role visibility of assistants or tools (users and roles spec).
- Overriding the system prompt or any parameter other than the model endpoint.

## Domain Model

Module Agents (schema `agents`); module Conversations (schema `conversations`). Every entity keeps `TenantId` and its filter.

| Entity | Change | Notes |
|---|---|---|
| **Assistant** | + `Exposed` (bool), + `AllowToolSelection` (bool) | D-1, D-2, D-19 |
| **AgentTool** | + `Mode` (`Required` default, `Optional`) | D-3 |
| **Conversation** (Conversations module) | + `Mode` (`Consumer`, `Debug`), + `DisabledTools` (list of tool names), + `OverrideModelEndpointId?` (soft Guid, debug only) | D-8, D-12, D-14; `AssistantId` stays a soft reference |

- **Effective tools of a turn**: the active tools of the root agent, minus the names in `DisabledTools` that are optional (debug: any name), plus nothing else. Required tools are always on for consumer conversations.
- Tool names that no longer match an attached tool are ignored at run time (D-17).
- Built-in catalog tools such as `ask_user` are attached like any other tool (`agent_tools`, origin Builtin), so they are selectable and have a mode. Tools the runtime offers without an `agent_tools` row (`load_skill`, spec 017 D-4) are outside the selection and unaffected.

## API Contract

Errors as `ProblemDetails`, validation errors as `ValidationProblem`.

**Consumer** (authenticated users)

| Verb | Route | Request | Response | Errors |
|---|---|---|---|---|
| GET | `/api/assistants` | - | `[{ id, name, allowToolSelection }]`, exposed only | 401 |
| GET | `/api/assistants/{id}/tools` | - | `[{ name, description, mode }]`; empty when `allowToolSelection` is off | 401; 404 not exposed |
| POST | `/api/conversations` | `{ assistantId, disabledTools? }` | 201 `{ id }` | 400 missing `assistantId`, invalid `disabledTools`; 404 unknown or not exposed assistant; 400 tools sent while `allowToolSelection` is off |
| GET | `/api/conversations` | - | consumer conversations only | 401 |
| GET | `/api/conversations/{id}` | - | existing body + `assistantId`, `disabledTools`, `debug?` | 404 |

All other conversation routes (messages, regenerate, attachments, tool-call decision and answer, rename, delete) are unchanged and valid for both modes.

**Admin** (`agents.manage`)

| Verb | Route | Request | Response | Errors |
|---|---|---|---|---|
| GET | `/api/admin/assistants` | - | all assistants: `[{ id, name, exposed, allowToolSelection, rootAgentSlug }]` | 401; 403 |
| GET | `/api/admin/assistants/{id}/tools` | - | `[{ name, description, mode }]`, all root tools | 404 |
| POST | `/api/admin/conversations` | `{ assistantId, modelEndpointId?, disabledTools? }` | 201 `{ id }` | 400 (unknown tool name, D-16); 404 assistant or endpoint |
| GET | `/api/admin/conversations` | - | debug conversations of the caller | 401; 403 |
| PATCH | `/api/admin/conversations/{id}/overrides` | `{ modelEndpointId?, disabledTools }` | 204 | 400; 404 not a debug conversation |

Assistant and agent-tool writes (spec 013, spec 004) gain the new fields: `exposed`, `allowToolSelection` on `POST/PUT /api/assistants`; `mode` on the agent tool attachments (`PUT /api/agents/{id}/tools`).

## Authorization

- Consumer routes: any authenticated user of the tenant, as today; they only see exposed assistants and the tools allowed by D-2.
- Admin routes and the new assistant/tool fields: `agents.manage` (held by `Admin`, spec 013 D-26). A user without it gets `403` on admin routes.
- The consumer never learns that non-exposed assistants exist (`404`, not `403`).
- Tenant boundary: unchanged; assistants, endpoints and conversations are filtered by `TenantId`, and an override endpoint must belong to the tenant.

## Edge Cases & Failure Modes

- `disabledTools` with a required, unknown or not-attached name: `400` listing the names. Names are matched exactly as the model sees them; duplicates are collapsed.
- `disabledTools` sent for an assistant with `allowToolSelection` off: `400` (the consumer was never told the tools exist).
- Admin de-exposes an assistant or turns `allowToolSelection` off while consumer conversations exist: they continue (D-17); the stored `disabledTools` keep applying as long as the tools are optional.
- Admin changes an optional tool to required: running consumer conversations get the tool back on at the next message.
- Admin detaches a tool from the agent, or deletes the assistant: the selection ignores the missing name; deletion follows spec 013 D-36.
- A debug override endpoint deleted after use: spec 013 refuses deletion only for agents, so the conversation reports a clear error on the next message and the admin changes the override.
- Override endpoint without tool support while tools remain (D-16): refused at creation and at `PATCH`.
- Two requests changing overrides concurrently: last write wins; a turn in progress keeps the overrides it started with (spec 013 AC-25 spirit).
- The wizard assistant cannot be de-exposed (D-18): `409`.
- Selecting the same conversation id on the consumer route that belongs to a debug conversation of another user: `404` (user filter, as today).

## Non-Functional Requirements

- Listing assistants and tools is a read of a handful of rows per tenant; no new latency budget.
- The effective tool set is computed per turn from current data (D-17), no cache to invalidate.
- No new external services; no new events (no asynchronous cross-module flow). Conversations asks Agents for validation and tool names only through `Quorum.Modules.Agents.Contracts`, with `AssistantId` and tool names as plain values.
- The migrations are regular EF Core migrations of the Agents and Conversations modules.

## Integrations

- **Contracts**: `Quorum.Modules.Agents.Contracts` gains what Conversations needs to validate a creation (assistant exposed, tool names selectable, endpoint exists) and the turn request carries the effective disabled names and the endpoint override into `IAssistantChat`; Microsoft.Extensions.AI / Agent Framework types do not cross it.
- **Spec 013**: `IAssistantCatalog.GetDefaultAssistantIdAsync` stays for the wizard; the conversation API stops using it (D-9).
- **Spec 004 / 015**: tool names are the exposed names of spec 015.
- **Portal**: `Chat.razor` moves to the admin routes (D-20); the other pages call the new fields.

## Acceptance Criteria

- **AC-1** Given existing installations, When the migration runs, Then every assistant is exposed with tool selection off, every attached tool is required, and conversations answer as before.
- **AC-2** Given two assistants, one exposed and one not, When a consumer lists assistants, Then only the exposed one appears; asking for the other by id returns `404`.
- **AC-3** Given `POST /api/conversations` without `assistantId`, Then `400`.
- **AC-4** Given an assistant with `allowToolSelection` off, When a consumer asks for its tools, Then the list is empty, and sending `disabledTools` on creation returns `400`.
- **AC-5** Given an assistant with tool selection on, one required and one optional tool, When a consumer asks for its tools, Then both are returned with their mode, and tools of sub-agents are not.
- **AC-6** Given that assistant, When the consumer creates a conversation with the optional tool in `disabledTools`, Then the answer runs without that tool and with the required one.
- **AC-7** Given `disabledTools` naming a required, unknown or not-attached tool, When creating a conversation, Then `400` lists the names.
- **AC-8** Given a consumer conversation, When read, Then `disabledTools` is returned and `debug` is absent; there is no way to change them.
- **AC-9** Given an admin, When listing admin assistants, Then all assistants appear with `exposed` and `allowToolSelection`; a user without `agents.manage` gets `403` on every admin route.
- **AC-10** Given a non-exposed assistant, When an admin creates a debug conversation on it, Then it works and the conversation does not appear in `GET /api/conversations`, only in `GET /api/admin/conversations`.
- **AC-11** Given a debug conversation, When the admin turns off a required tool, Then the next answer runs without it.
- **AC-12** Given a debug conversation with an endpoint override, When the next message is sent, Then the root agent uses the override endpoint, and sub-agents that inherit the endpoint use it too.
- **AC-13** Given an override endpoint without tool support and an agent with tools, When creating or patching, Then the request is refused (D-16).
- **AC-14** Given a debug conversation, When the admin patches the overrides between messages, Then the next message uses the new ones and the conversation GET returns them.
- **AC-15** Given a consumer conversation, When `PATCH /api/admin/conversations/{id}/overrides` is called on it, Then `404`.
- **AC-16** Given a consumer conversation in progress, When the admin de-exposes the assistant, Then the conversation keeps answering.
- **AC-17** Given a consumer conversation with an optional tool disabled, When the admin changes that tool to required, Then the next message runs with the tool on.
- **AC-18** Given the wizard assistant, When an admin tries to de-expose it, Then `409`.
- **AC-19** Given the portal Chat page, When an admin opens it, Then it offers all assistants, the endpoint override and the toggle of every root tool (required ones included), and uses only admin routes.
- **AC-20** Given the assistant form and the agent tools editor, When saved, Then `exposed`, `allowToolSelection` and each tool's mode persist and are returned by the API.

## Deferred Decisions

| Decision | Chosen fallback | Revisit trigger |
|---|---|---|
| Per-role visibility of assistants and tools | All authenticated users of the tenant see all exposed assistants | Users and roles spec |
| Selecting tools of sub-agents and overriding their models | Not allowed (root only, D-5, D-12) | A use case that needs per-sub-agent control |
| Changing the selection of a consumer conversation after creation | Fixed (D-8) | Feedback from the consumer UI / API clients |
| Persisting the debug overrides in the tool audit | Conversation only (D-21) | Audit log UI spec |
| Public `/v1/...` API: how `model` maps to exposed assistants and how it carries `disabledTools` | Out of this spec | Public API spec |

## Open Questions

None.
