# Spec 017: Agent Skills

**Status:** Approved (2026-11-04)
**Date:** 2026-11-04

Builds on spec 013 (agents, sub-agents, scope rules) and spec 004 (tools, audit, result size). Skills are the "skills" of `docs/brainstorming.md`: reusable packages of instructions that an agent loads on demand.

## Problem

Today everything an agent knows how to do has to live in its system prompt, which is paid for on every turn. On local models with a small context this forces admins to choose between a long prompt (slow, diluted) and a thin one (the agent lacks the procedure). Procedures such as "how we review a contract" or "how we write a weekly report" are needed only in some turns and are shared by several agents.

Skills solve it the way the Agent Skills format and Microsoft Agent Framework do: the agent always sees a short list (name and "when to use it"), and reads the full instructions only when the model decides it needs them. The admin writes a skill once and assigns it to the agents that need it. Skill packages are widely distributed as folders or archives of `SKILL.md` files, so importing them in bulk matters as much as the editor.

## Decisions

- **D-1**: A **skill** is a named piece of instructions: slug, display name, a description saying when to use it, and a markdown body. Skills are text only in this spec: no bundled resources and no scripts (see Deferred Decisions).
- **D-2**: Skills are a resource of the Agents module, managed by the admin, and **assigned to agents** (many to many), as tools are. They are not assigned to assistants.
- **D-3**: Runtime follows the progressive disclosure of Microsoft Agent Framework. For an agent with at least one skill, the request to the model carries (a) a block in the system prompt listing each skill as `slug: description`, and (b) the built-in tool `load_skill`, with a single string argument `name` (a skill slug), whose result is the body of that skill. Nothing else of the body is ever in the prompt.
- **D-4**: `load_skill` is a built-in tool of the Agents module, offered automatically whenever the agent has skills. In the tools tab of the agent edit form it is shown **already selected and locked** (not removable, with a note saying it is offered because the agent has skills); an agent with no skills shows it unselected and locked, with a note saying it is offered once a skill is assigned. It is never stored in `agent_tools`. It follows the conventions of built-in tools (spec 015: it is called by its own name, which the admin may rename on the Built-in tools page).
- **D-5**: The integration uses the Microsoft Agent Framework skills support (`AgentSkillsProvider` with `AgentInlineSkill` built from the database rows) wherever it fits; its types stay inside the Agents module and never cross `Quorum.Modules.Agents.Contracts`. Our own adapter exists only at the edges: reading the rows, the ledger and the audit.
- **D-6**: **Each agent has its own skills**, also when it runs as a sub-agent. A sub-agent uses only its own skills and never those of the caller; the caller's skills are not visible to it (consistent with spec 013 D-4, D-5, D-13).
- **D-7**: `load_skill` has no side effects and is not subject to confirmation: the `confirm` policy of spec 004 does not apply to it.
- **D-8**: A call to `load_skill` appears in the live ledger as an ordinary tool call, with the skill slug as argument, and in the tool audit log with `AgentId` and `AssistantId` (spec 013 D-28). The audit stores the call and its outcome, **never the body** (as spec 013 D-31 for delegations).
- **D-9**: An unknown slug, or a slug that is not assigned to the calling agent, returns a readable error as the tool result, listing the slugs available to that agent, so the model can correct itself.
- **D-10**: The body of a skill has **no size limit**. The result of `load_skill` obeys the rule of every tool result (spec 004 D-10, spec 013 D-29): a body larger than `ToolSettings.MaxResultCharacters` is **not truncated**; the model receives the "result too large" error. The admin UI shows an informational warning when the body being saved exceeds that limit.
- **D-11**: The **description** is mandatory and limited to **1,024 characters** (the limit of the Agent Skills standard), because it is in the prompt on every turn. The body is mandatory and non-empty.
- **D-12**: A skill is identified by a **slug unique per tenant**, in the same format as agent slugs (short, lowercase letters, digits and hyphens), plus a free `DisplayName` for the UI. The slug is the argument the model passes to `load_skill` and is the stable cross-reference of the future declarative format (spec 013 D-8). Slugs live in their own namespace: a skill slug may equal the name of a tool or of an agent, because it is never a tool name.
- **D-13**: Skills have a **scope** like agents (`Global`, `Tenant`, `ScopeTenantIds`) on top of the owning `TenantId`. A skill assigned to an agent must be visible at least as widely as the agent: a `Global` agent accepts only `Global` skills; an agent limited to some tenants accepts `Global` skills or skills whose scope includes those tenants. Violations are refused at save; narrowing the scope of a skill or of an agent in a way that breaks an assignment is refused with `409` (the rule of spec 013 D-21).
- **D-14**: Deleting a skill that is assigned to agents is refused with `409 Conflict`, listing the agents. There is no archive state and no "disabled" flag.
- **D-15**: Skills are assigned through `GET/PUT /api/agents/{id}/skills` (`{ "skillIds": [] }`, atomic replacement), as tools are. The agent document of spec 013 is unchanged. Assigning skills requires `agents.manage`; creating, editing, deleting, importing and exporting skills requires the new permission `skills.manage`, held by `Admin` (spec 004 only defines and checks permission names).
- **D-16**: An agent whose model endpoint has `SupportsTools = false` can be saved with skills, but they are **inert**: no skill list is added to the prompt and `load_skill` is not offered, because the model could not use it. The admin UI says so next to the skill selector. (This differs from spec 013 D-19, which refuses tools on such an endpoint.)
- **D-17**: A turn uses the skills as they were when it started (the list in the prompt and the bodies read by `load_skill`). Editing or deleting-after-unassigning a skill never alters a running turn (spec 013 D-8 and AC-25).
- **D-18**: An agent with no skills gets no skill block and no `load_skill`: agents that do not use skills are unchanged.
- **D-19**: **Import.** The admin can upload one `SKILL.md` file, a folder chosen in the browser (several files), or an archive (`.zip` or `.tar.gz`) holding many skills, as skills are usually distributed in packages. `SKILL.md` files are looked for up to two levels deep (as Microsoft Agent Framework does) and the folder name is irrelevant: the slug is the `name` of the frontmatter.
- **D-20**: Import is **preview, then confirm**. The upload returns a preview of every skill found: new, conflicting (the slug exists in the tenant) or invalid, with the reason. For each conflict the admin chooses `skip` (default) or `overwrite`, then confirms; nothing is written before the confirmation. The server keeps the parsed preview for 30 minutes; after that the upload must be repeated.
- **D-21**: Validation of an imported skill: the frontmatter must be YAML with `name` and `description`; `name` must already satisfy the slug format (**no silent normalisation**: otherwise the skill is invalid in the preview); the description limit of D-11 applies; the body must not be empty; a slug that appears twice in the same upload makes the later ones invalid. Invalid skills never block the valid ones.
- **D-22**: **Extra files** of a package (`references/`, `scripts/`, `assets/` and anything else) and **extra frontmatter fields** (`license`, `compatibility`, `metadata`, `allowed-tools`) are ignored and **reported** per skill in the preview and in the result, with a warning that the body may refer to files the agent will not have. In particular `allowed-tools` (an experimental field of the standard that lists tools pre-approved while a skill is active) grants nothing: the permissions and policies of spec 004 are the only ones.
- **D-23**: Limits of an upload: **20 MB**, applied both to the uploaded file and to the total size once extracted. **There is no limit on the number of skills.** Archives with absolute paths, `..` segments or links are rejected (path traversal), and entries that are not `SKILL.md` are not read beyond the report of D-22.
- **D-24**: An imported new skill is created with scope `Tenant` for the current tenant and with a `DisplayName` derived from its slug according to the **display name style** setting (D-28); the admin can change it afterwards in the editor. `overwrite` replaces description and body only; it keeps the display name, the scope and the assignments.
- **D-25**: **Export.** One skill is downloaded as a `SKILL.md` (frontmatter `name` and `description`, then the body); all skills or a selection are downloaded as a `.zip` with one `<slug>/SKILL.md` per skill. The display name and the scope are not written, so the file stays valid for other Agent Skills tools and is accepted back by the import.
- **D-26**: The admin UI gets a Skills page (list with search; editor: slug, display name, description, body, scope; import and export) and a skill selector in the agent edit form, next to the tools. The UI warns, without limiting, when an agent has more than **10** skills (the same threshold as spec 013 D-30).
- **D-28**: **Display name style** is a setting of the tenant, edited by the admin on the Skills page (permission `skills.manage`), with two values: `Slug` (default; `code-review` stays `code-review`) and `Humanized` (`code-review` becomes `Code review`: hyphens to spaces, first letter upper case, the rest unchanged). It applies only when a skill is created by import; it never rewrites existing skills, and the export does not write the display name (D-25).
- **D-27**: The data model stays serialisable to the future declarative format (spec 013 D-8): stable slugs, no runtime state in the definitions.

