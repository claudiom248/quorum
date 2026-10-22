# Plan 008: pgvector Knowledge Bases

**Spec:** `docs/specs/008-pgvector-knowledge-bases.md` (Approved 2026-10-22, Amendment 1 approved 2026-10-22)
**Architecture:** Modular Monolith; Vertical Slice inside the KnowledgeBases module.
**Mode:** `/tdd`: every acceptance criterion becomes a failing test first.
**Status:** Approved (2026-10-22)

## Decisions
| Topic | Choice |
|---|---|
| Where the coupling is today | `KnowledgeBase.Connection` is a `QdrantConnectionSettings`; `ConnectionRules`, `StoreProbe`, `KnowledgeBaseInspector`, `KnowledgeBaseRegistrar`, `KnowledgeBaseSearchEngine`, `FilterTranslator`, `MappingValidation.Vector` and the DTOs of `KnowledgeBaseContracts` / `InspectContracts` name Qdrant types (`QdrantConnection`, `QdrantException`, `QdrantCollectionInfo`, `QdrantCondition`). Everything else (mapping, discovery, `ChunkReader`, `PayloadSampler`, `JsonSchemaReader`, dictionary, visibility, rag tool) already works on JSON rows and dictionary types and is reused as it is |
| The contract (PR 1) | An internal `IStoreAdapter` per `KnowledgeBaseKind`, found through a `StoreAdapters` registry. It owns what is kind-specific and nothing else: reading and validating the `connection` object of a request (`JsonElement`), the rule that keeps a stored secret, turning input into stored settings (secrets protected) and stored settings into an open connection (secrets read), the probe of the source (reachability, existence, vectors, indexed paths, record count, sample), the check of the chosen vector, the search, the mapping of failures to neutral outcomes and codes, the description of the connection for responses, and the sample size. The shared parts stay shared: the dimension check against an embedding, discovery, mapping, the search engine's interleaving, thresholds and status handling |
| Neutral types | Moved out of `Storage/Qdrant` into `Storage`: `StoreFailure` and `StoreException` (the six outcomes of D-14 plus rate limiting for kinds that need it), `VectorInfo`, `SourceInfo` (vectors, indexed paths, record count), `StoreHit` (score and a `JsonObject` row), and the filter conditions (`MatchAny`, `Boolean`, `NumberRange`, `DateRange`, and the new `DocumentIds`). `QdrantConnector` keeps its code and returns the neutral types. The Qdrant warning and error codes (`WarningCodes.Qdrant*` and the rest) are kept as they are, because AC-1 says nothing observable changes |
| Capabilities | `IStoreCapability` is a marker; an adapter returns `Capability<T>()` or `null`. Qdrant and PgVector declare none in this plan. Spec 007 adds the lookups capability on top; its mapping validation turns a missing capability into `knowledge_base.lookups_not_supported` (007 AC-3), so the refusal of 008 A1-4 needs no code of its own here, only the adapter declaring nothing |
| Stored settings | The `Connection` column is already `jsonb NOT NULL` holding `{ Url, Collection, VectorName, ProtectedApiKey }`. `KnowledgeBase.Connection` becomes a small `ConnectionSettings` wrapper over a `JsonObject` with a value converter to the same `jsonb`; each adapter reads and writes its typed record from it. **No data migration:** rows keep their shape, and a PgVector row simply holds other members. The EF model snapshot changes (the owned `ToJson` goes away), so `dotnet ef migrations add` runs once and the migration must come out empty; a test starts from a row inserted in the old shape and reads, verifies and searches it |
| Request and response shape | `connection` in requests is a `JsonElement` that the kind's adapter reads; in responses it is the adapter's description (for Qdrant the same members as today, so the Web client does not change in PR 1; the golden JSON of the existing responses is pinned by a test before the move) |
| Row as JSON | The PgVector connector turns a row into a `JsonObject` (a column is a property; a `json`/`jsonb` column becomes a nested object; `uuid`, `date` and `timestamp` become strings; `numeric` a number; arrays lists), so `PayloadSampler`, `MappingValidation`, `ChunkReader` and the draft mapping are reused unchanged and paths are `column` or `column.key` as the spec says. A column whose name has a dot is reported as not mappable at this step, because the shared readers split paths on dots |
| Driver | **Npgsql** (the official PostgreSQL driver) with `NpgsqlDataSource`; one data source per registered KB in a small cache (sliding expiry, bounded size, disposed on eviction or when the KB's connection changes), a pool maximum of 5 unless the admin's connection string sets one. The query vector goes through the `Pgvector.Npgsql` helper of the pgvector organisation if the PR 3 prototype confirms it handles `vector` and `halfvec` parameters on the pinned Npgsql; otherwise as a `real[]` cast to the column's type. Failures translate from `PostgresException.SqlState` and `NpgsqlException` to the neutral outcomes; the message is never passed on |
| Read-only and limits | Every call opens a transaction, runs `SET TRANSACTION READ ONLY` first, then `SET LOCAL statement_timeout` to what is left of the search limit. Identifiers (schema, relation, columns) come only from names read from the catalog and are quoted with the driver's own quoting; values are parameters |
| Search query | `WITH nearest AS MATERIALIZED (SELECT <mapped columns>, <vector> <op> @q AS distance FROM <relation> WHERE <conditions> ORDER BY <vector> <op> @q LIMIT @k) SELECT ... FROM nearest ORDER BY distance`, with the optional threshold on `distance`. `SET LOCAL hnsw.iterative_scan = relaxed_order` and `ivfflat.iterative_scan = relaxed_order` for pgvector 0.8 or later, which is why the outer `ORDER BY` re-sorts; the PR 5 prototype also tries `strict_order` and keeps the simpler query if recall and time are the same. Score: cosine `1 - distance`, inner product `-distance` (the operator returns the negative), L2 `distance` |
| Tests | `PgVectorFixture` (Testcontainers PostgreSQL on the image of the compose `pgvector` profile, version read from the compose file as `QdrantFixture` does), separate from `PostgresFixture` (the platform database). The sample generator of D-18 is in `tools/Quorum.SampleKnowledgeBase`, with the deterministic embedding of spec 003; the integration tests use it. Per the run-sparingly rule, each PR runs the impacted tests while working and the whole suite once before opening it |
| Docs | `docs/knowledge-bases.md` (guide for data owners) gets the pgvector section in the closing PR; `docs/database-schema.md` is updated where the stored connection members are described; `docs/deferred.md` needs no change (008 and 007 already wrote their rows) |

## Order and parallel work
- **PR 1 comes first and alone.** Spec 007's lookups backend waits for it (007 depends on the contract), and every later PR of this plan builds on it.
- After PR 1 merges: PR 3 to PR 5 are one chain (same files, no parallel writers); **spec 007's backend can run in parallel** with them (it touches the Qdrant adapter, the mapping and Agents); **PR 2 (compose profile) is independent** and can be done at any time; the UI (PR 6) starts when PR 5 is merged.
- Spec 009 (Pinecone) is planned after this plan's PR 1 merges, because its adapter is written against the contract.

## Steps

### PR 1 - The per-kind contract (`refactor/008-store-contract`)
R1. Before touching code, run the impacted tests (`KnowledgeBases/*`, `Rag/*`) and pin what AC-1 promises: a golden test of the JSON of `POST /knowledge-bases/inspect`, `POST` and `GET /knowledge-bases/{id}`, `verify` and `search` for a Qdrant KB (members, codes, messages). Nothing in these expectations changes in this PR.
R2. Neutral types and conditions in `Storage` (see Decisions); `QdrantConnector` returns them; `QdrantCondition` becomes `FilterCondition`. Mechanical, compiler-driven. AC-1.
R3. `IStoreAdapter`, `StoreAdapters` and `QdrantStoreAdapter` (what `ConnectionRules`, `SameServer`, the unprotect/protect of the key, `MappingValidation.Vector`, `QdrantConnector.IsSupported`, the error-to-code switch of the search engine and the sample size do today moves into it). A registration error for a kind without an adapter is a clear startup failure.
R4. `StoreProbe`, `KnowledgeBaseInspector`, `KnowledgeBaseRegistrar`, `KnowledgeBaseSearchEngine` and `FilterTranslator` depend on the adapter and the neutral types. An architecture test fails if anything outside `Storage/Qdrant` references `Qdrant.Client` or `QdrantConnector`. AC-1.
R5. `ConnectionSettings` over `JsonObject` with the value converter; `KnowledgeBase.Create` and `Edit` take the kind; `connection` as `JsonElement` in requests and the adapter's description in responses; migration that must come out empty (checked), plus the test from a row in the old shape. AC-1.
R6. `IStoreCapability` and `Capability<T>()`; a test with a fake capability and a fake adapter that runs the inspector and the search engine over an in-memory store, which shows that nothing in the shared code assumes Qdrant. AC-39 (mechanism).
R7. `DocumentIds` condition (008 A1-2): `SearchInput` gains an optional list; the Qdrant adapter translates it to `MatchAny` on the `documentId` path; a KB without `documentId` mapped is skipped and named like a KB lacking a filter field (003 D-12). Not reachable from the rag tool or the public contracts yet: spec 007 wires that. Engine-level tests. AC-41 (skipping part).

### PR 2 - pgvector compose profile (`build/008-pgvector-profile`)
1. Optional `pgvector` profile in `docker-compose.yml`: a PostgreSQL with the extension (`pgvector/pgvector`, pinned), separate from the platform's database, port published on loopback only, credentials from `.env` with no defaults in the file, a named volume, a health check; the header comment lists the profile with `bundled-db` and `qdrant`. `deploy/compose.sh` is checked to pass the profile through.
2. The tests' compose reader learns the second image; a test that fails if the pinned tag stops being a pgvector one. AC-38.

### PR 3 - Connect, discover and inspect (`feat/008-pgvector-inspect`)
3. Prototype against the dev compose's PostgreSQL (a separate database): the catalog queries (relations with a `vector` or `halfvec` column the user can read, the column type and its dimensions from `atttypmod`, the operator class of the indexes, the extension version, `has_table_privilege`), the cast of a `real[]` and of the `Pgvector.Npgsql` types to both vector types, `vector_dims` on a column without dimensions, the three distance operators and scores. Findings are written below this step, as in plan 003.
4. `PgVectorFixture` and the sample generator of D-18 (relations: flat columns, LangChain-style `jsonb` metadata, `text[]` tags, a filtered column without index, two vector columns, `halfvec`, unspecified dimensions, a different dimension, an empty table, a view, HNSW with cosine, IVFFlat with L2, no vector index, names with capitals and spaces, a dotted column name, a pasteable schema); a CLI option in the sample project. AC-36 (generator part).
5. `KnowledgeBaseKind.PgVector`, the connection model (fields or string, exclusive; `schema`, `table`, `vectorColumn`, `distance`), `PgVectorStoreAdapter` with the secret handling of D-4 and D-16 (`ProtectedPassword` or `ProtectedConnectionString`), the data source cache. AC-2, AC-3, AC-6.
6. `PgVectorConnector`: catalog reads, the read-only transaction helper, row-to-JSON, failure translation. Unit and integration tests per outcome.
7. Discovery for rows: sampling of 100 rows without the vector column and with long values cut, the type table of D-9, non-mappable columns and keys reported (`bytea`, ranges, `interval`, arrays of other types, nested arrays, dotted names), unconstrained dimensions through `vector_dims`, the distance draft from the operator class, unindexed candidate columns, the pasted schema. AC-9 to AC-15.
8. `POST /knowledge-bases/relations` (200 at most, text filter, `truncated`; `knowledge_base.unsupported_operation` for other kinds), `POST /knowledge-bases/inspect` for `PgVector`, `knowledge_base.unsupported_kind` naming the supported kinds. AC-7, AC-8.

### PR 4 - Registry and verification (`feat/008-pgvector-registry`)
9. Create, update, detail, list and delete for `PgVector`: the connection with its secrets (never returned, `hasPassword` / `hasConnectionString`), `knowledge_base.password_required` and the carry-over rule (D-16), `knowledge_base.vector_column_required`, value checks of the mapping against the sample, the shared-KB reduced DTO. AC-4, AC-5, AC-8, AC-32.
10. Verification (D-13): extension and version, relation, vector column and type, dimensions against an embedding, mapped columns and keys, then the warnings that never change the status (no index on the vector column, an unindexed mapped column, pgvector before 0.8, write privileges, the `documentId` column without index per A1-3). A failed check saves the KB `Unavailable` with the cause and its consequence. AC-16 to AC-20, AC-43.
11. Authorization and logs: `knowledge_base.manage` on every route including the new one, nothing sensitive in logs or audit. AC-31, AC-33 (registration half). AC-39 for PgVector (declares no capability).

### PR 5 - Search (`feat/008-pgvector-search`)
12. Prototype of the search query (CTE with `MATERIALIZED`, `relaxed_order` against `strict_order`, filters on a selective condition over an HNSW and an IVFFlat index, thresholds, the score expressions); findings written below this step.
13. The condition translation: text, number, date, boolean and list over plain columns and over `jsonb` keys (extraction only when the JSON type fits), the typed `DocumentIds` condition (text, integer, `uuid` and `jsonb` key; an unconvertible id matches nothing), parameters only. AC-23, AC-24, AC-25, AC-40, AC-41.
14. `PgVectorStoreAdapter.Search`: read-only transaction, statement timeout from the engine's limit, iterative scan settings, the native score and the threshold, mapped columns only. AC-21, AC-22, AC-26, AC-27.
15. Failures and status: every outcome of D-14 reaches the model as a safe error result (no connection string, host, SQL or driver text), the KB goes `Unavailable` and comes back; a rag tool over a Qdrant and a PgVector KB interleaves with sources naming their KB; unusable rows counted. AC-28, AC-29, AC-30, AC-33 (search half).

### PR 6 - Admin UI (`feat/008-ui-pgvector`)
16. API client models for the per-kind `connection`; the registration wizard becomes kind-aware: connection (fields or string), list of relations with a filter, vector column and distance, mapping review by columns and keys, verify, test search. Logic in plain view classes covered by bUnit, as in plan 003 PR 7. The Qdrant flow is untouched. AC-34, AC-35.

### PR 7 - Documentation and verification (`docs/008-closing`)
17. The pgvector section of the guide for data owners (shape of a relation, recommended indexes, a read-only role, choosing the distance, what Quorum reads), `docs/database-schema.md`, README mention. AC-37.
18. Final pass: Release build, full suite, `get_diagnostics`, and a manual run through the LiteLLM proxy with a pgvector sample relation (memory rule: manual tests go through LiteLLM).

## Acceptance criteria by PR
| PR | Criteria |
|---|---|
| 1 | AC-1, AC-39 (mechanism), AC-41 (skipping part) |
| 2 | AC-38 |
| 3 | AC-2, AC-3, AC-6, AC-7, AC-8 (inspect half), AC-9 to AC-15, AC-36 (generator) |
| 4 | AC-4, AC-5, AC-8 (save half), AC-16 to AC-20, AC-31 to AC-33 (registration half), AC-39 (PgVector), AC-42 (by the capability absence, see Decisions), AC-43 |
| 5 | AC-21 to AC-30, AC-33 (search half), AC-40, AC-41 |
| 6 | AC-34, AC-35 |
| 7 | AC-36 (documentation and manual run), AC-37 |

## Risks
- **PR 1 is a large mechanical diff in code with strong tests.** Mitigation: the golden tests of R1 come first; moves and renames before any logic; the architecture test of R4 keeps the boundary from eroding; no behaviour change allowed in the PR, any wish for one goes to a separate PR.
- **The EF snapshot changes when the owned JSON becomes a converter.** Mitigation: the migration must be empty (verified by a test that reads a row inserted in the old shape); if EF insists on altering the column, the plan stops and re-plans before merging.
- **The query vector parameter and `halfvec` on the pinned Npgsql.** Mitigation: the PR 3 prototype decides between the helper package and the `real[]` cast before any adapter code.
- **Filtered approximate search returns too few rows.** Mitigation: iterative scan (D-12), tested with a selective filter on both index types in PR 5; older servers get the verification warning.
- **`jsonb` keys of mixed types.** Mitigation: extraction only when the JSON type fits the filter, a test per type (AC-24).
- **Testcontainers cost.** One shared pgvector container per test run, started on first use like `QdrantFixture`; sample relations are created once.
- **Spec 007 lands in parallel and touches the same adapter interface.** Mitigation: PR 1 defines the capability mechanism and the `DocumentIds` condition so that 007 adds, and does not change, the contract.

## Details beyond the spec to confirm
1. **`DocumentIds` reaches the engine in PR 1 (R7)** for every kind, ahead of spec 007 wiring the rag tool: it keeps the pgvector typing testable here (AC-40, AC-41) and costs three lines in the Qdrant adapter. Alternative: leave it entirely to 007 and test the pgvector translation at adapter level only.
2. **The query vector helper:** `Pgvector.Npgsql` if the prototype confirms it, otherwise the `real[]` cast.
3. **Pool size:** a maximum of 5 per KB when the admin's connection string does not set one (the string is the admin's responsibility, D-4).
4. **Sampling rows in physical order,** as D-10 says: no `TABLESAMPLE`.
5. **A `hnsw.iterative_scan` of `relaxed_order` with an outer re-sort** as the starting point, decided by the PR 5 prototype.
