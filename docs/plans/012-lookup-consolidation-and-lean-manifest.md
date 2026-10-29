# Plan 012: Lookup Consolidation and Lean Manifest

**Spec:** `docs/specs/012-lookup-consolidation-and-lean-manifest.md` (Approved 2026-10-29)
**Architecture:** Modular Monolith; Vertical Slice inside each module.
**Mode:** one implementation PR (`feat/012-lookup-consolidation`) built in the phases below, in order. Tests are kept to the minimum that covers each acceptance criterion once and are run only at the end, when the implementation is complete (see Verification). Development starts only after this plan is approved.
**Status:** Draft
**Affected:** KnowledgeBases (Domain, Persistence, Features/Lookups, Storage adapters, Contracts), Agents (Domain, Features/Tools, Tools/Lookup, Chat, Contracts), `Quorum.Web.Client` (lookups, search tools, assistant tools), `tools/Quorum.SampleKnowledgeBase`, `tests/Quorum.Integration.Tests`, docs.

## Decisions
| Topic | Choice |
|---|---|
| Ownership | **KnowledgeBases** owns the lookup model (name, description, storage, attributes), the rows, the snapshot query engine and the live reads. **Agents** owns `lookupFilters`, the exposure rule, the `Manifest` of the activation and the lean functions. Agents never reads lookup tables; it builds `describe` and `query` from `IKnowledgeBaseLookups`. |
| Contracts | `IKnowledgeBaseLookups` gains: filters, attribute sort and a row count on `ReadAsync`; `Storage` and `Attributes` on `LookupInfo`; a `DescribeAsync` that returns, for lookups and KBs, the kind, operations, filterable and sortable fields with type and operators, limits and the name of the list that holds a field's allowed values. `Agents.Contracts.ILookupToolUsage` gains the check "which tools filter on or expose this attribute" for `lookup.attribute_in_use`. No `Microsoft.Extensions.*` type crosses a Contracts project. |
| Lookup row | `lookups` gains `Name` (now required for every kind), `Description` (required), `Storage` (`Snapshot` default, `Live` entity only), `Attributes` (jsonb, `[{ key, label?, type, dictionaryFieldId? }]`, at most 10). The unique index `(TenantId, Name)` already exists and becomes the rule of D-2 once every row has a name. The name limit rises from 40 to 100 characters, so that `<kb>.<field>` fits (column length change in the same migration). |
| Attribute keys | A key is `^[a-z][a-z0-9_]{0,39}$`, unique in the lookup. For migrated entity lookups the key is the **existing JSON key** of the stored values (`EntityAttributes.Names`: the field name, with the cluster in front when two share a name), so existing generations stay valid (AC-25) even when that key does not match the rule; the rule applies to new and edited attributes only, and a migrated key that breaks it is kept and flagged in the editor. Keys always travel as SQL parameters, never as text in a statement. |
| Typed values | `LookupValue.Attributes` stays jsonb. `number` is a JSON number, `date` an ISO 8601 string, `textList` an array of strings, `text` a string cut at 200 characters (`EntityAttributes.MaxTextLength`). One `LookupAttributeValue` type does the coercion for the three sources (scan of a store, CSV row, typed row) and is the single place that decides "fits its type". |
| Snapshot engine | One `LookupRowQuery` (search, filters, sort, limit, cursor) is translated to SQL over `lookup_values` of the current generation of the slice. `eq` and `in` use jsonb containment (`@>`), `min`/`max` compare `(Attributes->>key)::numeric` or `::timestamptz`, sort uses the same expressions, with the row `Position` as tie break so the cursor stays stable. The unfiltered, unsorted-by-attribute read keeps going through `LookupCache` (single flight, already fast); any filter or attribute sort goes to SQL. Indexes are chosen in phase 2 by measuring on 50,000 rows (start: GIN `jsonb_path_ops` on `Attributes`; add expression indexes only if a range filter or sort goes over 1 s). |
| Live engine | `LiveLookupOperations.EntityValuesAsync` already scans the store. It gains the filters of D-5 as `FilterCondition`s (the same translation `SearchFilterInputs` gives the rag tool), an exact `CountAsync`, and `search` as a substring test. `ILookupsCapability` gains an `Orders` flag and an optional `OrderBy` on `RecordQuery`; a kind that cannot order is described as such and a `sort` request is an error result that suggests `Snapshot`. Where the store has no substring condition the name is tested in memory over the scanned pages, bounded by `MaxLiveScannedRecords` (50,000) and the 15 s timeout, and the result says it was cut. |
| Switching storage | One transaction: set `Storage`, delete the `lookup_values` of every slice of the lookup, set each slice to `CurrentGeneration = 0`, `Status = Pending`, `NextRefreshAt = null`, evict the cache tags. Back to `Snapshot`: `RequestRefresh` on the slice and the refresh service computes it. The refresh service skips `Live` lookups. |
| Fixed filters | `lookupFilters` is a column of `lookup_tools` (jsonb, additive, default `{}`), validated against the attributes of the lookup. The exposure rule is computed in one place (`LookupExposure`) used by validation (save), the definition builder (serve) and `describe`. A single-valued fixed filter hides the field; a multi-valued one narrows its enum. A tool saved before the spec in the "fixed and exposed" state is served without the parameter and the warnings list it (`LookupToolWarnings`). |
| Lean manifest | `assistant_tools.Manifest` (`varchar(10)`, default `Full`, additive). `LookupToolResolver` builds either the 007 functions or the lean pair from the same binding; hash and policy are shared, so the call-time check of spec 004 is unchanged. `lookup_describe__<name>` and `lookup_query__<name>` use the new origin-independent naming helper next to `ToolNaming.Lookup`. The lean schema is fixed (`query` has 7 parameters, `describe` one array). Validation of `query` is server-side against the target's own description, and the error result is the part of that description that applies. |
| Rounds | `describe` is one round whatever the number of targets; the existing round counter of `AssistantChat` already counts tool rounds, so lean needs no new counting, only a test (AC-18). |
| Audit | Same sink and server name `lookup`; tool names `<name>.describe` and `<name>.<operation>`; values never logged. |
| CSV | Server-side parsing for `import-preview` with **CsvHelper** (an established library, as the project asks), a new dependency of the KnowledgeBases project only, pinned in `Directory.Packages.props`. The admin UI keeps its own client-side parse for paste; the preview endpoint becomes the single source of the proposed types. Type proposal: all values numeric → `number`; all ISO dates → `date`; a column whose cells contain a separator that the admin sets (`;` or `|`) is only proposed `textList` when the admin asks, otherwise `text`. |
| Migration of data | One reversible migration per module. KnowledgeBases `ConsolidateLookups`: new columns, backfill of `Name` (`<kb>.<field>` for computed, with the cluster in front when two fields of the KB share a name; `<kb>` for entity), `Description` (the defaults of D-2), `Storage = Snapshot`, and `Attributes` of entity lookups from the `Mapping` JSON joined with `dictionary_fields` (type mapped to `text`/`number`/`date`; a list field to `textList`). The `entityAttribute` flag stays readable in the stored mapping and is used only to seed the attributes when an entity lookup is created. Agents `AddLookupToolFilters` and `AddAssistantToolManifest`. Each has a test that migrates from the previous snapshot with seeded rows and back. |
| Test infrastructure | `QdrantFixture`, the PgVector fixture and `tools/Quorum.SampleKnowledgeBase` already exist. The sample gains a `vendors` entity collection with `country` (text), `rating` (number), `since` (date), `tags` (list) so that AC-3, AC-4, AC-26 and AC-24 run on real stores; a 50,000 row variant is generated in the index measurement only, not shipped. Time-dependent tests use `FakeTimeProvider`. |
| UI | MudBlazor, logic in plain classes covered by bUnit as in plans 003, 007 and 010: `LookupAdminView.cs`, `ManualLookupForm.cs`, `LookupToolForm.cs`, `Pages/Lookups.razor`, `Pages/LookupTools.razor`, `Pages/RagTools.razor` (merged into one search tools page), `QuorumApi.Knowledge.cs`, `LookupApiModels.cs`, `LookupToolApiModels.cs`. |