## Scope

### In

- Skill entity, `agent_skills` assignment, scope rules, CRUD, migration of the Agents module.
- Runtime: skill block in the prompt, built-in `load_skill`, ledger and audit entries, interplay with sub-agents and with endpoints without tools.
- Import of file, folder and archives with preview and confirmation; export of one or many.
- Permission `skills.manage`.
- Admin UI: Skills page and selector on the agent.
- Documentation updates that spec 013 also made: the schema snapshot, the glossary and the admin guide.

### Out

- Skill resources (`read_skill_resource`) and scripts (`run_skill_script`), and any execution sandbox.
- Skills coming from MCP servers or from NuGet/PyPI packages.
- Versioning or history of skills.
- Declarative file format and graph editor.
- Skills on assistants, or inherited from the caller by sub-agents.
- Persisting the ledger per message.
- Display name styles beyond `Slug` and `Humanized`, or per-skill derivation rules.

## Domain Model

Module Agents (schema `agents`). Every entity carries `TenantId` with the global query filter.

| Entity | Fields | Notes |
|---|---|---|
| **Skill** (new) | Id, TenantId, Slug (unique per tenant), DisplayName, Description (≤ 1,024), Body (non-empty), Scope (`Global`, `Tenant`), ScopeTenantIds, UpdatedAt | Table `agents.skills` |
| **AgentSkill** (new) | AgentId, SkillId, TenantId | Unique per pair; table `agents.agent_skills` |
| **SkillSettings** (new) | TenantId, DisplayNameStyle (`Slug` default, `Humanized`) | One row per tenant, created on first save; absent row means the default (D-28) |

