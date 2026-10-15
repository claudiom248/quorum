# Spec 001: First Run and First Response

**Status:** Approved (2026-10-15)
**Date:** 2026-10-15

## Problem
A small company (or the founder as a tester) must be able to go from `docker compose up` to a first response from its own local model in a few minutes, without DevOps skills. This is the product's "first fifteen minutes" and its main differentiator.

## Scope
### In
- Startup with `docker compose up` (Postgres + app); migrations applied at startup.
- Setup mode with a setup token printed to the logs.
- First-run wizard: creation of the local administrator; configuration of the OpenAI-compatible model endpoint (URL, model name, optional API key, context budget) with a connection test and the list of available models; default system prompt shown and editable.
- Default tenant created automatically by the setup, without asking; not visible in the UI until multi-tenancy is enabled (enabling it is out of scope). `TenantId` is present in all data from the start.
- Setup mode closes when the wizard completes.
- Administrator login and logout.
- Chat with streamed responses; conversation persisted.
- Conversations: create, send messages and continue; list and reopen; rename; delete; stop and regenerate a response.
- Minimal settings page: same fields as the wizard for model and system prompt, with a connection test.
- Backend (API) **and** a minimal UI for wizard, login, chat and settings, in **Blazor WebAssembly** served by the Host, which uses the public API exclusively.

### Out
- License key (see Deferred Decisions).
- RAG and vector store, web search, OIDC, multi-agent presets, graph editor.
- Users other than the administrator; user management; password recovery.
- Enabling multi-tenancy.
- Read-only mode when a trial expires.
- API keys for external clients and an OpenAI-compatible endpoint exposed by Quorum.
- Automatic history summarization; multiple variants of a response.

## Domain Model
Every application entity carries `TenantId`.

| Entity | Module | Main fields | Lifecycle |
|---|---|---|---|
| **Tenant** | Tenancy | Id, Name ("Default"), CreatedAt | Created by the setup; not modified in this spec |
| **User** | Tenancy | Id, TenantId, Email, DisplayName, PasswordHash, Role (`Admin`), CreatedAt | Admin created by the setup |
| **SetupState** | Tenancy | IsCompleted, CompletedAt | Installation singleton; becomes completed exactly once, atomically |
| **Assistant** | Agents | Id, TenantId, Name, SystemPrompt, ModelEndpointId | Default assistant created by the setup; system prompt editable from wizard and settings |
| **ModelEndpoint** | Agents | Id, TenantId, BaseUrl, ModelName, ApiKey (encrypted), ContextTokenBudget (default 4096), UpdatedAt | Created by the wizard; editable in settings |
| **Conversation** | Conversations | Id, TenantId, UserId, AssistantId, Title, CreatedAt, UpdatedAt | Created on the first message; renamable; **hard delete** together with its messages |
| **Message** | Conversations | Id, ConversationId, Role (`User`/`Assistant`), Content, Status (`Completed`/`Interrupted`/`Failed`), CreatedAt | User messages are always `Completed`; a partial response is stored as `Interrupted` or `Failed`; the last response can be replaced by regeneration |

- **Setup token**: not persisted. Generated at every startup while `SetupState.IsCompleted = false`, kept in memory only, printed to the logs, invalid as soon as the setup completes.
- **Title**: first words of the user's first message, at most 60 characters, truncated at a word boundary.
- **Context sent to the model**: system prompt + the most recent messages that fit in `ContextTokenBudget`; older messages stay stored but are not sent. No summarization.
- **Model API key**: encrypted at rest with the master key (outside the database); never returned by the API (only "set/not set").

## API Contract
Errors use ProblemDetails. All routes are under `/api`.