## Order and phases
One branch, one PR, squash merge. The phases are the order of work; each ends with a compiling solution, not with a test run.
- Phases 1 and 2 first (model, then snapshot engine). Phases 3 and 4 are independent of each other. Phase 5 needs 2 and 4, phase 6 needs 5, phase 7 needs 6, phase 8 needs 3, 4 and 7, phase 9 is last.
- Each migration is generated against the current `main` snapshot and kept reversible. The Full / Lean setting reaches the API only in phase 7.

## Verification (once, at the end of phase 9)
1. Tests written along the way are few: one per acceptance criterion where it is cheap to state, plus unit tests of pure logic (`LookupAttributeValue`, filter translation, exposure rule, lean validation). No exhaustive matrices, and nothing is run during the phases.
2. `dotnet build -c Release`, `dotnet format --verify-no-changes`, `get_diagnostics` via Roslyn MCP.
3. Important tests, filtered by namespace: lookup model, snapshot queries, fixed filters, lean engine and manifest. Fix.
4. Slow ones, filtered: Qdrant and PgVector live lookups (AC-4, AC-5, AC-26), the 50,000 row index measurement, the UI bUnit and client tests, the end-to-end check with LiteLLM.
5. The full suite, once, last.

## Acceptance criteria map
| Phase | Acceptance criteria |
|---|---|
| 1 | AC-1, AC-2, AC-8, AC-25, AC-28 |
| 2 | AC-3, AC-9 |
| 3 | AC-6, AC-7 |
| 4 | AC-4, AC-5, AC-26 |
| 5 | AC-10, AC-11, AC-12, AC-13 |
| 6 | AC-15, AC-17, AC-22, AC-23 |
| 7 | AC-14, AC-16, AC-18, AC-19, AC-20, AC-21, AC-24 |
| 8 | AC-27 |
| 9 | End-to-end evidence (no new AC) |

