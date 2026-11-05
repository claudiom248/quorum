# Plan 017: Agent Skills

**Spec:** `docs/specs/017-agent-skills.md` (Approved 2026-11-04)
**Architecture:** Modular Monolith; Vertical Slice inside the Agents module.
**Mode:** `/tdd`: every acceptance criterion becomes a failing test first.
**Status:** Approved (2026-11-04). Implemented in PRs 1 to 6 on local branches `feat/017-skills-*` (2026-11-05), not pushed.
**Affected:** Agents (Domain, Persistence, Chat, Tools, Features/Skills), `Quorum.Shared` (`QuorumPolicies`), Tenancy (policy registration), `Quorum.Web.Client` (Skills page, agent form, built-in tools page), `tests/Quorum.Integration.Tests`, `docs/database-schema.md`, `docs/product.md`, `docs/mcp-tools.md`.

## What the code already gives us
- Delegation is the precedent for a tool that is **not a stored activation**: `AgentQuorumTool` is built at the start of the turn, the invoker treats its policy as `Auto` (`ToolCallInvoker.PrepareAsync`), and size limit, audit, ledger and cancellation come for free. `load_skill` follows the same path.
- Built-in tools are rows of `agents.tools` provisioned from `BuiltinToolCatalog` by `BuiltinToolProvisioner`; spec 015 renaming and the Built-in tools page already work for them. `ask_user` is the precedent for a built-in whose policy is not consulted.
- `AssistantChat.PrepareAsync` and `LoadSubAgentsAsync` are the one place where a turn reads its agent's definitions, for the root and for each sub-agent; `WithNotices` is where the prompt grows with the tools offered.
- Visibility of agents across tenants is `VisibleAgents()` with `IgnoreQueryFilters`; skills need the same helper (D-13).
- `AgentRules` holds the slug format; the 409 lists of references live in `BlockingReference`.

## Decisions
| Topic | Choice |
|---|---|
| Microsoft Agent Framework (spec D-5) | `AgentSkillsProvider` registers its own `load_skill` function on the agent, which would run outside `ToolCallInvoker`: no audit entry, no `MaxResultCharacters` rule, no ledger event, no renamed tool, no snapshot. So the **tool and the prompt block are ours** (`SkillQuorumTool`, a `QuorumTool`), following the MAF model (advertise, load). A spike at the start of PR 2 checks whether `Microsoft.Agents.AI` 1.25.0 exposes the pieces separately (a prompt renderer, an inline skill type usable without its tool registration); if it does, they are used for the block, otherwise nothing from MAF is used for skills and the Deferred Decisions of the spec say why. This is the one place where the spec's "wherever they overlap" is answered with "they do not overlap at the edge that matters". |
| Where `load_skill` lives | A fourth entry of `BuiltinToolCatalog` (`load_skill`, argument `name`), so it is provisioned per tenant, renamable and listed on the Built-in tools page. It is **never written to `agent_tools`** (D-4): `SkillTools.LoadAsync(db, agentId)` builds its `ActiveTool` from the tenant's built-in row when the agent has at least one visible skill and the row is `Enabled`. A disabled row makes skills inert (proposed, see Open Questions). Its policy is not consulted (D-7), like `ask_user`. |
| Turn snapshot (D-17) | `PrepareAsync` and `LoadSubAgentsAsync` read, once, the agent's skills (slug, description, **body**) into a `SkillSnapshot` held by the `SkillQuorumTool`; the call reads the snapshot, never the database. Bodies are read eagerly because D-17 promises the body as it was at the start; the cost is bounded by the skills of one agent (the UI warns above 10). |
| Prompt block | `WithNotices` gains `SkillsNotice(toolName, skills)`: a short instruction naming the tool as it is offered (it may be renamed, as `AskUserNotice` does), then `- slug: description` per skill, in slug order. It is part of the system prompt, so `ContextWindow` and the measure endpoints count it with no extra code. Inert when the endpoint has no tools (D-16) because `caller` is already `null` there. |
| Sub-agents (D-6) | `SubAgentSource` carries its own `SkillSnapshot`; the sub-agent's tools list gets its own `SkillQuorumTool`; the root's skills are never passed down. |
| Visibility | `SkillVisibility.For(db)` mirrors `VisibleAgents()`: own skills, `Global`, and `Tenant` scopes that include the tenant. An agent another tenant shares runs with the skills its **owner** assigned (as it does with tools, `ToolPool.OwnerOfAsync`). |
| Scope checks | `SkillScopeRules` reuses the agent rules of spec 013 D-21 between an agent and each skill; the agent update endpoint also checks its skills when its scope narrows (extends `AgentReferences`). |
| Import parsing | Established libraries only: **YamlDotNet** for the frontmatter, `System.IO.Compression` for `.zip`, `System.Formats.Tar` with `GZipStream` for `.tar.gz`. Extraction is done in memory over a counting stream that stops at 20 MB of extracted bytes; entries are never written to disk, so path traversal is a question of names (rejected) and links (rejected), not of the file system. |
| Import state | The preview is kept in `IMemoryCache` for 30 minutes, keyed by tenant and `previewId`, and consumed by the confirmation (D-20). Single instance by design, like the other in-memory caches (spec 007). |
| Display name | `SkillSettings` is one row per tenant created on first save; `DisplayNameStyle` is read at import. |
| Folder upload in the browser | Blazor's `InputFile` does not expose relative paths; the page uses a small JS module (`<input type="file" webkitdirectory>`) that hands the files and their `webkitRelativePath` to the same multipart endpoint. A manual check in the browser pane is part of PR 5. |
| Migration | One EF Core migration of the Agents module (PR 1): `skills`, `agent_skills`, `skill_settings`. Nothing to copy. The built-in row of `load_skill` is created by the provisioner at startup, not by the migration (PR 2). |
| Test strategy | Integration tests with Testcontainers; the fake OpenAI-compatible endpoint scripts the turns (as the tool and delegation tests do) for the runtime ACs; import tests build archives in memory. One test per AC, named after it. |

