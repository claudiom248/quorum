# Plan 009: Pinecone Knowledge Bases

**Spec:** `docs/specs/009-pinecone-knowledge-bases.md` (Approved 2026-10-22, Amendment 1 approved 2026-10-22)
**Architecture:** Modular Monolith; Vertical Slice inside the KnowledgeBases module.
**Mode:** `/tdd`: every acceptance criterion becomes a failing test first.
**Status:** Approved (2026-10-22)
**Depends on:** the first PR of plan 008 (Qdrant moved behind the per-kind contract, no change of behaviour), called "008 PR 1" here. PRs that need it are marked **[waits 008]**; the others touch no KnowledgeBases code and can start now.

## Decisions
| Topic | Choice |
|---|---|
| Ownership | Everything is in the **KnowledgeBases** module, a new `Storage/Pinecone/` beside `Storage/Qdrant/`: `PineconeClient` (REST) and `PineconeStoreAdapter`. No new project and no new package. The test server and the sample indexes live in `tools/` and `tests/` |
| REST client | `PineconeClient` over `IHttpClientFactory` with a named client `knowledge-base-pinecone` registered with `AddServerHttpClient` (already no redirects, no request logging, no timeout of its own). Per-call limits through linked tokens: 10 s for listing, inspection and verification, the search limit of the engine for a query. Four calls only: `GET {control}/indexes`, `GET {control}/indexes/{name}`, `POST {host}/describe_index_stats`, `POST {host}/query`. Request and response shapes are our own records (`System.Text.Json`, camel case), covered by the tests below. The header `Api-Key` goes only to the control plane URL and the index host the admin entered or the control plane returned for that index; `X-Pinecone-Api-Version` is the constant `PineconeApi.Version = "2026-07"`, in one place |
| Neutral contract | The neutral types of 008 PR 1 are used as they are: `StoreException` with `StoreFailure` (this plan checks that `RateLimited` is among the outcomes and adds it there if 008 PR 1 left it out), `SourceInfo`, `VectorInfo`, `StoreHit`, `FilterCondition`. The Pinecone adapter returns: `SourceInfo` with the index's dimension and metric as the one vector, the namespace's record count from `describe_index_stats`, and the indexed keys (all for serverless; `metadata_config.indexed` for pods) |
| Dates in the contract | The one place where Pinecone needs the shared code to learn something: a date stored as a number. 008 PR 1 left no such notion, so this plan adds to `IStoreAdapter` a `DateEncoding` (`IsoString` for Qdrant and PgVector, `EpochNumber` for Pinecone) and to the mapped field an optional `EpochUnit`. Three consumers read it: `MappingValidation.Values` (a date fits a number inside the plausible range of the field's unit instead of an ISO string), `FilterTranslator` (the `DateRange` condition carries the unit so the Pinecone adapter converts the instants to numbers) and the draft (`RefineDraft` hook of the adapter: a number key whose name looks like a date and whose values fit the range of seconds or of milliseconds is proposed as `Date` with that unit). Strings are never proposed as dates here |
| Stored settings | `ConnectionSettings` (008 PR 1) holds `IndexName`, `Namespace`, `ControlPlaneUrl?`, `IndexHost?` and `ProtectedApiKey`. The key's carry-over rule (D-3) is the adapter's, as Qdrant's is. `EpochUnit` is a new member of the mapped field inside the `Mapping` JSON: no schema change, but the EF model snapshot changes, so `dotnet ef migrations add` runs once and the migration must come out empty; **it can conflict with the mapping migration of plan 007 PR 1**, so whichever lands second deletes and re-adds its migration (never edits a merged one) |
| Index support | An index is supported when Pinecone describes it as a dense vector index (`vector_type` dense) and as not holding a document schema. How a document index shows in the listing and in `describe` is fixed by the PR 3 prototype; an index the listing cannot tell is listed as supported and fails verification with a clear message if it is not |
| Sampling | One `query` with a seeded pseudo-random unit vector of the index's dimension (a fixed seed, so a draft is the same every time), `topK` 100, `includeMetadata` true, `includeValues` false, in the namespace. A response refused as too large is retried once with 20. Metadata becomes a `JsonObject` (lists as arrays); records without metadata are skipped; keys with a dot are reported as not mappable because the shared readers split paths on dots |
| Search | One `query` per KB. The conditions become one `$and`: `$in` for texts and lists, `$gte`/`$lte` for numbers and for dates (numbers in the field's unit), `$eq` for booleans. The `DocumentIds` condition (008 A1-2 and 009 A1-2) becomes an `$or` of an `$in` with the ids as strings and an `$in` with the numeric ones, the second only for ids that parse as numbers. The threshold is applied by Quorum after the response (no parameter exists), by the direction of the index metric; Pinecone returns the whole metadata, and only the mapped keys are read |
| Tests, three tiers | **Unit tests** with a stub `HttpMessageHandler`: request shapes, headers, version, no redirect followed, every failure translated, the 4 MB retry. **Integration tests** against a Pinecone-compatible server started by a fixture; which one is decided by the spike of PR 1 (below). **A live suite** that runs by hand against a real Pinecone project (`PINECONE_API_KEY` and an index name in the environment; skipped when absent, marked with a trait, never in CI) and checks that the server of the integration tests and the service agree on scores, filters and errors (AC-33) |
| Test server | The spike (PR 1) evaluates `ghcr.io/pinecone-io/pinecone-index` (pinned by digest, the data plane of one index) with a small in-process stub of the two control-plane calls. It is accepted when it supports: `query` with `$and`, `$or`, `$in`, `$eq`, `$gte`/`$lte` filters, `includeMetadata`, namespaces, the three metrics, `describe_index_stats` with namespaces and counts, and when it does not reject the `X-Pinecone-Api-Version: 2026-07` header. Otherwise `tools/Quorum.FakePinecone`, a small ASP.NET server with exact in-memory search, replaces it. **The session asks before pulling the image**, as the plan needs the user's go for a download |
| Sample data | `tools/Quorum.SampleKnowledgeBase` gains a `PineconeSeeder` that creates the sample indexes of D-15 through the test server, with the deterministic embedding of spec 003. The seeder needs `upsert`, so it carries a tiny writer of its own: the product client never writes |
| Docs | `docs/knowledge-bases.md` (guide for data owners) gets the Pinecone section in the closing PR. `docs/database-schema.md` is **not tracked in `main`** (it is an untracked file in the author's checkout), as plan 007 already notes: whoever commits it first adds the Pinecone connection and `epochUnit` there |

## Order and parallel work
- **PR 1 (test server and sample data) starts now,** in parallel with 008 PR 1: it touches only `tools/` and `tests/Fixtures`.
- **PR 2 (REST client)** has no dependency on the contract either; its unit tests use a stub handler. It can start now and merge before 008 PR 1.
- **PR 3 to PR 5 are one chain [waits 008]** (same files, no parallel writers), after 008 PR 1 is merged; they also follow plan 008's PR 3 only in the sense that both add a kind to the same registrar, so whichever lands second rebases.
- The UI (PR 6) starts when PR 5 is merged; documentation (PR 7) is last.

## Steps

### PR 1 - Test server and sample data (`feat/009-pinecone-test-server`)
1. **Spike.** With the user's go for the download, pull `ghcr.io/pinecone-io/pinecone-index` and run it against the acceptance list in the Decisions table; record the findings under this step: how it is started (ports and options), which calls and filters it supports, the scores it gives per metric, how it answers an oversize or invalid request, and whether it takes the pinned API version. Decide: the image with the control-plane stub, or `tools/Quorum.FakePinecone`.
2. `PineconeFixture` (one container or one in-process server for the whole test run, started on first use like `QdrantFixture`; the image digest is pinned in one place) and the control-plane stub (`GET /indexes`, `GET /indexes/{name}` with dimension, metric, host and status), or the fake server with the four calls of the client and exact search.
3. The sample indexes of D-15 in the sample project, with a `PineconeSeeder`: flat metadata, a list of tags, a date as a Unix timestamp in seconds and another in milliseconds, a numeric key and a string that look alike, a document id as a string in some records and as a number in others, a different dimension, an empty namespace, several namespaces, a pasteable schema. AC-32 (generator part).
4. The live suite skeleton: a trait, the environment variables, and the first checks (scores per metric, a filter of each type, an invalid filter, a wrong key); it reports "skipped" when no key is set. AC-33.

### PR 2 - The REST client (`feat/009-pinecone-client`)
5. `PineconeClient` and its records for the four calls, `PineconeApi.Version`, the named client in `KnowledgeBasesModule`, the 10 s and search limits, the failure translation (401 and 403 unauthorized, 404 not found, 400 rejected, 429 rate limited, timeouts and connection errors unreachable, anything else unexpected; the message never carries the key, the URLs or Pinecone's text). Unit tests with the stub handler. AC-5, AC-26.
6. Prototype of the facts the spec marks "to confirm" against the test server and, where a key is available, the real service: the direction of the Euclidean score, how a document index appears in the listing and in `describe`, the status and body of an oversize response, the shape of `describe_index_stats` for the default namespace. Findings are written under this step and fix the details of steps 8, 11 and 14.

### PR 3 - Connect, discover and inspect **[waits 008]** (`feat/009-pinecone-inspect`)
7. `KnowledgeBaseKind.Pinecone`, the connection model (key, index, namespace, the two advanced fields), `PineconeStoreAdapter` with the key handling of D-2 and D-3, the `DateEncoding` and `EpochUnit` additions to the contract (Decisions). AC-1, AC-2.
8. `POST /knowledge-bases/indexes`: indexes with type, dimension, metric, readiness and support (with the reason when not supported), and with `indexName` the namespaces with their record counts, the default one shown as "(default)". `knowledge_base.unsupported_operation` for other kinds. AC-6, AC-7.
9. Discovery: the seeded sampling query, flat metadata to `JsonObject`, types from the values, dotted keys and records without metadata reported or skipped, the `RefineDraft` hook that proposes dates with their unit, the pasted flat schema. `POST /knowledge-bases/inspect` for `Pinecone` with the index's dimension and metric as the vector. AC-8, AC-9, AC-10, AC-11, AC-13, AC-14.

### PR 4 - Registry and verification **[waits 008]** (`feat/009-pinecone-registry`)
10. Create, update, detail, list and delete for `Pinecone`: the key never returned (`hasApiKey`), `knowledge_base.api_key_required` and its rule, the mapping's `epochUnit` (stored and returned), `knowledge_base.epoch_unit_required` and `knowledge_base.invalid_mapping` for a date on a key that is not numeric or has no unit, the shared-KB reduced DTO. The migration that must come out empty. AC-3, AC-4, AC-12, AC-28.
11. Verification (D-11): control plane and key, index ready and dense, dimension against an embedding, namespace with records, mapped keys in a sample, then the warnings that never change the status: an integrated-embedding index, a pod index whose indexed keys leave out a mapped key (the key of the `documentId` role included, per A1-3), recently written records. A failed check saves the KB `Unavailable` with the cause and its consequence; an `Unavailable` KB that recovers becomes `Available`. AC-15, AC-16, AC-17, AC-18, AC-39.
12. Authorization and logs: `knowledge_base.manage` on the new route, nothing sensitive in logs or audit (no key, no URL entered by the admin). The capability absence: a consumer that asks a Pinecone KB for the lookups capability gets "not supported" and no call is made. AC-27, AC-29 (registration half), AC-35, AC-38 (by the capability, as in plan 008).

### PR 5 - Search **[waits 008]** (`feat/009-pinecone-search`)
13. The condition translation: text, number, date (in the field's unit), boolean and list; the `DocumentIds` `$or` of two `$in`; a record whose key has another type simply does not match. AC-20, AC-21, AC-36, AC-37.
14. `PineconeStoreAdapter.Search`: the query, the whole metadata returned and only the mapped keys read, the threshold by metric direction, fewer than K chunks allowed, unusable chunks counted. AC-19, AC-22, AC-25.
15. Failures and status: every outcome (rate limit and oversize response included) reaches the model as a safe error result, the KB goes `Unavailable` and comes back, partial failure among several KBs; a rag tool over a Qdrant, a PgVector and a Pinecone KB interleaves with sources naming their KB. AC-23, AC-24, AC-29 (search half).

### PR 6 - Admin UI (`feat/009-ui-pinecone`)
16. Web client models for the per-kind `connection` and the mapping's `epochUnit`; the kind-aware wizard shows the Pinecone fields, the lists of indexes and namespaces, the mapping review with the unit of each date, verification and the test search. Logic in plain view classes covered by bUnit. The Qdrant and PgVector flows are untouched. AC-30, AC-31.

### PR 7 - Documentation and verification (`docs/009-closing`)
17. The Pinecone section of the guide for data owners (what Quorum reads, dates as Unix timestamps and the unit, a read-only key, namespaces, document indexes not supported, ids as strings or numbers), `docs/database-schema.md` if tracked by then, README mention. AC-34.
18. Final pass: Release build, full suite, `get_diagnostics`, the live suite against a real project, and a manual run through the LiteLLM proxy with a sample index (project memory).

## Acceptance criteria by PR
| PR | Criteria |
|---|---|
| 1 | AC-32 (generator), AC-33 |
| 2 | AC-5, AC-26 |
| 3 | AC-1, AC-2, AC-6, AC-7, AC-8, AC-9, AC-10, AC-11, AC-13, AC-14 |
| 4 | AC-3, AC-4, AC-12, AC-15, AC-16, AC-17, AC-18, AC-27, AC-28, AC-29 (registration), AC-35, AC-38, AC-39 |
| 5 | AC-19, AC-20, AC-21, AC-22, AC-23, AC-24, AC-25, AC-29 (search), AC-36, AC-37 |
| 6 | AC-30, AC-31 |
| 7 | AC-32 (documentation and manual run), AC-34 |

Every criterion from AC-1 to AC-39 has one main owner.

## Risks
- **Facts about Pinecone that the docs leave open** (the Euclidean score direction, the shape of a document index in the listing, the oversize response). Mitigation: the PR 2 prototype settles them before the adapter; the live suite keeps them true; the verification message falls back to "unexpected" instead of a wrong claim.
- **The test server drifts from the service,** or the `pinecone-index` image is frozen on an older API. Mitigation: the acceptance list of the spike, a digest pin, the live suite, and `tools/Quorum.FakePinecone` as the stated fallback.
- **The adapter contract grows in this plan** (`DateEncoding`, `RefineDraft`) after 008 PR 1 froze it. Mitigation: the additions are optional with a default that keeps Qdrant and PgVector unchanged, and they ship with PR 3 where their tests are.
- **Snapshot conflict between `EpochUnit` and the mapping members of plan 007.** Mitigation: both migrations must be empty; the second to land deletes and re-adds its own.
- **Retirement of API version `2026-07`.** Mitigation: one constant, the contract shapes are tested, and the deferred row says when to revisit.
- **A real Pinecone project is needed for the live suite and the final run.** Mitigation: both are skipped when no key is set, and PR 7 says plainly if they were not run.
- **Rate limits and cost on the Starter plan** while testing live. Mitigation: the live suite is small and manual.

## Details beyond the spec to confirm
1. **`DateEncoding`, the `EpochUnit` on the mapped field and the `RefineDraft` hook are added to the contract in PR 3,** not in 008 PR 1, so that 008 stays free of Pinecone concepts. The alternative is to add them to 008 PR 1 now.
2. **The decision on the test server is taken by the spike,** with the user's go before the image is pulled.
3. **The seed of the sampling vector** is a constant in the adapter; any fixed value does, only the draft's repeatability matters.
4. **The live suite lives in the existing integration test project** with a trait and a skip, not in a separate project.
5. **Timeouts** reuse the 10 s and 15 s of the other kinds.