Every AC from AC-1 to AC-28 appears once as main owner.

## Steps

### Phase 1 - The lookup model
1. Domain: `Lookup` gains `Name` (required), `Description` (required), `Storage` (`LookupStorage`), `Attributes` (`IReadOnlyList<LookupAttribute>`, `LookupAttributeType`). Factories take a name and a description; `Computed` and `Entity` write the defaults of D-2 ("Distinct values of *country* in *Contracts*", "Entities of *Vendors*"). `MaxNameLength` 100. Context configuration, the `ConsolidateLookups` migration with the backfill described in Decisions, and its test (seed the old shape, migrate, assert, revert). AC-25.
2. `LookupAttributes` (validation: at most 10, key rule, unique, type, `dictionaryFieldId` only on entity lookups and it must be a mapped field of the KB) and `LookupNames` (collision rule across kinds in the tenant, the default `<kb>.<field>` / `<kb>`). AC-8, AC-28.
3. `ComputedLookups.SyncAsync` creates the entity lookup with attributes seeded from the `entityAttribute` flags and the default name and description; a renamed KB or field does not rename an existing lookup. AC-1, AC-2.
4. `PUT /knowledge-bases/lookups/{lookupId}` (`{ name?, description?, storage?, attributes? }`): `lookup.invalid_name`, `lookup.invalid_attribute`, `lookup.storage_not_supported`, `lookup.name_taken` (409). `storage` is stored here and acted on in phase 4; until then a `Live` request is accepted only for entity lookups and has no effect on reads, so phase 1 rejects `Live` with `lookup.storage_not_supported` and phase 4 opens it. Manual create/update take `description` as required (400 when absent). Rows of `GET /knowledge-bases/{id}/lookups` and `/manual-lookups` gain `name`, `description`, `storage`, `attributes[]`. AC-1, AC-2.
5. `IKnowledgeBaseLookups.ListAsync` returns `Description`, `Storage`, `Attributes` in `LookupInfo` (additive); the Agents editor and the lookup tool still use the 007 shape. `lookup.attribute_in_use` is wired in phase 5 where tools gain filters.