| Verb | Route | Request | Response | Errors |
|---|---|---|---|---|
| GET | `/setup/status` | - | `{ isSetupMode, defaultSystemPrompt }` (`defaultSystemPrompt` only in setup mode, `null` afterwards) | - |
| POST | `/setup/verify-token` | `{ token }` | 204 | 401 wrong token; 404 setup completed |
| POST | `/setup/model/test` | token + `{ baseUrl, apiKey? }` | `{ reachable, models[] }` | 401; 404 setup completed; 422 endpoint unreachable |
| POST | `/setup/complete` | token + `{ admin: { email, displayName, password }, model: { baseUrl, modelName, apiKey?, contextTokenBudget }, systemPrompt }` | 204 + session cookie | 400 validation; 401; 409 setup already completed |
| POST | `/auth/login` | `{ email, password }` | 204 + cookie | 401 credentials; 423 account locked |
| POST | `/auth/logout` | - | 204 | - |
| GET | `/auth/me` | - | `{ email, displayName, role }` | 401 |
| GET | `/settings/assistant` | - | `{ baseUrl, modelName, hasApiKey, contextTokenBudget, systemPrompt }` | 401 |
| PUT | `/settings/assistant` | `{ baseUrl, modelName, apiKey?, contextTokenBudget, systemPrompt }` | 204 | 400; 401 |
| POST | `/settings/assistant/test` | `{ baseUrl, apiKey? }` | `{ reachable, models[] }` | 401; 422 |
| GET | `/conversations` | - | `[{ id, title, updatedAt }]` (sorted by `updatedAt` desc) | 401 |
| POST | `/conversations` | - | `{ id }` | 401 |
| GET | `/conversations/{id}` | - | `{ id, title, messages[] }` | 401; 404 |
| PATCH | `/conversations/{id}` | `{ title }` | 204 | 400; 401; 404 |
| DELETE | `/conversations/{id}` | - | 204 | 401; 404 |
| POST | `/conversations/{id}/messages` | `{ content }` | SSE stream of tokens, final event with status | 400; 401; 404; 409 response already in progress |
| POST | `/conversations/{id}/regenerate` | - | SSE stream that replaces the last response | 401; 404; 409 |

- The setup token travels in a dedicated header (e.g. `X-Setup-Token`).
- The user stops a response by closing the stream (request cancellation); the server stores the partial response as `Interrupted`.
- Conversations of another user or tenant return 404 (not 403), so their existence is not revealed.

## Authorization
- **Web UI**: HttpOnly, Secure, SameSite authentication cookie (same origin as the API); no token reachable from JavaScript. Antiforgery protection on state-changing calls.
- **Admin password**: at least 12 characters, no forced complexity rules; temporary lockout after 5 failed attempts.
- **Roles**: only `Admin` exists in this spec and can access chat and settings. Each conversation is visible and editable only by its user, within its tenant.
- **Setup API**: reachable only in setup mode and only with a valid setup token; unavailable once the setup completes.

## Edge Cases & Failure Modes
- **Interrupted streaming** (model error, timeout, user stop): the text received so far is stored with status `Interrupted` (stop/cancellation) or `Failed` (error/timeout); the UI shows it with the option to regenerate.
- **Regeneration**: replaces the assistant's last response (including interrupted or failed ones); no variants are kept.
- **Message sent while a response is in progress in the same conversation**: rejected with 409.
- **Model unreachable during chat**: the user message is stored; the response is `Failed` with a readable error in the UI.
- **Model unreachable during setup**: the connection test fails with a clear message; the setup can still be completed with a warning (the model may still be downloading or turned off) and the admin fixes it from the settings.
- **Concurrent setup**: completion is atomic; the first one wins, the second receives 409.
- **Restart during setup**: a new token in the logs; the old one is no longer valid; no partial data is persisted before `/setup/complete`.
- **Deletion**: explicit confirmation in the UI; permanent deletion of the conversation and its messages. Deleting during an in-progress response cancels the response.
- **Message longer than the context budget** (together with the system prompt): rejected with 400 stating the maximum length.
- **Token estimation**: characters / 4, with a 20% safety margin on the budget; replaceable later without changing the API.
- **Database unreachable at startup**: the app waits (the compose file uses the Postgres healthcheck), then exits with a clear error in the logs if it cannot connect.

## Non-Functional Requirements
- No performance targets in this spec: first-run time, overhead before the first token and concurrent users are measured, and targets are set in a later spec.
- No outbound telemetry.
- Structured logs; the setup token is the only secret ever printed to the logs, never passwords or API keys.
- Streaming via **Server-Sent Events**.

## Integrations
- **OpenAI-compatible model endpoint**: `GET /v1/models` for the test and the model list; `POST /v1/chat/completions` with `stream: true` for chat. Verified at least with Ollama.
- **PostgreSQL**: platform database.
- No events published externally; any integration events between modules stay internal.