- **Slug**: same format as the slug of agents (D-12).
- **Lifecycle**: skills are created, edited, imported and deleted by the admin; deletion is refused while assigned (D-14).
- **Runtime state** is never stored on definitions (D-27). The preview of an import (D-20) is transient server state, not a definition.
- **Migration**: a regular EF Core migration of the Agents module adding the two tables; existing data is untouched.

## API Contract

All under `/api`, errors as `ProblemDetails`, validation errors as `ValidationProblem`.

| Verb | Route | Request | Response | Errors |
|---|---|---|---|---|
| GET | `/skills` | - | `[{ id, slug, displayName, description, scope, agentCount }]` | 401; 403 |
| GET | `/skills/{id}` | - | skill document below | 404 |
| POST / PUT | `/skills`, `/skills/{id}` | skill document | 201 / 204 | 400 (slug, format, D-11, D-13); 404; 409 (slug taken; scope narrowing breaks an assignment) |
| DELETE | `/skills/{id}` | - | 204 | 404; 409 listing the agents (D-14) |
| GET / PUT | `/agents/{id}/skills` | `{ skillIds: [] }` | skill list | 400 (skill not visible to the agent, D-13); 404; policy `agents.manage` |
| POST | `/skills/import/preview` | `multipart/form-data`: one or more files (a `SKILL.md`, the files of a folder with their relative paths, or one `.zip` / `.tar.gz`) | `{ previewId, expiresAt, items: [{ slug, status: new\|conflict\|invalid, reason?, ignoredFiles: [], ignoredFields: [] }] }` | 400 (unreadable, path traversal); 413 (over 20 MB) |
| POST | `/skills/import` | `{ previewId, decisions: [{ slug, action: skip\|overwrite }] }` (conflicts omitted default to `skip`) | `{ created: [], overwritten: [], skipped: [], failed: [{ slug, reason }] }` | 404 (preview unknown or expired); 409 (a slug taken after the preview) |
| GET / PUT | `/skills/settings` | `{ displayNameStyle: Slug\|Humanized }` | `{ displayNameStyle }` / 204 | 400; 403 |
| GET | `/skills/{id}/export` | - | `SKILL.md` (`text/markdown`) | 404 |
| GET | `/skills/export?ids=` | ids omitted = all | `.zip` | 404 |