### Phase 2 - Typed rows and the snapshot query engine
6. `LookupAttributeValue`: coercion of a scanned JSON node, a CSV cell and a typed value to the stored form, with the "does not fit" error naming the key. The computer (`EntityAttributes.ToValue`) now builds the stored attributes from the lookup's attribute definitions instead of the mapping flags, keeping the existing keys. A scan value that does not fit its type is stored absent, never as a wrong type.
7. `LookupRowQuery` and `LookupRowSql`: search, filters (`eq`, `in`, `min`, `max`; `textList` contains), sort by name / attribute / count, limit, cursor, row count. The cursor carries the sort key and the position. `LookupReader` routes to SQL for any filter or attribute sort; the unfiltered paths keep the cache. Unit tests for the translator, integration tests against PostgreSQL. AC-3.
8. `GET /knowledge-bases/lookups/{lookupId}/values`: `filters` (JSON) and `sort=<attribute>`; typed `attributes` in the answer; `lookup.invalid_filter` (unknown attribute, operator not valid for the type, value not fitting). `ReadAsync` of the contract takes `Filters` and returns the matching count. AC-3.
9. Indexes: generate the 50,000 row set in a throwaway measurement test, run the filter, range and sort cases with and without the GIN index and with expression indexes, keep the cheapest set that keeps each under 1 s, write the numbers in this plan ("As built") and add the migration `AddLookupValueAttributeIndexes`. The ceiling (`MaxValuesPerLookup`, `truncated`) is unchanged and tested at the maximum. AC-9.

### Phase 3 - Manual lookups with attributes
10. `POST` and `PUT /knowledge-bases/manual-lookups` carry `attributes[]` and `values[].attributes`; values are validated with `LookupAttributeValue`; `lookup.invalid_value` names the row and the column; a manual lookup is always `Snapshot` (`storage = Live` is 400 `lookup.storage_not_supported`). AC-6.
11. `POST /knowledge-bases/manual-lookups/import-preview` (multipart CSV via CsvHelper): detected columns, proposed type per column, first rows, the limits of 007 (rows, duplicate key) and of D-3 (10 attribute columns); `lookup.invalid_csv`. A failing row fails the whole import and nothing is saved. AC-7.
12. `ManualLookupValidation` and `ManualLookupEndpoints` lose their private value rules in favour of the shared ones; the 007 tests stay green unchanged.

### Phase 4 - Live storage mode
13. `ILookupsCapability`: typed attributes in `ScanAsync` rows (checked and fixed per adapter, a test per kind: number stays number, date stays an ISO string, a list stays an array), `Orders` flag and `OrderBy` on `RecordQuery`. Qdrant orders with `order_by` (range index required, the `Failed`-style message names the field), PgVector with `ORDER BY`. If an adapter changes behaviour, a short amendment is added to the spec of that kind (008). AC-26.
14. `LiveLookupOperations`: entity rows in `Live` mode with filters, search, count, sort (only when the store orders), cursor, the 15 s timeout, and the error result that suggests `Snapshot`. The read path of `KnowledgeBaseLookups` picks the engine by `Storage`. AC-4.
15. The storage switch of Decisions (`PUT .../lookups/{id}` with `storage`): rows and generations deleted at once, cache tags removed, refresh service skips `Live`; back to `Snapshot` makes the slice due. A read in progress finishes with what it read. AC-5.
16. Sample `vendors` collection in `tools/Quorum.SampleKnowledgeBase`; tests on Qdrant and PgVector for AC-4 and AC-26.