## Order and parallel work
- **PR 1** first: entities, migration, permission, CRUD, assignment. Everything else needs the tables.
- **PR 2** (runtime) and **PR 3** (import) depend on PR 1 and touch different folders (`Chat`/`Tools` against `Features/Skills`); they can be developed in parallel and merge in any order. Only PR 1 has a migration, so there is no migration conflict.
- **PR 4** (export) depends on PR 1 and lives in its own endpoint class; it can go in parallel with PR 2 and 3. Its import-round-trip test (AC-28) needs PR 3, so it merges after it.
- **PR 5** (UI) depends on the API of PR 1, 3 and 4 and on PR 2 for the locked tool row; it merges last among the code PRs.
- **PR 6** (documentation) closes the plan.
- The spec and this plan are on the `docs/spec-017` branch and its PR; implementation PRs start from `main` once that PR is merged.

## Acceptance criteria map
| AC | PR |
|---|---|
| AC-1, AC-2, AC-3, AC-4, AC-5, AC-6, AC-7, AC-30, AC-32 | 1 |
| AC-8, AC-9, AC-10, AC-11, AC-12, AC-13, AC-14, AC-15, AC-16, AC-17 | 2 |
| AC-18, AC-19, AC-20, AC-21, AC-22, AC-23, AC-24, AC-25, AC-26, AC-27, AC-31 | 3 |
| AC-28 | 4 |
| AC-29, AC-33 | 5 |

## Steps