Skill document:

```json
{
  "slug": "contract-review",
  "displayName": "Contract review",
  "description": "How to review a contract clause by clause. Use it when the user asks to check, compare or summarise a contract.",
  "body": "# Contract review\n\n1. ...",
  "scope": "Tenant",
  "scopeTenantIds": ["uuid"]
}
```

`load_skill` as seen by the model: one string argument `name`; the result is the body.

## Authorization

- Skills (read, write, import, export): `skills.manage`, held by `Admin`. Reads need it too, because bodies may hold internal procedures.
- Assigning skills to agents: `agents.manage` (spec 013 D-26).
- Tenant boundary: every entity filtered by `TenantId`; visibility of `Global` skills across tenants follows the rule already used for agents and MCP servers. An agent sees only the skills assigned to it that are visible to the tenant of the turn.
- `load_skill` runs with the permissions of the agent, needs no user permission and no confirmation (D-7).

## Edge Cases & Failure Modes

- **Slug**: duplicate within the tenant, or invalid format, is a validation error. Editing a slug of an assigned skill is allowed; assignments are by id, and the new slug is what the model sees from the next turn.
- **Unknown or unassigned slug** in `load_skill`: readable error with the available slugs (D-9).
- **Body too large** for a tool result: "result too large" error, nothing truncated (D-10).
- **Endpoint without tools**: skills saved but inert (D-16); the UI shows a notice.
- **Sub-agent**: gets only its own skills; a skill assigned only to the caller is unknown to it (D-6).
- **Concurrent edits**: a running turn keeps the definitions it started with (D-17).
- **Import**: a corrupt archive, path traversal, links or an over-limit size reject the whole upload with no preview; an invalid skill inside a valid upload appears as invalid and the others proceed (D-21, D-23). Two skills with the same slug in one upload: the later ones are invalid. A slug created by someone else between preview and confirmation returns `409` for that skill and it is reported in `failed`; the others are still applied. The confirmation is idempotent per preview: repeating it after success finds the preview consumed (`404`).
- **Overwrite** keeps display name, scope and assignments (D-24).
- **Deletions and scope narrowing** (D-13, D-14): refused with `409` and the list of references.
- **Archive bombs**: the 20 MB limit applies to the extracted size too (D-23).

## Non-Functional Requirements

- Skills add no model round trip unless the model calls `load_skill`; the skill block adds roughly the size of the descriptions to the prompt of an agent that has skills (about 100 tokens each is the reference of Microsoft Agent Framework).
- No volume assumptions beyond hundreds of skills per tenant (imported packages can be large); the list endpoint is not paginated, like the other admin lists, and the UI warns above 10 skills per agent (D-26).
- Logs are structured (Serilog) and carry the agent slug and the skill slug; no body text in logs.
- Microsoft Agent Framework / Extensions.AI types never cross `Quorum.Modules.Agents.Contracts` (D-5).
- The import parsers use established libraries (YAML frontmatter, zip, tar/gzip) instead of hand-written ones.

## Integrations

- **Microsoft Agent Framework**: `AgentSkillsProvider` and `AgentInlineSkill` for the prompt block and `load_skill`, on the `ChatClientAgent` of spec 013 (D-5).
- **Spec 004 (tools)**: audit entries, size rule for results, the permission model; `load_skill` is a built-in tool with the conventions of spec 015.
- **Spec 013 (agents)**: scope rules (D-21), `AgentId` in the audit (D-28), no nesting; the agent edit form gains the skill selector.
- **Conversations (Contracts)**: the ledger entry of `load_skill` uses the existing tool-call event; no new event.
- No events and no Wolverine usage: there is no asynchronous cross-module flow.