## Acceptance Criteria
**Setup**
- **AC-1** Given a new installation, When the app starts, Then it applies the migrations, enters setup mode and prints a setup token to the logs.
- **AC-1b** Given setup mode, When the status is requested, Then it includes the default system prompt; after setup it is null.
- **AC-2** Given setup mode, When a setup API is called without a token or with a wrong token, Then it returns 401.
- **AC-3** Given a valid token and a reachable Ollama endpoint, When the connection is tested, Then it returns `reachable: true` with the list of models.
- **AC-4** Given an unreachable endpoint, When the connection is tested, Then it returns 422 with an understandable message.
- **AC-4b** Given an unreachable endpoint, When the setup is completed with valid data, Then the setup succeeds and the UI shows a warning asking to check the model from the settings.
- **AC-5** Given valid data, When the setup is completed, Then the default tenant, the admin, the assistant with its system prompt and the model configuration (encrypted API key) are created, setup mode ends and the admin is authenticated.
- **AC-6** Given a completed setup, When any setup API is called, Then it returns 404 (409 for `/setup/complete`).
- **AC-7** Given two concurrent completion requests, When they arrive together, Then only one succeeds and the other receives 409.
- **AC-8** Given an admin password shorter than 12 characters, When the setup is completed, Then it returns 400.
- **AC-9** Given the app restarted during setup, When it starts again, Then it prints a new token and the previous one is no longer valid.

**Authentication**
- **AC-10** Given the admin, When logging in with correct credentials, Then an HttpOnly cookie is issued and `/auth/me` returns the admin's data.
- **AC-11** Given 5 failed login attempts, When logging in again, Then it returns 423 even with the correct password, until the lockout expires.
- **AC-12** Given an unauthenticated user, When calling chat or settings APIs, Then it returns 401.

**Chat**
- **AC-13** Given an authenticated admin, When sending the first message in a new conversation, Then the response is streamed via SSE and both message and response are stored as `Completed`.
- **AC-14** Given the first message, When the conversation is created, Then its title is the first words of the message, at most 60 characters, truncated at a word boundary.
- **AC-15** Given a long conversation, When a message is sent, Then the model receives the system prompt and only the most recent messages that fit in the context budget.
- **AC-15b** Given a message that on its own (with the system prompt) exceeds the estimated context budget, When it is sent, Then it returns 400 stating the maximum length and nothing is stored.
- **AC-16** Given a response in progress, When the user stops it, Then the text received is stored with status `Interrupted`.
- **AC-17** Given the model failing during streaming, When the response breaks off, Then the text received is stored with status `Failed` and the UI shows the error.
- **AC-18** Given any last response, When regeneration is requested, Then the new response replaces the previous one.
- **AC-19** Given a response in progress, When another message is sent in the same conversation, Then it returns 409.
- **AC-20** Given several conversations, When the list is requested, Then they are sorted by last update and can be reopened and continued.
- **AC-21** Given a conversation, When it is renamed, Then the new title is stored (validation: not empty, at most 60 characters).
- **AC-22** Given a conversation, When it is deleted, Then the conversation and its messages no longer exist in the database.
- **AC-23** Given a conversation of another user or tenant, When it is requested, Then it returns 404.

**Settings**
- **AC-24** Given the admin, When changing endpoint, model, budget or system prompt, Then subsequent messages use the new configuration.
- **AC-25** Given an API key is set, When the settings are read, Then the key is not returned (only `hasApiKey: true`).

**Installation**
- **AC-26** Given the images are already pulled, When running `docker compose up`, Then Postgres and the app start and the wizard is reachable from the browser.

## Deferred Decisions
| Decision | Fallback chosen now | Revisit trigger |
|---|---|---|
| License key verification | No license step; without a key the app runs as implicit Community (product rule in `product.md`); entering a key and enforcing limits are out of scope | Dedicated Licensing spec |
| Performance targets | No targets; measurement only | After the first measurements on reference hardware |

## Open Questions
_None._

## Amendments
| Date | Change |
|---|---|
| 2026-10-15 | `GET /setup/status` also returns `defaultSystemPrompt` (only in setup mode, `null` afterwards) so the wizard can show the default system prompt; added AC-1b. |