### PR 1: Skills, assignment and settings (`feat/017-skills-api`)
1. `QuorumPolicies.SkillsManage = "skills.manage"`, registered for `Admin` in `TenancyModule` next to `AgentsManage`.
2. Domain: `Skill` (slug, display name, description, body, scope, scope tenant ids, updated at), `AgentSkill` (agent, skill, tenant), `SkillSettings` (tenant, display name style enum `Slug`/`Humanized`). Tenant query filters; unique `(TenantId, Slug)`; unique `(AgentId, SkillId)`; `agent_skills` rows go with the agent (cascade) and block the skill's deletion (restrict, reported by the endpoint as 409).
3. `AgentsDbContext` configuration and the migration `AddSkills`; refresh `docs/database-schema.md` is left to PR 6.
4. `SkillRules` (slug format reused from `AgentRules`, description 1..1024, body non-empty) and `SkillScopeRules` (spec 013 D-21 between an agent and a skill, and between the tenant lists).
5. `SkillEndpoints` (`skills.manage`): list with `agentCount`, get, create, update, delete with `409` and `BlockingReference` listing the agents; a scope change that breaks an assignment is `409`.
6. `AgentSkillsEndpoints` (`agents.manage`): `GET/PUT /api/agents/{id}/skills` with atomic replacement and the visibility check (400). The agent update checks its assigned skills when its scope narrows (extends `AgentReferences`).
7. `SkillSettingsEndpoints`: `GET/PUT /api/skills/settings` (`skills.manage`), absent row means `Slug`.
8. Tests (named after the AC): AC-1, AC-2, AC-3, AC-4, AC-5, AC-6, AC-7, AC-30, AC-32.