## Acceptance Criteria

- **AC-1** Given an admin, When creating a skill with a valid slug, description and body, Then it is stored and listed; a duplicate or malformed slug, an empty body, or a description over 1,024 characters returns a validation error.
- **AC-2** Given a user without `skills.manage`, When calling any skill endpoint (read, write, import, export), Then the response is `403`.
- **AC-3** Given a user with `agents.manage` but not `skills.manage`, When assigning existing skills to an agent, Then it succeeds; When creating a skill, Then `403`.
- **AC-4** Given an agent with skills assigned through `PUT /agents/{id}/skills`, When the list is replaced, Then the previous assignments are replaced atomically.
- **AC-5** Given a `Global` agent and a `Tenant`-scoped skill, When assigning, Then it is rejected (D-13).
- **AC-6** Given a skill assigned to an agent, When narrowing the scope of the skill or of the agent so the assignment breaks, Then `409` lists the assignment.
- **AC-7** Given a skill assigned to agents, When deleting it, Then `409` lists the agents; unassigned, it is deleted.
- **AC-8** Given an agent with two skills on an endpoint with tools, When the request to the model is built, Then the system prompt lists `slug: description` of both and the model is offered `load_skill` with a `name` argument; no body is in the prompt.
- **AC-9** Given that request, When the model calls `load_skill` with an assigned slug, Then the result is that skill's body.
- **AC-10** Given the model calls `load_skill` with an unknown slug, or with a slug not assigned to the agent, When it runs, Then it receives a readable error listing the agent's slugs and the turn continues.
- **AC-11** Given an agent with no skills, When the request is built, Then there is no skill block and no `load_skill` (D-18).
- **AC-12** Given an agent with skills on an endpoint with `SupportsTools = false`, When saving, Then it is accepted; When the request is built, Then no skill block and no `load_skill` are present (D-16).
- **AC-13** Given a root agent with skills and a sub-agent with others, When the sub-agent runs, Then it sees only its own skills and not the root's, and the root keeps only its own (D-6).
- **AC-14** Given a skill whose body is larger than `MaxResultCharacters`, When `load_skill` is called, Then the model receives the "result too large" error and nothing is truncated; the admin UI showed a warning at save (D-10).
- **AC-15** Given a call to `load_skill`, When shown in the ledger, Then it appears as a tool call with the skill slug as argument; no confirmation is requested (D-7, D-8).
- **AC-16** Given a call to `load_skill`, When audited, Then the entry holds the tool name, the slug argument, the outcome, `AgentId` and `AssistantId`, and not the body (D-8).
- **AC-17** Given a turn in progress, When an admin edits or reassigns a skill of the agent, Then the running turn uses the skills as they were at its start (D-17).
- **AC-18** Given a single valid `SKILL.md` and the default style `Slug`, When uploaded, Then the preview lists it as new, and after confirmation it exists with scope `Tenant` and `DisplayName` equal to its slug (D-24).
- **AC-31** Given the style set to `Humanized`, When importing `code-review`, Then the new skill has `DisplayName` "Code review"; existing skills keep their names, and changing the style never rewrites them (D-28).
- **AC-32** Given a user without `skills.manage`, When reading or changing the style, Then the response is `403`.
- **AC-19** Given a folder of files and given a `.zip` and a `.tar.gz` each holding several skills at depth up to two, When uploaded, Then the preview lists every `SKILL.md` found, regardless of the folder names.
- **AC-20** Given an upload with no limit on the count, When it holds hundreds of valid skills under 20 MB, Then all appear in the preview and can be confirmed.
- **AC-21** Given an upload over 20 MB, or an archive that extracts to more than 20 MB, When uploaded, Then it is rejected with `413` and nothing is written.
- **AC-22** Given an archive with absolute paths, `..` segments or links, When uploaded, Then it is rejected and nothing is written (D-23).
- **AC-23** Given a `SKILL.md` whose `name` is not a valid slug, missing `description`, a description over the limit, or an empty body, When previewed, Then that skill is `invalid` with the reason, the others in the upload are unaffected, and nothing is normalised (D-21).
- **AC-24** Given a package with `references/`, `scripts/`, `assets/` and frontmatter `license`, `metadata`, `allowed-tools`, When previewed and imported, Then only name, description and body are stored and the ignored files and fields are reported per skill (D-22); no tool is pre-approved by `allowed-tools`.
- **AC-25** Given an upload with a slug that already exists, When previewed, Then it is `conflict`; confirming without a decision skips it, `overwrite` replaces description and body and keeps display name, scope and assignments (D-20, D-24).
- **AC-26** Given a preview, When it is not confirmed, Then nothing was written; When 30 minutes pass or it was already confirmed, Then the confirmation returns `404`.
- **AC-27** Given a slug created between preview and confirmation, When confirming it as `new`, Then that skill is in `failed` with a `409` reason and the others are applied.
- **AC-28** Given a skill, When exported, Then the file is a `SKILL.md` with frontmatter `name` and `description` and the body; Given all or selected skills, Then a `.zip` with `<slug>/SKILL.md` each; and importing that `.zip` in a clean tenant reproduces the same slugs, descriptions and bodies.
- **AC-29** Given the admin UI, When editing an agent, Then a skill selector lists the visible skills, warns above 10 selected and shows the notice of D-16 when the endpoint has no tools.
- **AC-33** Given the tools tab of an agent with skills, Then `load_skill` is shown selected and locked with the note of D-4; Given an agent with no skills, Then it is shown unselected and locked with its note; in neither case is it written to `agent_tools`.
- **AC-30** Given a skill slug equal to the name of a tool or of an agent, When saving the skill, Then it is accepted (D-12).

