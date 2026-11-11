# Plan 019: Models as a First-Class Resource

**Spec:** `docs/specs/019-models-as-first-class-resource.md` (Approved 2026-11-11)
**Architecture:** Modular Monolith; a new module `Models` with Vertical Slice inside, consumed through `Quorum.Modules.Models.Contracts`.
**Mode:** `/tdd`: every acceptance criterion becomes a failing test first (AC-19 to AC-21 are manual, see "Delivery").
**Status:** In Review
**Affected:** new `Quorum.Modules.Models` + `.Contracts`; Agents (Domain, Persistence, Chat, Features, Embeddings, Probing, Secrets, Provisioning, Features/Settings); Agents.Contracts; KnowledgeBases (Domain, Persistence, Features/Registry, Search, Inspect, Verification); Tenancy (Features/Setup, policy registration); Conversations (only if spec 018 landed first, see Order); `Quorum.Shared` (`QuorumPolicies`, `QuorumActivityNames`); `Quorum.Host`; `Quorum.slnx`; `Quorum.Web.Client`; `tests/Quorum.Integration.Tests`; `docs/`; `CLAUDE.md`.

## What the code already gives us
- `ModelEndpoint` is already a self-contained resource: entity, `/api/model-endpoints` CRUD, probe (`ModelEndpointProbe`), key protector (`ApiKeyProtector`), `BlockingReference` conflicts for delete and for the tools rule (spec 013 D-19). It moves almost as it is; what is new is capabilities, scope, prefix, dimensions and the usage guards.
- `AssistantChat` resolves endpoints **without the tenant filter** (`ModelEndpoints.IgnoreQueryFilters()`) and `VisibleAgents()` implements the scope rule; the models follow the same two patterns, so D-8 and D-9 are an extension of existing behaviour.
- `IEmbeddings` is the one cross-module seam for embeddings (KB search engine and `StoreProbe` use it); it changes from `(text, modelName)` to `(text, modelId)`. The KB search already groups queries by `(model, prefix)` before embedding: the key becomes the model id.
- The Data Protection purpose of the key is a plain string constant; keeping `Quorum.Agents.ModelEndpoint.ApiKey` in the Models module makes stored ciphertext readable without re-encrypting (D-17).
- `ModuleMigrator<TContext>` applies each module's migrations in registration order at startup; `Program.cs` documents that module order is startup order. Models is registered after Tenancy.
- Telemetry sources are named in `QuorumActivityNames` (`AgentsSource`, `KnowledgeBasesSource`, `ConversationsSource`); the embedding span lives in the Agents source today.
- Migrations per module today: Tenancy 4, Agents 18, KnowledgeBases 10, Conversations 3. Licensing is an empty project with no database (spec D-25 corrected accordingly).
- Not yet merged: PR for spec 019; spec 018 (assistants and tool selection) is approved with its plan and adds a **debug model-endpoint override** to Conversations and to `IChatAssistantAccess` (see Order).
- The working tree of `refactor/query-volume-review` has uncommitted changes in `AgentsModule`, `AssistantChat`, `AgentInput`, `AgentToolsEndpoints`, `ToolPool` and the resolvers, the same files PR 2 touches. Implementation PRs start from `main`; rebase if that refactor lands first.

