# Spec 010: Admin UI Redesign

**Status:** Approved (2026-10-24)
**Date:** 2026-10-23

## Problem
The web UI grew one feature at a time. Tools, Knowledge bases, Lookups and the Dictionary live on a few very long pages (`Tools.razor` alone is 577 lines and embeds the RAG and lookup sections, each of about 600 lines). The top bar has one entry per area, and Knowledge adds a second row of buttons. An edit appends a form at the bottom of the same page, so the admin loses the context of the row being edited. There is no search and no filter, although every component has a scope (Global, Tenant, Assistant) and a type. The initial setup and the Settings page duplicate the same model-connection form, and the setup asks for things that can be configured later.

This spec redesigns the whole signed-in web UI around a **sidebar shell**, **one page per component type**, **lists as data grids with a shared filter bar**, **drawers for create and edit**, and a **lighter first-run setup with a "Getting started" checklist**. MudBlazor stays.

## Scope
### In
- **Shell**: sidebar navigation grouped by area, replacing the top bar navigation (D-1, D-2).
- **List pages**, one per component type, built on one shared grid and filter bar (D-3 to D-6): MCP servers, RAG tools, Lookup tools, Built-in tools, Audit log, Knowledge bases, Lookups (manual and derived from knowledge bases), Dictionary.
- **Drawer** for create, edit and detail, synchronised with the URL; delete in a dialog (D-7 to D-9).
- **Audit log** page for tool calls, read-only, on the existing endpoint (D-21).
- **Inline actions** in tool tables: enabled switch, policy select, approve or keep disabled (D-10).
- **Shared components**: `ScopeBadge`, `ScopePicker` (replaces `ScopeEditor`), `ModelConnectionForm`, filter bar, list page scaffold.
- **Settings** as the assistant page with tabs Model, Behaviour and Tools (D-11).
- **Setup** reduced to two steps, plus the **Getting started** checklist (D-12, D-13).
- **API changes** needed by the filters: uniform scope in every list item, server-side filtering and cursor pagination on the large lists, a read-only tenants endpoint, the checklist state (D-14 to D-17).
- Responsive behaviour below 960 px (D-18).

### Out
- Changing the Chat page, its composer, or the theme palette.
- New features of the components themselves (no new tool kinds, no new KB kinds, no new fields). Existing forms keep their fields.
- Managing several assistants: the UI stays single-assistant (D-11). The agent filter exists but is driven by the assistants the API already returns.
- Tenant administration (creating or renaming tenants). Only a read-only list for filters and pickers.
- Roles beyond Admin. Non-admin users see Chat only plus what they see today (D-19).
- Replacing MudBlazor, a custom design system, or a visual rebrand.
- A flat "All tools" view across types (decided out; revisit if reviewing pending tools across servers proves painful).

## Decisions