### PR 2: Runtime: prompt block, `load_skill`, ledger and audit (`feat/017-skills-runtime`)
9. **Spike** (before any code): does `Microsoft.Agents.AI` 1.25.0 offer a renderer or a skill type usable without registering its own tool? Write the answer, with the decision of the Decisions table, in the PR description and, if nothing is reused, in the spec's Deferred Decisions via a spec amendment.
10. `BuiltinToolCatalog.LoadSkill` with its definition (argument `name`); the provisioner creates the row for each tenant at startup and keeps the admin's decisions; the Built-in tools page lists it (no change to the page in this PR beyond the data).
11. `SkillTools.LoadAsync(db, agentId)`: visible skills assigned to the agent (owner's assignments for a shared agent), the built-in row if `Enabled`, producing the `ActiveTool` and the `SkillSnapshot`; nothing when there are no skills (AC-11).
12. `SkillQuorumTool : QuorumTool`: validates `name` against the snapshot, returns the body, or throws `ToolFailedException` with the readable error listing the agent's slugs (AC-10). `ToolCallInvoker.PrepareAsync` treats it as `Auto`, like `AgentQuorumTool`, and skips the stored-activation check.
13. `AssistantChat.PrepareAsync` and `LoadSubAgentsAsync`: load the snapshot for the root and for each sub-agent (only when the endpoint supports tools, AC-12); add the tool to each list; `WithNotices` appends `SkillsNotice` (name as offered). Sub-agents never receive the caller's snapshot (AC-13). `forMeasure` takes the same path.
14. Audit: confirm the entry stores the call, the `name` argument, the outcome, `AgentId` and `AssistantId`, and no body; `AuditRedactor` needs no change unless the arguments are redacted by name (AC-16). The ledger shows the call as an ordinary tool call (AC-15).
15. Tests: AC-8, AC-9, AC-10, AC-11, AC-12, AC-13, AC-14 (body above `MaxResultCharacters`), AC-15, AC-16, AC-17 (edit between the start of the turn and the call).

### PR 3: Import with preview and confirmation (`feat/017-skills-import`)
16. Add `YamlDotNet` to `Directory.Packages.props` and the Agents project.
17. `SkillPackageReader`: accepts the multipart files as a single `SKILL.md`, the files of a folder with relative paths, a `.zip` or a `.tar.gz`; finds `SKILL.md` up to two levels deep; counting stream with the 20 MB limit on upload and on extracted size; rejects absolute paths, `..` segments and links; returns the skills found with the list of other files (AC-19, AC-20, AC-21, AC-22).
18. `SkillFrontmatter`: YAML parse, `name` and `description` required, `name` validated against the slug rule with no normalisation, other fields reported as ignored (AC-23, AC-24).
19. `SkillImportPreview`: statuses `new`, `conflict`, `invalid` (with duplicates inside the upload marked invalid), stored in `IMemoryCache` for 30 minutes; endpoint `POST /api/skills/import/preview` (413 over the limit).
20. `POST /api/skills/import`: decisions per conflict (default `skip`), creation with scope `Tenant` and the display name from `SkillSettings` (AC-18, AC-31), `overwrite` replacing description and body only, `failed` with a `409` reason for a slug taken after the preview, preview consumed (AC-25, AC-26, AC-27).
21. Tests: AC-18 to AC-27 and AC-31, building archives in memory (a zip bomb case for the extracted limit, a traversal case, a link case in the tar).

### PR 4: Export (`feat/017-skills-export`)
22. `SkillExportEndpoints`: `GET /api/skills/{id}/export` (`text/markdown`, frontmatter `name` and `description`, then the body) and `GET /api/skills/export?ids=` (`.zip` with `<slug>/SKILL.md`, all when omitted). The display name and the scope are not written.
23. Test AC-28: export all, import the zip into a clean tenant, same slugs, descriptions and bodies (needs PR 3 merged).

### PR 5: Admin UI (`feat/017-skills-ui`)
24. `ApiModels` and `QuorumApi` for skills, assignment, settings, import and export; permission names in `AccessRules`.
25. Skills page (`Pages/Skills.razor`): list with search, editor (slug, display name, description, body, scope through `ScopeForm`/`ScopeView`), the size warning of D-10, the display name style setting, delete with the 409 list, navigation entry.
26. Import dialog: file, folder (JS module for `webkitdirectory`) and archive; preview table with status, reason, ignored files and fields; `skip`/`overwrite` per conflict; confirm; result. Export buttons (one, selected, all).
27. Agent form: skill selector next to the tools, warning above 10 skills (together with the existing sub-agent and tool count rule), the notice for an endpoint without tools (AC-29); `AgentToolPicker` shows `load_skill` selected and locked with the notes of D-4, never sent to `SetAgentTools` (AC-33). The Built-in tools page notes that `load_skill` ignores policy and that disabling it turns skills off.
28. Component tests for the picker rows and the import preview; manual check of folder upload and of the agent form in the browser pane.

### PR 6: Documentation (`docs/017-skills-docs`)
29. `docs/database-schema.md` (snapshot at the new migration, ER diagram), `docs/product.md` (glossary: skill, `load_skill`), `docs/mcp-tools.md` where it lists built-in tools, the status lines of spec and plan.

## Risks
- **MAF does not fit the edge** (Decisions, step 9): the mitigation is our own small tool and prompt block, which is also what keeps audit, size rule and snapshot correct. The risk is only that the spec says "wherever they overlap"; the PR records why they do not.
- **Eager bodies at every turn** (D-17): an agent with many large skills reads them on every turn. Bounded by the UI warning and by the fact that bodies are plain text; revisit if turn start latency shows it.
- **Bodies without a limit** (D-10): a body above the tool result limit makes `load_skill` fail at runtime; the UI warns at save and the model receives the "result too large" error.
- **Archive handling**: zip bombs, traversal and links are the security surface of this spec. Everything is read in memory under a byte counter; links and odd names are rejected; the tests include the hostile archives. A review with the `security-auditor` agent is part of PR 3.
- **Folder upload in Blazor WebAssembly** has no first-class API; the JS module is small but is the likeliest place for browser differences (checked manually in PR 5; the archive path is the fallback).
- **Built-in provisioning for existing tenants**: the new catalog entry must appear for tenants that exist before the release; the provisioner is verified to add missing definitions at startup (step 10 test).
- **Cross-tenant shared agents**: the skills used are the owner's assignments, and bodies of `Global` skills of another tenant are read with the filter off; covered by a test in PR 2.
- **Local `main` is one commit ahead of `origin/main`** after the spec commit; unrelated to the code, noted so nobody branches from it.

## Open Questions
None. Confirmed on approval (2026-11-04): a disabled `load_skill` built-in makes skills inert, and if the MAF spike finds nothing reusable the prompt block and the tool are ours, with the wording of spec D-5 amended and no further question.