## Decisions
| Topic | Choice |
|---|---|
| Strangler order | Models backend first, with nothing depending on it; then Agents move over; then KnowledgeBases; then the squash. Each PR is green on its own with fresh Testcontainers databases. |
| Capabilities | `[Flags] enum ModelCapabilities { Chat, Tools, Embeddings }`, stored as text in one column (`"Chat,Tools"`); the rules (Tools needs Chat, at least one) live in `ModelRules`, moved from `Agents.Contracts.ModelEndpointRules`. |
| Scope | `ModelScope { Global, Tenant }` + `ScopeTenantIds` (`jsonb` list like agents). `ModelVisibility.For(tenantId)` is the single visible-set predicate (owned, Global, listed), used by list, get and the consumers' checks, like `VisibleAgents`. |
| Contracts surface | `IModelCatalog` (list visible by capability, get, `CheckUsableAsync(modelId, required capabilities, consumer scope)`, `ResolveAsync(modelId)` returning URL, provider name, unprotected key, budget, capabilities for the chat client), `IEmbeddings.EmbedQueryAsync(text, modelId)`, `IModelProbe`, `IModelProvisioning` (create/update the chat model of the assistant facade, exempt from the tools check, D-16), `IModelUsageSource` + `ModelUse` (kind, id, name, owner tenant, scope, scope tenants, required capabilities). Names are indicative; no MEAI/OpenAI type crosses the Contracts. |
| Guards in one place | `ModelUsageGuard` in Models takes `IEnumerable<IModelUsageSource>` and answers D-10 (delete), D-11 (capability removal: any use whose required capabilities are no longer a subset) and D-12 (scope narrowing: any use whose owner/scope is no longer covered) with one `BlockingReference` list. Consumers only report their uses. |
| Spec 018 override is not a "use" | A debug conversation's model override is not a usage source: deleting that model is not blocked and the turn fails with a clear message, as plan 018 already decides for endpoints. |
| Read policy | `models.manage` (writes) and `models.read` (lists, get) are policies registered in Tenancy next to the others; `models.read` is `RequireRole(Admin)` today, which equals the union of `models.manage`, `agents.manage` and `knowledge_base.manage`; a comment says so. |
| Non-owner view | One `ModelResponse` with `baseUrl`/`hasApiKey` nullable and `owned`; the projection leaves them null unless `TenantId == current`. |
| Telemetry | `QuorumActivityNames.ModelsSource` and `ModelsTelemetry` in Models; the embedding span keeps its name and tags and moves to this source (registered where the others are). Dashboards that filter by source must follow (spec 016). |
| Transitional migrations | PR 2 and PR 3 add ordinary migrations (rename column, drop `agents.model_endpoints`, drop KB columns). They are throwaway: PR 4 replaces every module's history. Until PR 4, no real database is upgraded in place (see Risks). |
| Squash | `dotnet ef migrations remove` of all steps, then `migrations add Initial` per module with the module's design-time factory; the old `AgentsMigrationTests`-style tests of individual steps are deleted or rewritten as a single "migrates and has no pending model changes" test per module. |
| Restore scripts | Written against the **final** schema after PR 4, kept outside git (`scripts/upgrade-019/`, listed in `.git/info/exclude`), verified on a database produced by the v0.17.0 release. |
| Test strategy | Integration tests with Testcontainers and the fake OpenAI-compatible endpoint, one test per AC named after it; fixtures create models through the Models API instead of endpoints. Full suite only at the end of each PR; earlier runs only for impacted components. |