### Phase 5 - Fixed filters and exposure
17. `LookupTool` gains `LookupFilters` (migration `AddLookupToolFilters`); `POST` and `PUT /tools/lookup` accept and validate them (`lookup_tool.invalid_filter`) with the operators of D-5. `ILookupToolUsage` answers which tools use an attribute; `PUT .../lookups/{id}` refuses removing or retyping it (`lookup.attribute_in_use`, listing the tools). AC-12, AC-13.
18. `LookupExposure`: single value hides, several narrow to an enum. Save refuses an exposed fixed field (`lookup_tool.field_fixed`); the definition builder and `LookupToolWarnings` handle the legacy state (served without the parameter, warning in `GET`). A call outside the enum is an error result. AC-10, AC-11.
19. `lookupFilters` and the exposure rule apply to both storage modes and create no slice (a condition on rows). The 007 `fixedFilters` on dictionary fields keep their behaviour for KBs and computed lists. AC-12.

### Phase 6 - The lean engine
20. `IKnowledgeBaseLookups.DescribeAsync` and the Agents-side `LookupDescriber`: kind, operations, filterable fields with type and operators, sortable fields, limits, restriction of the fixed filters, and for a field with allowed values the name of the list (never the values). A computed list lists value and count, `search`, `sort` (name, count) and no attributes; a snapshot not ready is reported as such. AC-15, AC-22.
21. `LookupQueryService`: `query(target, operation, filters?, search?, sort?, limit?, cursor?)` validated against the target's description; `values` and `count` for lookups, `documents` and `count` for KBs; `count` on a computed list counts values that match, not documents; results keep the format and the 6,000 character cap. The error result carries the part of the target's description that applies and the valid targets for an unbound one. AC-17, AC-22.
22. A KB whose kind lacks the capability is skipped and named, as in 007. AC-23. Both services are exercised directly in tests and are not yet offered to the model.

### Phase 7 - Lean manifest on the activation
23. `AssistantTool.Manifest` (migration `AddAssistantToolManifest`, existing rows `Full`); `PUT /assistants/{id}/tools` accepts `manifests`, keeps the current value of tools not in the map, 400 `tool.manifest_not_supported` for a non-lookup tool; `GET` returns `manifest` per lookup tool. AC-19, AC-20.
24. `LookupToolDefinition` builds the lean pair and the tool description (lookups and KBs with name and description); `LookupToolResolver` chooses by manifest; `LookupQuorumTool` routes `describe` and `query` to the phase 6 services, with the audit entries `<name>.describe` and `<name>.<operation>`. `describe` takes several targets and counts as one round (test over `AssistantChat`). AC-14, AC-16, AC-18.
25. `GET /tools/lookup/{id}` returns `definitions: [{ manifest, functions, estimatedTokens }]` for both manifests (the estimator of spec 004 D-27). A test on the sample project, a tool with three lookups and two KBs, asserts `lean < full`; the two figures are written in this plan. AC-21, AC-24.

### Phase 8 - Admin UI (lookups, search tools page, lean switch)
26. One table of lookups of the three kinds with badges for origin, storage mode and freshness (`Ready`, `Stale`, `Preparing`, `Failed`); the editor of name, description, storage switch (with the warning that `Live` deletes the stored copy) and attributes (key, label, type, mapped field). Knowledge base wizard: the attribute pick moves to the lookup editor. bUnit for the view logic. AC-27 (part).
27. Manual lookup form: attributes and typed columns; CSV import through `import-preview` with the proposed types and the confirmation step, naming the failing row and column. AC-27 (part).