## Deferred Decisions

| Decision | Chosen fallback | Revisit trigger |
|---|---|---|
| Skill resources (`read_skill_resource`) and scripts (`run_skill_script`) | Not supported; ignored and reported at import (D-22). The data model leaves room for resources attached to a skill. | A package the customers need that cannot work as text only; a decision on a script sandbox |
| Skills coming from MCP servers or from packages | Not supported. | Demand from customers |
| Versioning and history of skills | None; edits overwrite. | Admins asking to compare or roll back |
| Declarative file format and graph editor | Slugs and `skillIds` by slug stay serialisable (D-27). | The declarative format spec |
| Honouring `allowed-tools` of a skill | Ignored (D-22); permissions only from spec 004. | Users and roles spec |
| Persisting the ledger (including `load_skill`) per message | Not persisted, as for any tool (spec 013 D-11). | Ledger-per-message spec |

## Amendments

- **2026-11-05, D-5 (spike of PR 2).** `Microsoft.Agents.AI` 1.25.0 exposes `AgentSkillsProvider`, an `AIContextProvider` that builds the skill block and registers its own `load_skill` (with read-resource and run-script tools and their approval rules). Its `load_skill` runs outside `ToolCallInvoker`, so it would lose the audit entry (D-8), the result size rule (D-10), the ledger event (D-8), the tool rename of spec 015 and the turn snapshot (D-17). Its content builder (`AgentInlineSkillContentBuilder`) is internal. Decision: the `load_skill` tool and the skill block are ours (`SkillQuorumTool`, `SkillsNotice`), following the MAF model (advertise the names, load the body on demand). MAF stays the source of the skill concepts where it does not decide the runtime. `load_skill` is kept out of the tool pool: it is offered to agents with skills and never stored as an activation (D-4).

## Open Questions

None. Confirmed in review (2026-11-04): the per-tenant display name style with `Slug` and `Humanized` (D-28), `load_skill` shown selected and locked in the tools tab (D-4), automatic activation, overwrite semantics, slug edits while assigned, the 30-minute preview and the 20 MB limit on the extracted size.