| # | Decision |
|---|---|
| D-1 | **Sidebar shell.** The signed-in layout has a persistent sidebar (`MudDrawer` mini/persistent variant) and a slim top bar with the theme toggle, the user and sign out. The sidebar groups: **Chat**; **Tools** (MCP servers, RAG tools, Lookup tools, Built-in, Audit log); **Knowledge** (Knowledge bases, Lookups, Dictionary); **Assistant** (Settings); **Lab** (Editor lab) at the bottom. The current group and page are highlighted. |
| D-2 | **Pending-review badge.** The MCP servers entry shows the number of tools pending review; the count comes from the existing server list (`PendingReviewCount`). |
| D-3 | **One page per type, one scaffold.** Each list page is a thin component over a shared `ListPage` scaffold: title and description, primary action ("Add …"), filter bar, `MudDataGrid`, empty state, load error with retry. The monolithic `Tools.razor`, `RagToolsSection`, `LookupToolsSection`, `Knowledge*.razor` pages are split accordingly; no page exceeds what fits in review. |
| D-4 | **Filter bar.** Shared by all list pages, showing only the filters that apply to the list: **Scope** (Global, Tenant, Assistant, multi-select), **Tenant** (multi-select, matches the owner or a tenant the item is shared with; shown only when the installation has more than one tenant), **Agent** (matches items whose scope is Assistant and includes it, or that apply to every assistant), **Type** (the tool kind or KB kind where the list has one), **Status** (Available/Unavailable, Enabled/Disabled/To review), and **text search** on the name. Active filters show as removable chips and a "Clear filters" action. |
| D-5 | **Filters live in the URL.** The state of filters, search and sort is the query string (`?scope=assistant&tenant=…&q=…`), so a view can be shared, the back button works and a reload keeps it. |
| D-6 | **Uniform scope badge.** Every list shows scope with one `ScopeBadge`: "Global", "Tenant" (with "+N" when shared with other tenants), "Assistants: A, B" (collapsed to a count when long), "Shared with you" for items owned by another tenant. |
| D-7 | **Drawer for create, edit, detail.** Opening a row, or "Add …", opens a right-hand drawer over the list, which stays visible. The drawer is synchronised with the URL (`/tools/mcp/{id}`, `/tools/mcp/new`): links can be shared, back closes it and a reload reopens it. Large forms (RAG tool, Lookup tool) use the wide drawer size; the **knowledge base wizard** is a full page (`/knowledge/bases/new`, `/knowledge/bases/{id}` to edit), not a drawer; below 960 px every drawer is full screen. |
| D-8 | **Unsaved changes.** Closing a drawer whose form was modified (close button, Esc, back, backdrop) asks "Discard changes?"; an untouched form closes at once. |
| D-9 | **Delete in a dialog.** Delete asks for confirmation in a `MudDialog` that names the item and what removing it implies (today's texts are kept). Nothing is deleted inline in the page. |
| D-10 | **Inline tool actions.** In the grids of tools, each row has an **Enabled** switch and a **Policy** select (Ask before running, Run without asking). A tool pending review shows **Approve and enable** and **Keep disabled** in place of the switch, with the policy select. Rows that belong to a server shared by another tenant are read-only. The MCP server drawer lists its tools with the same actions and a filter for "To review". |
| D-11 | **Settings is the assistant page.** The page keeps the single assistant and has three tabs: **Model** (the shared `ModelConnectionForm` plus context budget), **Behaviour** (system prompt), **Tools** (the picker of the tools the assistant may call, with search, grouped by source, and the warnings that exist today). Each tab saves on its own and shows its own status. |
| D-12 | **Setup in two steps.** Step 1 **Admin**: setup token, name, email, password. Step 2 **Model**: the same `ModelConnectionForm` used by Settings (URL, API key, test connection, model, tool support, context budget). The system prompt is no longer asked at setup: the default one applies and is edited in Settings. Setup keeps the existing endpoints and behaviour (token verification, completion, the "model unreachable" warning leading to Settings). |
| D-13 | **Getting started checklist.** After setup the home page shows a checklist: Model connected (done by setup), Write the system prompt, Register an MCP server, Connect a knowledge base. An item is **completed by opening its page once** (Settings Behaviour tab, MCP servers, Knowledge bases); no configuration is required. The checklist can be dismissed. Its state is stored **per installation, on the server**: any admin's visit completes an item for everyone; dismissing hides it for everyone. It disappears when all items are done or it is dismissed. |
| D-14 | **Uniform scope in the API.** Every list item of tools and knowledge components exposes the same `ToolScope` (kind, tenant ids, assistant ids): KB list items (today a string), dictionary fields (string) and manual lookups (string plus tenant ids) are brought to it. Where a component supports only Global and Tenant, the assistant ids are empty. Existing request shapes keep working. |
| D-15 | **Server-side filtering and cursor pagination.** The lists that can reach thousands of items (tools of a server, dictionary fields, knowledge bases, manual lookups) accept `scope`, `tenant`, `assistant`, `type`, `status`, `q`, `limit` and `cursor`, and return `{ items, cursor }` like `LookupValuesPage`. The grid uses MudBlazor's server data mode. Lookup values keep their own paging. Small lists (MCP servers, RAG tools, Lookup tools, Built-in) may load whole and filter on the client; the API supports the same query for consistency. |
| D-16 | **Tenants endpoint.** A read-only `GET` returns the tenants (id, name) for admins. It feeds the tenant filter and the multi-select that replaces the typed ids of "Other tenants". |
| D-17 | **Checklist endpoint.** The Tenancy module (owner of the installation settings) stores the checklist items completed and the dismissed flag, with a `GET` and an idempotent `PUT` per item. |
| D-18 | **Responsive.** Below 960 px the sidebar collapses behind a menu button, drawers are full screen and grids scroll horizontally. |
| D-19 | **Authorization.** Tools, Knowledge and the Settings tabs stay admin-only, as the API already enforces. Non-admins do not see these entries; opening their URL shows a "You don't have access" page, not an empty page. The shell shows what the signed-in role may open. |
| D-20 | **Editor lab** is kept as a "Lab" entry at the bottom of the sidebar. |
| D-21 | **Audit log page.** Under Tools, a read-only grid of the tool-call audit log on the existing `GET /api/tools/audit`: time, tool, server, user, assistant, decision, outcome, duration, error code. Filters: period (from, to), tool, user, outcome, kept in the URL like the others (D-5). A row opens a drawer with the details (arguments and truncation flag, result size, source count, conversation). It keeps the endpoint's own `page`/`pageSize` paging with a total; the cursor of D-15 does not apply. Nothing changes or deletes an entry. |
| D-22 | **Lookups in both sections.** The Lookups page lists **every lookup**, manual and derived from knowledge bases (kind, field, unit, slices), each with its knowledge base, scope badge and a Manual/Derived filter. The detail of a knowledge base also lists its derived lookups, with the refresh actions that exist today. A derived lookup opens the same values view from either place; editing exists only for manual lookups, derived ones are managed through their knowledge base. |

## Domain Model
No new domain entities in the Agents and KnowledgeBases modules. The changes are in read models:
- `ToolScope` (existing) becomes the scope of every list item (D-14).
- **Tenancy**: a checklist state per installation: set of completed item keys (`model`, `prompt`, `tools`, `knowledge`) and `Dismissed`.
- UI-only models: `ListQuery` (filters, search, sort, cursor) with a round trip to and from the query string, and `ScopeFilter` helpers.

## API Contract
| Verb | Route | Request | Response | Errors |
|---|---|---|---|---|
| GET | knowledge-bases, dictionary, lookups (manual), tools of a server | `scope, tenant, assistant, type, status, q, limit, cursor` | `{ items[], cursor }` with `ToolScope` in every item | 400 invalid filter or cursor |
| GET | (tool servers, RAG tools, Lookup tools, tools) | same query, optional | list or page, scope uniform | 400 |
| GET | lookups (all, manual and derived) | same query | page of `{ id, name, kind (Manual/Derived), knowledgeBase?, scope }` | 400 |
| GET | tools/audit (existing) | `from, to, tool, userId, outcome, page, pageSize` | `{ items[], total }`, unchanged | 400 |
| GET | tenants | none | `[{ id, name }]` | 403 not admin |
| GET | setup checklist | none | `{ items: { key, done }[], dismissed }` | 403 |
| PUT | setup checklist item | `{ done: true }` | 204, idempotent | 403, 404 unknown key |
| PUT | setup checklist | `{ dismissed }` | 204 | 403 |

Routes are fixed in the plan, following the existing endpoint groups.

## Authorization
Unchanged. The new endpoints require an admin (`tools.manage` or `knowledge_base.manage` as applicable; the checklist and tenants require admin). Reads never expose the tenant ids of other tenants beyond what the existing detail endpoints already show to the owner.

## Edge Cases & Failure Modes
- **Stale cursor or changed filter**: a changed filter resets the cursor; an invalid cursor returns 400 and the grid reloads from the start.
- **Deep link to a deleted item**: the drawer shows "This item no longer exists" with a link back to the list.
- **Deep link to an item the user may not see**: same message; existence is not revealed.
- **Concurrent edit**: unchanged from today (last write wins); not addressed here.
- **Inline change fails** (tool state or policy): the control returns to the stored value and the page shows the problem, as the rows do today.
- **Filters that remove everything**: an empty state with "Clear filters", distinct from the "nothing registered yet" state with the primary action.
- **Single-tenant installation**: the tenant filter and the tenant picker are hidden; scope badges still render.
- **Checklist concurrent visits**: marking an item done is idempotent.

## Non-Functional Requirements
- Lists with thousands of rows: a page of 50 rows renders and a filter change answers in under 1 s on a typical installation; the grid never holds more than the loaded pages.
- Each list page and its drawer are independent components; shared components have unit tests (bUnit is not used today; see Deferred Decisions).
- Accessibility kept at today's level or better: drawers trap focus and return it to the row, grids are keyboard navigable, state is never conveyed by colour alone.
- No new UI library; MudBlazor version unchanged.

## Integrations
None external. Internal: the Agents and KnowledgeBases modules expose the query on their list endpoints; Tenancy exposes tenants and the checklist. Cross-module data (assistant names for the scope badge, tenant names) is composed by the UI from the public API, as today.

## Acceptance Criteria
- AC-1: Given a signed-in admin, when the app loads, then the sidebar shows Chat, Tools (4 entries), Knowledge (3 entries), Settings and Lab, and the top bar has no navigation buttons.
- AC-2: Given a non-admin, when the app loads, then Tools, Knowledge and Settings tabs for admin settings are absent, and opening `/tools/mcp` by URL shows "You don't have access".
- AC-3: Given the MCP servers page, when I open "Add server", then a drawer opens at `/tools/mcp/new` and the list stays visible; when I press back, then the drawer closes.
- AC-4: Given an edit drawer with changes, when I press Esc, then I am asked to discard them; given no changes, then it closes at once.
- AC-5: Given a list, when I filter by scope Assistant and an agent and type text, then the URL carries these filters, a reload keeps them and only matching rows show.
- AC-6: Given two tenants, when I filter by one tenant, then rows owned by it or shared with it show; given one tenant, then no tenant filter is shown.
- AC-7: Given more than one page of items, when I scroll or page, then the next page loads by cursor and the filters still apply; when a filter changes, then the cursor resets.
- AC-8: Given a tool pending review, when I click "Approve and enable", then it becomes enabled with the selected policy without leaving the page; given the change fails, then the row shows the stored value and the problem.
- AC-9: Given a server shared by another tenant, then its tools and the row actions are read-only and the badge says "Shared with you".
- AC-10: Given any list, then each item shows the same scope badge (Global, Tenant, Assistants) wherever it appears.
- AC-11: Given the delete action, then a dialog names the item and what removal implies, and nothing is removed before confirming.
- AC-12: Given the setup, then it has two steps; the model step is the same form as the Settings Model tab; completing it leads to the home with the checklist.
- AC-13: Given the checklist, when an admin opens the Behaviour tab, MCP servers and Knowledge bases once each, then all items are done and the checklist disappears for every admin; given I dismiss it, then it is hidden for every admin.
- AC-14: Given Settings, then I can save the model, the prompt and the tools separately, each tab with its own status.
- AC-15: Given a viewport under 960 px, then the sidebar is behind a menu button and drawers are full screen.
- AC-16: Given a deep link to a deleted item, then the drawer says the item no longer exists and offers a way back.
- AC-17: Given the knowledge base, dictionary and manual lookup lists, then their items expose the full scope (kind, tenants, assistants) in the API.
- AC-18: Given the audit log page, when I filter by outcome and period, then only matching entries show, the URL carries the filters and no action edits or deletes an entry.
- AC-19: Given a lookup derived from a knowledge base, then it appears on the Lookups page and in the detail of its knowledge base, opens the same values view from both, and cannot be edited or deleted there.
- AC-20: Given the knowledge base wizard, then it opens as a full page, and leaving it with unsaved input asks to discard it like a drawer does.

## Deferred Decisions
| Decision | Fallback chosen now | Revisit when |
|---|---|---|
| Flat "All tools" view across types | Not built; the review happens per server and per type | Reviewing pending tools across many servers is reported as painful |
| Component tests for the UI (bUnit) | Shared components covered by Playwright-free unit tests of their view models; pages verified by hand in the browser | A regression slips through that a component test would have caught |
| Several assistants in the UI | Single assistant; filter by agent already works with the assistants the API returns | The product allows more than one assistant |
| Saved filter views | None | Admins ask for named views |

## Open Questions
None.
