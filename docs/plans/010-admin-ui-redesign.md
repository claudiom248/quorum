# Plan 010: Admin UI Redesign

**Spec:** `docs/specs/010-admin-ui-redesign.md` (Approved 2026-10-24)
**Architecture:** Modular Monolith; Vertical Slice inside each module. The web client (`Quorum.Web.Client`, Blazor WebAssembly, MudBlazor 9.11) stays a client of the public API.
**Mode:** `/tdd`: every acceptance criterion becomes a failing test first (integration tests with Testcontainers for the API, bUnit for components and pages, as the repository already does).
**Status:** Approved (2026-10-24)
**Affected:** Tenancy, KnowledgeBases and Agents (endpoints), `Quorum.Shared` (list query plumbing only), `Quorum.Web.Client` (everything visible), `tests/Quorum.Integration.Tests`, `docs/`.

## Decisions
| Topic | Choice |
|---|---|
| One list contract | `ListQuery` (`scope`, `tenant`, `assistant`, `type`, `status`, `q`, `limit` default 50 and max 200, `cursor`) and `Page<T>` (`items`, `cursor`) live in `Quorum.Shared/Endpoints` as plumbing only: binding, validation and an opaque cursor codec (base64url of the last `(Name, Id)`). No business rules there: each module applies the filters to its own query. Order is always name then id, which is what makes the keyset cursor stable; **sort by another column is not offered** (the spec's "sort in the URL" is limited to the audit log, which has its own paging) |
| One code path in the UI | Every grid uses MudDataGrid's **server data** mode against the query, also the four small lists (MCP servers, RAG tools, Lookup tools, Built-in). No client-side filtering path is built: it would duplicate the filter semantics. The server supports the query on those lists too (D-15) |
| Filter semantics, in one place | One server-side `ScopeFilter` helper per module (same rules, tested twice): `scope` matches the kind; `tenant` matches the owner or a tenant in the scope's tenant ids; `assistant` matches Assistant scope that includes it, or any item that is not Assistant-scoped (it applies to every assistant). The UI's filter chips mirror it |
| Uniform scope | List items replace the string `Scope` with the `ToolScope` object (`kind`, `tenantIds`, `assistantIds`). Clients are in this repository, so the web client moves in the same PR; the sample and docs are updated in the closing PR. Dictionary fields support only Global and Tenant: their `assistantIds` are empty and `tenantIds` is empty too (they are not shared to named tenants today) |
| Routes | `GET /api/tenants` (Tenancy), `GET /api/onboarding/checklist` and `PUT /api/onboarding/checklist/{key}` and `PUT /api/onboarding/checklist/dismissed` (Tenancy; `key` is `model`, `prompt`, `tools` or `knowledge`), `GET /api/knowledge-bases/lookups` for the aggregate lookups page, `GET /api/tools/servers/{id}/tools` for the paged tools of a server. Existing detail routes keep returning the inline tools until the UI stops using them (removed in the closing PR) |
| Checklist storage | A row per installation in the Tenancy schema (`OnboardingChecklist`: completed keys as a flag set, `Dismissed`, timestamps) with one EF migration. `model` is set by the setup completion itself. Updates are idempotent upserts |
| Drawer and URL | List pages declare two routes (`/tools/mcp` and `/tools/mcp/{Id}`, `Id` being `new` or a GUID) and render the drawer in the same component, so the grid keeps its data and filters while the drawer opens and closes. **Spike in PR 6** confirms Blazor reuses the component instance across the two routes; if it does not, the drawer state is moved to a cascading `DrawerRoute` service that reads the URL and the pages stay on one route |
| URL state | `ListState` (client) round-trips `ListQuery` to the query string with `NavigationManager`, replacing the entry for filter changes (so back does not step through every keystroke) and pushing one for drawer open and close. Text search is debounced (300 ms) |
| Access page | A `NoAccess` component for routes of a section the role may not open; the sidebar hides the entries (D-19). The API remains the real guard |
| Test strategy | API: integration tests per endpoint (filters, cursor, validation, tenant isolation, authorization) in the existing `Tools`, `KnowledgeBases` and `Setup` folders. UI: bUnit for the shared components and each page, with `QuorumApi` replaced by a fake; logic kept in plain view classes (`*View.cs`, `*Form.cs`) as the project does. One end-to-end pass in the browser at the end (PR 16) |
| Deferred item to fix | Spec 010's non-functional section says bUnit is not used. It is (`bunit` in `Directory.Packages.props` and in the test project). This plan's PR corrects the spec text (amendment 1) so the two agree; no behaviour changes |

## Order and parallel work
- **Backend chain A (KnowledgeBases):** PR 2 → PR 3 → PR 4. **Backend chain B (Agents):** PR 5, after PR 3 (needs the shared list query). **PR 1 (Tenancy)** is independent of both.
- **PR 6 (shell and shared components) has no backend dependency** and starts now, in parallel with PR 1 to PR 5. The existing pages keep working inside the new shell, so every PR leaves a usable app.
- **Page PRs 7 to 14** each wait for the backend PR they list; pages of different areas touch different files and can run in parallel after PR 6, except those that share a component (marked).
- PR 15 (setup and checklist) waits for PR 1 and PR 13's Settings tabs. PR 16 is last.

## Acceptance criteria map
| AC | PR | AC | PR |
|---|---|---|---|
| AC-1, AC-2, AC-15 | 6 | AC-12, AC-13 | 15 (13 server side in 1) |
| AC-3, AC-4 | 6, 7 | AC-14 | 14 |
| AC-5, AC-7 | 3, 6 (client), pages | AC-16 | 6, 7 |
| AC-6 | 1, 6 | AC-17 | 2 |
| AC-8, AC-9 | 7, 8, 9 | AC-18 | 10 |
| AC-10 | 6, then each page | AC-19 | 4, 12 |
| AC-11 | 6, then each page | AC-20 | 11 |

## Steps

### PR 1 - Tenants and onboarding checklist API (`feat/010-tenants-checklist-api`)
1. `GET /api/tenants` in Tenancy: id, name and `current` (true for the tenant of the signed-in user, which the scope picker leaves out of "other tenants") of every tenant, admin only (D-16). The web client already has `TenantSummary(Id, Name, Current)` since PR 6. Integration tests: admin gets the list, a non-admin gets 403, anonymous 401.
2. `OnboardingChecklist` entity, configuration and migration (`dotnet ef migrations add AddOnboardingChecklist`); `GET` returns the four keys with `done` and `dismissed`; `PUT .../{key}` marks one done (idempotent, 404 for an unknown key); `PUT .../dismissed`. The setup completion sets `model`. AC-13 (server side), AC-6 (tenants).

### PR 2 - Uniform scope in the list items (`feat/010-uniform-scope`)
3. Knowledge base list items, dictionary fields and manual lookups return `ToolScope` instead of a string/string-plus-ids; the detail DTOs already carry it. Tests assert the shape for Global, Tenant (with shared tenant ids) and Assistant, and that another tenant's shared item exposes no tenant ids of the owner.
4. Web client: `KnowledgeBaseListItem`, `DictionaryField`, `ManualLookupInfo` move to `ToolScope`; the three Knowledge pages and `KnowledgeAdminView.ScopeText`/`LookupAdminView` read the new shape. No visual change. AC-17.

### PR 3 - List query, shared plumbing, knowledge lists (`feat/010-list-query`)
5. `ListQuery`, `Page<T>`, the cursor codec and the validation (limit range, malformed cursor to 400 `list.invalid_cursor`) in `Quorum.Shared/Endpoints`; unit tests, including a cursor from another list type being rejected.
6. KnowledgeBases module: `ScopeFilter` and the query on `GET /api/knowledge-bases` (`type` is the KB kind, `status` Available/Unavailable), `GET /api/dictionary` (`type` is the field type) and `GET /api/lookups` (manual). Integration tests: each filter alone and combined, `tenant` over owner and shared, `assistant` rule, cursor across pages with stable order, a filter change with an old cursor, and tenant isolation. AC-5, AC-7 (server side).
7. The web client calls the new shape for these three lists (a `Page<T>` reader in `QuorumApi`), still rendered by the old pages with the first page only until PR 11 to PR 13 replace them: the old pages request `limit=200` and warn "showing the first 200" so nothing is silently cut meanwhile.

### PR 4 - Aggregate lookups list (`feat/010-lookups-list`)
8. `GET /api/knowledge-bases/lookups`: manual and derived lookups in one page: `id`, `name`, `kind` (Manual or Derived), `knowledgeBase` (id and name, derived only), `field`, `unit`, `scope`, `valueCount`; filters from the shared query plus `kind`. Derived lookups of a KB the caller cannot see do not appear. Tests for visibility, sharing and cursor. AC-19 (server side).

### PR 5 - Tools list query and scope (`feat/010-tools-list-query`) **[waits PR 3]**
9. Agents module: the query on `GET /api/tools/servers`, `GET /api/tools/rag`, `GET /api/tools/lookup` (names as in the endpoint groups) with `ScopeFilter` and `status` (Available/Unavailable for servers, Enabled/Disabled/To review for tools); `type` is not meaningful there and is rejected with 400 when sent, which is how the UI knows not to show it. Tests as in step 6.
10. `GET /api/tools/servers/{id}/tools` paged with `status` and `q`; `GET /api/tools` (built-in) accepts `q` and `status`. `ToolInfo` gains `serverId` and the server's scope so the UI can show the badge and the read-only rule per tool. AC-8, AC-9 (server side).

### PR 6 - Shell and shared components (`feat/010-shell`)
11. **Spike:** two routes on one component keep the instance and the grid state; findings written under this step, and the `DrawerRoute` fallback of the Decisions table used if not. **Done (2026-10-24):** a throwaway page with `@page "/spike"` and `@page "/spike/{Id}"` kept the same component instance (same GUID, same click counter) while navigating by link between `/spike`, `/spike/abc` and `/spike/new`, and back, in the real app (Blazor WebAssembly, MudBlazor 9.11). The list pages therefore declare both routes and render the drawer themselves; the `DrawerRoute` fallback is not needed. The page was removed again.
12. `MainLayout` becomes the sidebar shell (`MudDrawer`, groups of D-1, current page highlighted, Lab at the bottom), slim top bar with theme toggle, user and sign out; collapse below 960 px behind a menu button; admin-only entries hidden for other roles; `NoAccess` component. The pending-review badge reads `PendingReviewCount` from the server list. AC-1, AC-2, AC-15.
13. Shared components, each with bUnit tests and no page dependency: `ScopeBadge` (D-6), `ScopePicker` replacing `ScopeEditor` (scope kind, tenant multi-select fed by `GET /api/tenants`, assistant multi-select), `FilterBar` (chips, clear, only the filters the page declares, tenant filter hidden when the installation has one tenant), `ListPage` scaffold (title, primary action, grid slot, empty states for "nothing registered" and "no match", load error with retry), `ListState` (URL round trip), `ListDrawer` (right drawer, wide variant, full screen under 960 px, Esc and backdrop and back handling), `DiscardChangesDialog` and `ConfirmDeleteDialog`. AC-3, AC-4, AC-10, AC-11, AC-16.
14. Sidebar entries for pages that do not exist yet point to the current pages (`/tools`, `/knowledge`...), so nothing is dead; each page PR repoints its entry.

### PR 7 - MCP servers and Built-in tools (`feat/010-ui-mcp-servers`) **[waits PR 5, PR 6]**
15. Split `Tools.razor`: `McpServers.razor` (grid: name, URL, scope badge, status, tools with the "to review" count, last refreshed), the server drawer (form with `ScopePicker`, test connection, headers, refresh, the tools of the server with the inline actions and a "To review" filter, delete in the dialog), `BuiltinTools.razor`. Extract `ToolTable` (Enabled switch, Policy select, Approve and keep disabled, read-only for shared servers) as a shared component used by the next two PRs. AC-3, AC-8, AC-9, AC-11, AC-16.
16. Redirect `/tools` to `/tools/mcp`. Port the existing page tests.

### PR 8 - RAG tools (`feat/010-ui-rag-tools`) **[waits PR 5, PR 7's ToolTable]**
17. `RagToolsSection` (611 lines) becomes `RagTools.razor` with a wide drawer for the form; the form logic stays in `RagToolForm`. Inline state and policy as in PR 7.

### PR 9 - Lookup tools (`feat/010-ui-lookup-tools`) **[waits PR 5, PR 7's ToolTable]**
18. `LookupToolsSection` (666 lines) becomes `LookupTools.razor`, same pattern; the definition preview (the tool as the model sees it) is kept in the drawer.

### PR 10 - Audit log (`feat/010-ui-audit-log`) **[waits PR 6]**
19. `AuditLog.razor` on the existing endpoint: period, tool, user and outcome filters in the URL, page/total paging with the grid's own pager, details drawer, no write action. A small `NamedUser` lookup is not added: the user column shows the id with the display name when the user list is already known to the session. AC-18.

### PR 11 - Knowledge bases and wizard page (`feat/010-ui-knowledge-bases`) **[waits PR 3, PR 6]**
20. `KnowledgeBases.razor` (grid with kind, status, scope, verified date; drawer for the detail, verify, test search and delete). `KnowledgeBaseWizard` (595 lines) moves to its own routes `/knowledge/bases/new` and `/knowledge/bases/{id}`, with the discard dialog on leave (`NavigationManager.RegisterLocationChangingHandler`). The detail lists the derived lookups of the KB (from the existing per-KB route). AC-20, AC-19 (KB side).

### PR 12 - Lookups (`feat/010-ui-lookups`) **[waits PR 4, PR 6]**
21. `Lookups.razor` on the aggregate list with the Manual/Derived filter; the drawer edits manual lookups and shows derived ones read-only through the shared `LookupValues` view; refresh actions stay with the KB. AC-19.

### PR 13 - Dictionary (`feat/010-ui-dictionary`) **[waits PR 3, PR 6]**
22. `Dictionary.razor` on the paged list with the field-type and cluster filters; create and edit in the drawer; the "used by" count opens the list of KBs that use the field if the API already returns it, otherwise it stays a number (no new endpoint).

### PR 14 - Model form and Settings tabs (`feat/010-ui-settings`) **[waits PR 6]**
23. `ModelConnectionForm` (URL, key with keep/remove, test connection, model list or text, tool-support switch with detection, context budget) as one component taking its test and detect delegates; bUnit tests for the key rules and the detection hints.
24. `Settings.razor` with the tabs Model, Behaviour and Tools, each saving on its own with its own status; the Tools tab has the search, grouping by source and the existing warnings. AC-14.

### PR 15 - Setup and checklist (`feat/010-ui-setup-checklist`) **[waits PR 1, PR 14]**
25. `Setup.razor` in two steps using `ModelConnectionForm`; the system prompt step is dropped (the default prompt applies; the API already accepts it omitted, otherwise the request sends the default); the "model unreachable" redirect to Settings is kept.
26. Home page checklist component reading and updating `GET/PUT /api/onboarding/checklist`; the Behaviour tab, MCP servers and Knowledge bases pages call `PUT .../{key}` on first display; the checklist shows for admins only and disappears when complete or dismissed. AC-12, AC-13.

### PR 16 - Cleanup, docs and end-to-end check (`chore/010-cleanup`)
27. Remove what the new pages replaced: `Tools.razor`, the `*Section` components, `KnowledgeNav`, `ScopeEditor`, the old Knowledge pages, unused CSS in `app.css`, the inline tools in the server detail if nothing reads them, the temporary `limit=200` notices of step 7.
28. Docs: the guide `docs/knowledge-bases.md` (navigation names), the sample project README if it names pages, `CHANGELOG` is left to release-please.
29. End-to-end pass in the browser against the real stack (compose from `docker-compose.yml`, per the project's rule), covering AC-1 to AC-20 by hand where no test does, light and dark theme, 375 px and desktop; findings fixed in this PR or recorded in `docs/deferred.md`. Final `dotnet build -c Release` and the full test suite.

## Risks
- **Large pages to split** (Tools 577, RagTools 611, LookupTools 666, Wizard 595 lines): moving logic without changing it is the safest path; the existing tests are ported first, then the pages are rebuilt on top. A PR that grows past review size is split at the grid/drawer boundary.
- **Cursor and filters:** a keyset cursor over `(Name, Id)` breaks if names are mutable between pages; accepted, an edited name can repeat or skip one row across a page boundary, and a reload fixes it.
- **MudDataGrid server data with URL state** can loop (state change triggers navigation triggers reload). `ListState` guards by comparing the new query with the current one before navigating; covered by a bUnit test.
- **Breaking API shape inside the repo (PR 2):** the web client and the sample tools are the only consumers; the integration tests and the build catch the rest.
- **Release build:** analyzers run in Release in CI; each PR is built in Release before it opens (project rule).
- **Spec 007 amendment waiting:** `docs/deferred.md` has an unrelated local edit in the author's checkout; this plan does not touch it.

## Open Questions
None.