## Order and parallel work
- **Order with spec 018** (decided): **spec 018 is implemented first.** Plan 018 writes its debug override as `OverrideModelEndpointId`, `ChatTurnOptions.ModelEndpointId`, `debug.modelEndpointId` and an endpoint selector in the portal Chat page; PR 2 of this plan renames them (step 17). PR 1 of this plan can be developed while 018 is in progress, but PR 2 starts only after 018's PR 4 (Conversations) is merged.
- **PR 1** first: standalone, nothing depends on it yet.
- **PR 2** depends on PR 1. **PR 3** depends on PR 1 and can start in parallel with PR 2 once the Contracts of PR 1 are merged, but merges after PR 2 (both extend `ModelUsageGuard`'s registered sources and both touch the portal).
- **PR 4** (squash) is the last code PR: it must come after every PR that adds a migration, including all of spec 018's (Agents `AddChatExposure`, Conversations `AddConversationSelection`) and any other work in flight.
- **PR 5** (documentation) closes the plan; the **restore scripts** are a delivery step after PR 4, not a PR.
- Spec and plan are on `docs/spec-019` with their own PR; implementation PRs start from `main` after it is merged. Every PR targets `main`.

## Acceptance criteria map
| AC | PR |
|---|---|
| AC-5, AC-6, AC-12, AC-13, AC-14, AC-15 | 1 |
| AC-7 to AC-10 (guard logic with a fake usage source) | 1 |
| AC-1, AC-16, and AC-7 to AC-11 for agents | 2 |
| AC-2, AC-3, AC-4, AC-17, and AC-7 to AC-11 for knowledge bases | 3 |
| AC-18 | 4 |
| AC-19, AC-20, AC-21 | Delivery (manual) |

## Steps

### PR 1: Models module (`feat/019-models-module`)
1. Projects `Quorum.Modules.Models` and `Quorum.Modules.Models.Contracts` (same csproj conventions, `InternalsVisibleTo` for tests), added to `Quorum.slnx` and referenced by `Quorum.Host`; `AddModelsModule()` registered after Tenancy in `Program.cs`, assembly added to `MapEndpoints`.
2. Domain `Model` (name unique per tenant, connection, capabilities, `ContextTokenBudget?`, `QueryPrefix?`, `EmbeddingDimensions?`, scope) with factory and `Configure`/`Rename`/`ReplaceApiKey`/`Rescope`; `ModelRules` (name, URL, provider name, budget, capabilities, prefix length, dimensions bound, scope) moved from `ModelEndpointRules`.
3. `ModelsDbContext` (schema `models`, table `models`, tenant filter, unique `(TenantId, Name)`, `ScopeTenantIds` jsonb), `ModelsDbContextFactory` for design time, migration `Initial`; registration through the same extension the other modules use.
4. `ApiKeyProtector` with the **same purpose string**; `QuorumActivityNames.ModelsSource`, `ModelsTelemetry`.
5. `ModelProbe : IModelProbe` (move of `ModelEndpointProbe`: reachability, model list, tools detection) plus the embedding probe (embeds a fixed text, returns dimensions, stable `embeddings.*` codes); a result with `suggestedCapabilities`, each probe independent (spec Edge Cases).
6. `ModelEmbeddings : IEmbeddings` (move of `OpenAiEmbeddings`): `EmbedQueryAsync(text, modelId)` resolves the model without the tenant filter, requires `Embeddings`, prepends the prefix, keeps the wire-format policy and error mapping; `embeddings.no_endpoint` becomes `embeddings.no_model`.
7. `Features/Models`: `GET/POST /api/models`, `GET/PUT/DELETE /api/models/{id}`, `POST /api/models/test`; validation per D-2/D-3/D-6/D-7; `ModelVisibility`; reduced view for non-owners; `ModelUsageGuard` wired into PUT and DELETE (409 `model.in_use`, `model.capability_in_use`, `model.scope_in_use` with the `BlockingReference` list).
8. Contracts: `IModelCatalog`, `IEmbeddings`, `IModelProbe`, `IModelProvisioning`, `IModelUsageSource`, `ModelUse`, DTOs; no consumer yet. `CheckUsableAsync` implements D-8.
9. `QuorumPolicies.ModelsManage`, `QuorumPolicies.ModelsRead`; registered in `TenancyModule`.
10. Portal: `Models.razor` at `/models` and `/models/{Id}` (list with capability chips and scope, form with capability checkboxes, fields by capability, scope selector reusing the agent one, connection test with suggestions and dimensions, read-only rows for other tenants), `ModelConnectionForm` reused; a new top-level nav entry "Models" between Chat and Agents; `QuorumApi.Models.cs`. The old Model endpoints entry and page stay until PR 2.
11. Tests: AC-5, AC-6, AC-12, AC-13, AC-14, AC-15 end to end; AC-7 to AC-10 against `ModelUsageGuard` with a fake `IModelUsageSource`; embedding by id with the prefix and the stable codes; the ciphertext written with the old purpose decrypts.

### PR 2: Agents move to Models (`feat/019-agents-on-models`, `feat!`)
12. Agents domain and API: `Agent.ModelEndpointId` -> `ModelId` (`AgentInput`, `AgentEndpoints`, responses, declarative reference); no FK; validation through `IModelCatalog.CheckUsableAsync` (visible, `Chat`, `Tools` when tools or sub-agents, scope not broader than the model, D-8); spec 013 D-12 untouched.
13. `AssistantChat` and resolvers: replace the `ModelEndpoints` reads with `IModelCatalog.ResolveAsync` (one batched call for the sub-agents of a turn); `EndpointConfiguration` is built from the resolved connection.
14. `AgentsModelUsage : IModelUsageSource` (agents with their owner, scope, required `Chat`, `Tools` when they have tools or sub-agents); registered in `AgentsModule`. Agent scope narrowing and tool attachment keep their checks and now consult the catalog for `Tools`.
15. Settings facade and provisioning: `AssistantSettingsEndpoints` and `AssistantProvisioning` create and update the root agent's model through `IModelProvisioning` (capabilities `Chat` and, when detected, `Tools`; exempt from the tools rule, D-11/D-16); Tenancy `SetupValidation`, `TestModelConnection`, `SetupEndpoints` use `Models.Contracts` (`IModelProbe`, `ModelRules`).
16. Remove from Agents: `ModelEndpoint`, `ModelEndpointEndpoints`, `ModelEndpointProbe`, `ApiKeyProtector`, `OpenAiEmbeddings`, `IModelEndpointProbe`, `IEmbeddings` and `ModelEndpointRules` from Agents.Contracts; `AgentsDbContext` loses the entity and the FK; migration `MoveModelsToModelsModule` (rename the column, drop the FK, drop `model_endpoints`; transitional).
17. Spec 018 is already in `main` (Order): rename `OverrideModelEndpointId` (Conversations migration), `ChatTurnOptions.ModelEndpointId`, `IChatAssistantAccess` override validation (through the catalog: visible, `Chat`, `Tools` when needed), `debug.modelEndpointId` -> `modelId`, and the Chat page selector (lists `/api/models?capability=chat`).
18. Portal: agent form selects among chat models; remove `ModelEndpoints.razor`, its nav entry and API calls; redirect `/agents/endpoints` -> `/models`; `GettingStarted` and the `Setup` redirect point to `/models`; `AgentApiModels`, `ApiModels`, `QuorumApi.Agents` renamed.
19. Tests: fixtures (`AssistantFixtures`, `QuorumApp`) create chat models through the Models API; update `AgentApiTests`, `AgentListTests`, `DelegationTests`, `AssistantApiTests`, `AssistantSettingsTests`, `SetupCompletionTests`, `SkillApiTests`/`SkillRuntimeTests`, `ToolLoopTests`, `RealOllamaTests`, `AgentPagesTests`, `ChecklistWiringTests`; delete `ModelEndpointTests`; rewrite the endpoint parts of `AgentsMigrationTests`. New: AC-1, AC-16, AC-7 to AC-11 with agents as the usage source (including Global agent -> non-Global model refused, shared agent running on the owner's model, no URL or key for another tenant).

### PR 3: Knowledge bases move to Models (`feat/019-kb-on-models`, `feat!`)
20. KB domain and persistence: `EmbeddingModel` + `QueryPrefix` -> `EmbeddingModelId` (required, no FK); migration `UseEmbeddingModelId` (transitional: drops both columns, adds the column; fresh databases only, see Risks).
21. Registry, inspect and verification: `KnowledgeBaseContracts`, `InspectContracts`, `KnowledgeBaseRegistrar`, `KnowledgeBaseInspector`, `ConnectionRules`, `StoreProbe` take `embeddingModelId`; validation through `IModelCatalog.CheckUsableAsync` (visible to the KB owner per D-8, `Embeddings`); the detail also returns the model name.
22. `KnowledgeBaseSearchEngine`: group by `EmbeddingModelId`, call `EmbedQueryAsync(text, modelId)` once per distinct model (the prefix is applied inside Models).
23. `KnowledgeBasesModelUsage : IModelUsageSource` (KB owner, scope, required `Embeddings`); registered in `KnowledgeBasesModule`.
24. Portal: `KnowledgeBaseDraft`, `KnowledgeBaseWizard`, `KnowledgeBaseEditor`, `KnowledgeBases.razor`, `KnowledgeApiModels`: replace the text and prefix fields with a list of embedding models, empty state linking to `/models`, model name in the list and detail.
25. Tests: AC-2, AC-3, AC-4, AC-17 and the KB side of AC-7 to AC-11 (a Global KB searched by another tenant embeds with the owner's model and key); update `KnowledgeBaseApi`, `InspectApi`, the golden `inspect.json` and the KB fixtures; the golden file changes in this PR only.

### PR 4: Squash migrations (`build/019-squash-migrations`)
26. For Tenancy, Agents, KnowledgeBases, Conversations and Models: remove every migration and snapshot, regenerate `Initial` from the final model with `dotnet ef migrations add Initial` (each module's design-time factory); check the generated SQL for the schema, indexes, jsonb and defaults against the old snapshot.
27. Replace the per-step migration tests with one test per module: applies on an empty database and `HasPendingModelChanges()` is false (AC-18); drop tests that exercised removed migrations.
28. Smoke: start the Host on an empty database through the base `docker-compose.yml`, run the first-run wizard, create a model, an agent and a KB.

### PR 5: Documentation (`docs/019-docs`)
29. `docs/product.md` (glossary: model replaces model endpoint, capabilities, prefix), `docs/database-schema.md` (schema `models`, changed columns), `docs/knowledge-bases.md` (embedding model by id), `docs/deferred.md` (the "Embedding endpoint per knowledge base" row is resolved; add the spec's deferred decisions), `CLAUDE.md` (architecture list gains Models, `Add<Module>Module` order). Roll the plan status to Implemented. The PR title or last commit of PR 4 carries `BREAKING CHANGE` so release-please writes it in the changelog (never by hand).

### Delivery: restore scripts (not a PR)
30. After PR 4, write `01-before-start.sql` (rename each module schema to `<schema>_old`, with its `__EFMigrationsHistory`) and `02-restore.sql` (single transaction; refuse when the new tenant table is not empty, AC-21; stop before writing when a KB's tenant has no endpoint and list them, AC-20; copy every table with explicit column lists in dependency order; apply D-27 for models, KB links and scopes; `models.models` ids equal the old endpoint ids; the key ciphertext is copied as is; the 018 override column too, if present).
31. Verify on a database produced by the v0.17.0 release (worktree at tag `v0.17.0`, base `docker-compose.yml`, never the dev compose): seed data in every module, run script 1, start the new build, run script 2, then compare row counts and relations, open the portal, and chat on a real model (AC-19). Deliver the two files to the user in the conversation, never committed.

## Risks
- **Interim state of main**: between PR 2 and PR 4 a real database cannot be upgraded in place (transitional migrations drop `model_endpoints` and the KB columns without copying). Mitigation: develop on scratch databases, take the upgrade of the maintainer's database only once, after PR 4, through the scripts; if a release is cut in between, the changelog must say so.
- **Spec 018 collision**: it adds migrations and an endpoint override that this plan renames; the squash must come after both. If 018 and 019 progress in parallel, PR 4 is held until 018's last migration lands.
- **Contracts break**: `IEmbeddings` and the probe move out of Agents.Contracts and are referenced by KnowledgeBases and Tenancy; PR 1 adds the new ones, PR 2 and PR 3 switch the callers and PR 2 deletes the old, so no PR leaves a dangling reference.
- **Scope rule on restored data**: existing Global agents and KBs used tenant-only endpoints; D-27 widens the restored model's scope. A wrong union would make the new D-8 check reject a later save of that agent or KB; AC-19 checks the scope of every restored model.
- **Telemetry source**: the embedding span changes source; dashboards or queries filtering on the Agents source lose it (spec 016). Update them in PR 1 or note it in the PR.
- **Restore script correctness** is only verified by hand and is not in CI; explicit column lists and the single transaction limit the damage, and the old schemas stay as `*_old` until the maintainer drops them.
- **Refactor in flight** on `refactor/query-volume-review` (see above).

## Open Questions
None. Decided with the maintainer: spec 018 first (Order); development on scratch databases between PR 2 and PR 4, with the upgrade done once through the scripts (Risks).