#### Search tools page and lean switch (phase 8)
28. One page for search tools with **RAG** and **Lookup** tabs (`RagTools.razor` and `LookupTools.razor` merge; the editor keeps its layout; routes redirect; the nav entry is one). Design per spec 010.
29. Lookup tool editor: `lookupFilters` per entity or manual lookup, the `field_fixed` refusal and the legacy warning, and the estimated tokens of both manifests side by side. Assistant tools page: the Full / Lean switch per lookup tool. AC-27.

### Phase 9 - Documentation and verification
30. `docs/knowledge-bases.md` (roles of D-1, entity lookup as a table, `Live` versus `Snapshot`, what `scan` must return), `docs/database-schema.md` (new columns, `lookup_tools.LookupFilters`, `assistant_tools.Manifest`), `docs/deferred.md` (the Deferred Decisions of the spec; the ETL endpoint item stays), README of the sample project.
31. End-to-end check with a real model through the LiteLLM proxy on :4000 with a scoped key (project memory), small model and the larger one, `full` and `lean`, on the questions of plan 007 step 23 plus "the rating of ACME" and "the vendors of Italy rated 4 or more"; record what the model got wrong and the token figures in this plan. The app runs from the base `docker-compose.yml`.
32. The Verification section above, in that order.

## Risks
- **Backfill of names and attributes.** Two computed lookups can collide on `<kb>.<field>` (same field name in two clusters), and a migrated attribute key may break the new key rule. Mitigation: the cluster prefix then a numeric suffix for names; migrated keys are kept and flagged; the migration test seeds both cases.
- **jsonb range queries.** `(Attributes->>key)::numeric` fails on a malformed row. Mitigation: `LookupAttributeValue` is the only writer and stores only fitting values; the cast is guarded by `jsonb_typeof`. Index choice is measured in phase 2, not assumed.
- **Live `sort` and `search` depend on the store.** Qdrant `order_by` needs a range index and has no substring match on a name without a text index. Mitigation: the `Orders` flag and the description tell the model; the fallback is `Snapshot`; the in-memory name test is bounded and reports a cut.
- **Scan types.** If an adapter returns numbers as strings, attribute filters on `Live` silently miss rows. Mitigation: a per-adapter contract test in phase 4 (AC-26) before anything else uses the types.
- **Switching to `Live` is destructive by design** (D-4). Mitigation: explicit warning in the UI, a test that no row remains, and the switch back is a background recompute.
- **Hiding a field a conversation already used** (edge case of the spec). Mitigation: the rejected call returns the schema of the target; covered by a test.
- **Lean mode on small models.** A model may skip `describe`. Mitigation: the error result already carries the applicable schema, so one failed `query` teaches the shape; the end-to-end check in phase 9 records it.
- **Size of phase 7.** `LookupQuorumTool` is already 700 lines. Mitigation: the lean tool is a separate class sharing the result formatting; phase 6 keeps the logic out of the tool.
- **Migration conflicts** in the `knowledge_base` and `agents` contexts with other open specs. Mitigation: rebase and regenerate before opening each PR; keep each reversible.
- **Release analyzers.** CI builds Release; the PR is built in Release before it is opened (project memory).

## Details beyond the spec to confirm
- The name limit rises from 40 to 100 characters (needed for `<kb>.<field>`); `lookup_tools` aliases keep their own limit.
- The `entityAttribute` flag of the mapping stays readable but only seeds the attributes at creation; after that the lookup owns them (D-3). Clients that still send the flag keep working.
- phase 1 rejects `storage = Live` until phase 4 opens it, so no release holds a switch that does nothing.
- Live `search` is a substring test of the name, in the store when it can and in memory over the bounded scan otherwise; a cut result says so.
- A migrated attribute key that does not fit the new key rule is kept as it is, because renaming it would invalidate the stored generations.
- CSV import uses CsvHelper on the server; the paste path of the UI keeps its client-side parse and also goes through `import-preview` before saving.
- `textList` columns in a CSV are proposed only when the admin names the separator.

## Open Questions
None.
