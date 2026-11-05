# Changelog

## [0.17.0](https://github.com/claudiom248/quorum/compare/v0.16.0...v0.17.0) (2026-11-05)


### Features

* chat turn spans with usage and outcome (spec 016) ([#187](https://github.com/claudiom248/quorum/issues/187)) ([f09866a](https://github.com/claudiom248/quorum/commit/f09866a79d4030dc0da9e37d3f239cd76ec888c2))
* keep the parameterized command text of the database spans (spec 016 D-8 amended) ([#195](https://github.com/claudiom248/quorum/issues/195)) ([37f2ee7](https://github.com/claudiom248/quorum/commit/37f2ee7c9d271d5e72ef2f30fd6cb1e24fa3dcb6))
* no bundled observability backend, export OTLP to any backend (spec 016) ([#194](https://github.com/claudiom248/quorum/issues/194)) ([b24ace1](https://github.com/claudiom248/quorum/commit/b24ace1344edba7f0c86a8aaef86990c7804b5b4))
* observability compose profile and operator guide (spec 016) ([#192](https://github.com/claudiom248/quorum/issues/192)) ([2d69a1a](https://github.com/claudiom248/quorum/commit/2d69a1a2df9523a4f07fd090880c92b693645e59))
* publish the bundled PostgreSQL on loopback ([#198](https://github.com/claudiom248/quorum/issues/198)) ([c250747](https://github.com/claudiom248/quorum/commit/c250747c0dffd72b4f91dd4905e04c7c57a6a631))
* skill export as SKILL.md and zip (spec 017) ([#202](https://github.com/claudiom248/quorum/issues/202)) ([6ef4851](https://github.com/claudiom248/quorum/commit/6ef48519be51cb9e2186c9373c5e44b1ab931280))
* skill runtime, load_skill and audit (spec 017) ([#200](https://github.com/claudiom248/quorum/issues/200)) ([8fdb239](https://github.com/claudiom248/quorum/commit/8fdb239f53e1cb0c04923e695f580930749467d5))
* skills admin page, import dialog and agent selector (spec 017) ([#203](https://github.com/claudiom248/quorum/issues/203)) ([6c843ff](https://github.com/claudiom248/quorum/commit/6c843ff90888cb4cac3fafcc5351fd0e6be2e0f9))
* skills, assignment and settings (spec 017) ([#199](https://github.com/claudiom248/quorum/issues/199)) ([6333e7b](https://github.com/claudiom248/quorum/commit/6333e7ba6f371aeef01828ad3ef7fad5452ff871))
* telemetry foundation with OTLP export and HTTP/Npgsql instrumentation (spec 016) ([#185](https://github.com/claudiom248/quorum/issues/185)) ([6052c0a](https://github.com/claudiom248/quorum/commit/6052c0a11ceea839910fa5d2fdcaa15ffaa016ee))
* tool spans, pipeline spans and trace link in the audit log (spec 016) ([#191](https://github.com/claudiom248/quorum/issues/191)) ([5d90364](https://github.com/claudiom248/quorum/commit/5d903647a5b7b9be789b0d2ad22cf2a1e5add63e))


### Bug Fixes

* telemetry service name, privacy and background traces (spec 016) ([#193](https://github.com/claudiom248/quorum/issues/193)) ([7f678f3](https://github.com/claudiom248/quorum/commit/7f678f3c2e229f32bd8daa07aec53064ea66f117))
* tool spans name the kind of tool, not the audit server (spec 016) ([#197](https://github.com/claudiom248/quorum/issues/197)) ([eae58c2](https://github.com/claudiom248/quorum/commit/eae58c2786aac5f5550a7b615dfe91d72b27f4f6))

## [0.16.0](https://github.com/claudiom248/quorum/compare/v0.15.0...v0.16.0) (2026-11-04)


### Features

* in-chat preview panel for rendered content (spec 014) ([#173](https://github.com/claudiom248/quorum/issues/173)) ([66418ae](https://github.com/claudiom248/quorum/commit/66418aeec7761984a1be4f995d7aa33aea25f7d9))
* preview viewer as srcdoc, restyled, JSON colours, CDN allowlist (spec 014) ([#178](https://github.com/claudiom248/quorum/issues/178)) ([fb59eae](https://github.com/claudiom248/quorum/commit/fb59eaeb998229d28e6186006811be0143a18ee9))
* vendored mermaid for preview diagrams (spec 014) ([#175](https://github.com/claudiom248/quorum/issues/175)) ([9c61664](https://github.com/claudiom248/quorum/commit/9c616645d717720cb9947f53bbe3e55ceb80974e))


### Bug Fixes

* Blazor WebAssembly 10.0.14 for advisory GHSA-g27p-qf6j-c96c ([#179](https://github.com/claudiom248/quorum/issues/179)) ([a71002f](https://github.com/claudiom248/quorum/commit/a71002f92cd260d876141b3836be4db70247c8a5))
* lean query schema says search and sort are for lists and follows describe (spec 012) ([#180](https://github.com/claudiom248/quorum/issues/180)) ([10e5435](https://github.com/claudiom248/quorum/commit/10e5435ab0c714f8052bd4946da6d99973bcc98b))

## [0.15.0](https://github.com/claudiom248/quorum/compare/v0.14.0...v0.15.0) (2026-11-03)


### ⚠ BREAKING CHANGES

* admin UI polish, ledger styles, tools called by their own name (spec 013, spec 015) ([#171](https://github.com/claudiom248/quorum/issues/171))

### Features

* admin UI for agents, assistants and model endpoints, nested delegation ledger (spec 013) ([#165](https://github.com/claudiom248/quorum/issues/165)) ([43061b9](https://github.com/claudiom248/quorum/commit/43061b975bc223b6343098584b744e071c611ed2))
* admin UI polish, ledger styles, tools called by their own name (spec 013, spec 015) ([#171](https://github.com/claudiom248/quorum/issues/171)) ([934519d](https://github.com/claudiom248/quorum/commit/934519dc3f2137f6f20b9f2032e8e8ae559b727f))
* agents API with sub-agent edges and tools on agents (spec 013) ([#156](https://github.com/claudiom248/quorum/issues/156)) ([20b73d2](https://github.com/claudiom248/quorum/commit/20b73d27c9002e72298369d5b8f11f53da136b65))
* agents foundation, assistant behaviour preserved (spec 013) ([#155](https://github.com/claudiom248/quorum/issues/155)) ([ab6321d](https://github.com/claudiom248/quorum/commit/ab6321d2882183576e26d2c4a63ef27e6871c991))
* assistants CRUD (spec 013) ([#161](https://github.com/claudiom248/quorum/issues/161)) ([1dbdad9](https://github.com/claudiom248/quorum/commit/1dbdad9d83a43302de5dfca3235d1bcb6bad55e7))
* contracts sample with real relations and one article per chunk ([#158](https://github.com/claudiom248/quorum/issues/158)) ([4a60379](https://github.com/claudiom248/quorum/commit/4a60379a5a132b8ed131bc963f40736284b43129))
* delegation runtime, agents call their sub-agents as tools (spec 013) ([#163](https://github.com/claudiom248/quorum/issues/163)) ([35c42cc](https://github.com/claudiom248/quorum/commit/35c42cc17c31b33b2961147cee448e2138d2664d))
* describe per operation and filters that match each target (spec 012 A1) ([#141](https://github.com/claudiom248/quorum/issues/141)) ([c39e1e7](https://github.com/claudiom248/quorum/commit/c39e1e7fca42963e3bd1882f11602d64dd42be83))
* entity-only knowledge bases without a text role (spec 003 Amendment 3) ([#140](https://github.com/claudiom248/quorum/issues/140)) ([70f2bc9](https://github.com/claudiom248/quorum/commit/70f2bc9863ebcc8b12dc98ce93038dc419511d66))
* group the tool calls of an answer in one ledger ([#146](https://github.com/claudiom248/quorum/issues/146)) ([d406c27](https://github.com/claudiom248/quorum/commit/d406c274194695c48ee30ea128f62e701a8ef08e))
* lean describe and query take the attribute filters of entity and manual lists (spec 012 A1-5, A1-7) ([#151](https://github.com/claudiom248/quorum/issues/151)) ([2863b34](https://github.com/claudiom248/quorum/commit/2863b341385252bd0f3a8a6e26ed0db1ed4193aa))
* lean describe states filter types, operators and sort values (spec 012 D-5, D-8) ([#144](https://github.com/claudiom248/quorum/issues/144)) ([e266f0b](https://github.com/claudiom248/quorum/commit/e266f0b669eba406445e7376fe59042b11d9192e))
* lookup consolidation and lean manifest (spec 012) ([#135](https://github.com/claudiom248/quorum/issues/135)) ([0b737a6](https://github.com/claudiom248/quorum/commit/0b737a615dbaa0d32b753741e0bb92dbf7cc821b))
* model endpoints as a resource (spec 013) ([#154](https://github.com/claudiom248/quorum/issues/154)) ([5d43b97](https://github.com/claudiom248/quorum/commit/5d43b979197c9db61d39695bcb7ece425c194870))
* one knowledge base per documents or count call (spec 012 A1-2) ([#143](https://github.com/claudiom248/quorum/issues/143)) ([f12e338](https://github.com/claudiom248/quorum/commit/f12e338e749da8335e0e17294035993d13feaa80))
* one selector of targets in the lookup tool editor (spec 012 A1-10) ([#142](https://github.com/claudiom248/quorum/issues/142)) ([67c7d0d](https://github.com/claudiom248/quorum/commit/67c7d0d7e945f75e31e479e97ad765c169162576))
* read-only sample reference-data MCP server ([#164](https://github.com/claudiom248/quorum/issues/164)) ([10c2576](https://github.com/claudiom248/quorum/commit/10c25769b703ebf24d74aaeba1708fa3dcd01e1f))
* rules of a knowledge base and a target name in a lookup tool (spec 012) ([#139](https://github.com/claudiom248/quorum/issues/139)) ([c76c157](https://github.com/claudiom248/quorum/commit/c76c157202e9fa7bb58146d3523923f485f88384))
* web fetch tool reading pages for the agent (spec 006) ([#132](https://github.com/claudiom248/quorum/issues/132)) ([c1d9863](https://github.com/claudiom248/quorum/commit/c1d986350a5ed770b49466e389caaf8c587e9b8a))


### Bug Fixes

* answer 409 or 400 when a foreign key races agent delete or save (spec 013) ([#157](https://github.com/claudiom248/quorum/issues/157)) ([2077c1b](https://github.com/claudiom248/quorum/commit/2077c1b017b10a86b489d202e3877fe95bd89a19))
* default the revalue amount to 0, not null ([#170](https://github.com/claudiom248/quorum/issues/170)) ([ece1904](https://github.com/claudiom248/quorum/commit/ece1904bec9f933f6d5754a897fdbc07d072d31b))
* framework docs cite themselves as the family framework ([#167](https://github.com/claudiom248/quorum/issues/167)) ([5b0bdeb](https://github.com/claudiom248/quorum/commit/5b0bdebf70caa5ccbbff138075a0bd3dea7a148b))
* knowledge base filters take min and max on a date (spec 012) ([#166](https://github.com/claudiom248/quorum/issues/166)) ([9814a6f](https://github.com/claudiom248/quorum/commit/9814a6f2645cbe17a371186da56e8aa57e17d633))

## [0.14.0](https://github.com/claudiom248/quorum/compare/v0.13.0...v0.14.0) (2026-10-29)


### Features

* web search tool on the platform's SearXNG instance (spec 005) ([#129](https://github.com/claudiom248/quorum/issues/129)) ([2d9a51f](https://github.com/claudiom248/quorum/commit/2d9a51f3f900a82184b9be36bdaabf1fb603d1c4))

## [0.13.0](https://github.com/claudiom248/quorum/compare/v0.12.0...v0.13.0) (2026-10-28)


### Features

* dual auth on refresh and status endpoint for knowledge base lookups (spec 011) ([#122](https://github.com/claudiom248/quorum/issues/122)) ([cbe2f69](https://github.com/claudiom248/quorum/commit/cbe2f6928b4e3143fa8ca042ec06625e4b7d7c83))
* Integrations tab in the settings for service tokens (spec 011) ([#127](https://github.com/claudiom248/quorum/issues/127)) ([742c2a4](https://github.com/claudiom248/quorum/commit/742c2a480c95b3e1dd69eeb400101c5f0b7e0e6a))
* service token authentication middleware in the host (spec 011) ([#118](https://github.com/claudiom248/quorum/issues/118)) ([db9eaff](https://github.com/claudiom248/quorum/commit/db9eaff24b12b67850f8b1e2534181fd1a555a13))
* service token generation, listing and revocation in Tenancy (spec 011) ([#117](https://github.com/claudiom248/quorum/issues/117)) ([016de75](https://github.com/claudiom248/quorum/commit/016de75ff5567f39da81522c43974710245b59ed))


### Bug Fixes

* a collection of entities counts documents, not chunks (spec 007) ([#123](https://github.com/claudiom248/quorum/issues/123)) ([54b7729](https://github.com/claudiom248/quorum/commit/54b77298d1b0fe83e3b01fe5c5260c1cd1eb3249))
* tell the model how to total a field with the lookup tools (spec 007) ([#124](https://github.com/claudiom248/quorum/issues/124)) ([6a72715](https://github.com/claudiom248/quorum/commit/6a7271554cf7e68759ab958102bcaeb68c043f3d))

## [0.12.0](https://github.com/claudiom248/quorum/compare/v0.11.0...v0.12.0) (2026-10-27)


### Features

* lookups for pgvector in the registry, the wizard and the samples (spec 008 amendment 2) ([#113](https://github.com/claudiom248/quorum/issues/113)) ([ae6e684](https://github.com/claudiom248/quorum/commit/ae6e684d8364fd9aac21ae5c53f640012de11545))
* lookups for pgvector knowledge bases (spec 008 amendment 2) ([#107](https://github.com/claudiom248/quorum/issues/107)) ([048c7b2](https://github.com/claudiom248/quorum/commit/048c7b2d4fa83b59e4bb14cd37061f653e7ca5d4))
* register pgvector knowledge bases from the admin pages (spec 008) ([#105](https://github.com/claudiom248/quorum/issues/105)) ([ee9bbe9](https://github.com/claudiom248/quorum/commit/ee9bbe992a6f9194ca834eebab889e447c12070f))
* search pgvector knowledge bases (spec 008) ([#103](https://github.com/claudiom248/quorum/issues/103)) ([8043374](https://github.com/claudiom248/quorum/commit/8043374367ce1a29aed529a0782d4c5db3344b30))
* serve the documents of the samples and grow them to 620 contracts and 420 employees ([#114](https://github.com/claudiom248/quorum/issues/114)) ([30c4b6e](https://github.com/claudiom248/quorum/commit/30c4b6ebde82cc65012b79a9cdb4c666b9ce4a71))
* the name of an entity can be its title and its document id (spec 007 A2) ([#111](https://github.com/claudiom248/quorum/issues/111)) ([172970d](https://github.com/claudiom248/quorum/commit/172970d6d888fae84d2e4976f35dee905d91c5a1))
* verify pgvector knowledge bases and keep their secrets (spec 008) ([#102](https://github.com/claudiom248/quorum/issues/102)) ([e7f6198](https://github.com/claudiom248/quorum/commit/e7f61986263aa787b62fb0077fd6678ffad23105))


### Bug Fixes

* code review findings of the pgvector store (spec 008) ([#112](https://github.com/claudiom248/quorum/issues/112)) ([e748b74](https://github.com/claudiom248/quorum/commit/e748b74f4fee5771a6b5879bbf7a6249f7aeac96))
* keep chat state per conversation and let each one stream its own answer ([#108](https://github.com/claudiom248/quorum/issues/108)) ([34f3d16](https://github.com/claudiom248/quorum/commit/34f3d167b576e663cf2725224be11ffbccfdc685))

## [0.11.0](https://github.com/claudiom248/quorum/compare/v0.10.0...v0.11.0) (2026-10-26)


### Features

* admin UI pages, two-step setup and getting-started checklist (spec 010) ([#99](https://github.com/claudiom248/quorum/issues/99)) ([62ece40](https://github.com/claudiom248/quorum/commit/62ece4012f85ee96bb3d8607db0dee97908fb172))
* connect, list relations and inspect pgvector knowledge bases (spec 008) ([#101](https://github.com/claudiom248/quorum/issues/101)) ([d27bcb8](https://github.com/claudiom248/quorum/commit/d27bcb803ee67643aca64d0679d9809c8978321c))
* sidebar shell and shared list components (spec 010) ([#94](https://github.com/claudiom248/quorum/issues/94)) ([1a361c5](https://github.com/claudiom248/quorum/commit/1a361c548f1a032eee5c9f72a207a0845138e41c))
* tenants list and onboarding checklist API (spec 010) ([#96](https://github.com/claudiom248/quorum/issues/96)) ([9fc98a2](https://github.com/claudiom248/quorum/commit/9fc98a2ddd53dd6b80bf54441af1ce277d2fc602))
* uniform scope, list query and aggregate lookups (spec 010) ([#97](https://github.com/claudiom248/quorum/issues/97)) ([0feaeb4](https://github.com/claudiom248/quorum/commit/0feaeb48478069be8e00efe03340e9a6e769c84e))


### Bug Fixes

* code review findings for lookups and admin UI (specs 007, 010) ([#100](https://github.com/claudiom248/quorum/issues/100)) ([db410da](https://github.com/claudiom248/quorum/commit/db410da915f354480475410beba7ce3e7189a782))

## [0.10.0](https://github.com/claudiom248/quorum/compare/v0.9.0...v0.10.0) (2026-10-23)


### Features

* admin UI for lookup tools and the document_ids switch (spec 007) ([#86](https://github.com/claudiom248/quorum/issues/86)) ([db9de58](https://github.com/claudiom248/quorum/commit/db9de58f21a5c1fcca83e2cf98e101b83d899d3a))
* admin UI for lookups (spec 007) ([#85](https://github.com/claudiom248/quorum/issues/85)) ([834ed4e](https://github.com/claudiom248/quorum/commit/834ed4e985425001b03c7f1ef0394c38a92ea8c9))
* document_ids on rag tools (spec 007) ([#84](https://github.com/claudiom248/quorum/issues/84)) ([576b1f6](https://github.com/claudiom248/quorum/commit/576b1f6cebc1814bf6e4da4c953af7560b4d1a40))
* entity lookups over a collection of entities (spec 007) ([#81](https://github.com/claudiom248/quorum/issues/81)) ([94abb53](https://github.com/claudiom248/quorum/commit/94abb5390bd06607a3906df5c30fc3d5ec4c0443))
* filter lists of entities live and say which lists can be filtered (spec 007 A1) ([#89](https://github.com/claudiom248/quorum/issues/89)) ([9f22bbc](https://github.com/claudiom248/quorum/commit/9f22bbc18dd8004c4ba826b03b9bb76c57af83e8))
* live lookup operations (spec 007) ([#83](https://github.com/claudiom248/quorum/issues/83)) ([93b4342](https://github.com/claudiom248/quorum/commit/93b4342a03d670c75550870dd1abec0b6828fdf7))
* lookup generations, cache and background refresh (spec 007) ([#78](https://github.com/claudiom248/quorum/issues/78)) ([610ef51](https://github.com/claudiom248/quorum/commit/610ef51ca05bbcaa2b69257da260ce4064083f25))
* lookup storage and mapping members (spec 007) ([#73](https://github.com/claudiom248/quorum/issues/73)) ([3d3881e](https://github.com/claudiom248/quorum/commit/3d3881ee7d4af64d639f8839884e494b042ba4b3))
* lookup tool over stored lookups (spec 007) ([#79](https://github.com/claudiom248/quorum/issues/79)) ([f41deb6](https://github.com/claudiom248/quorum/commit/f41deb68e876b1e2ca0a151ee02af9ac7d543ecb))
* manual lookups and the lookup values reader (spec 007) ([#75](https://github.com/claudiom248/quorum/issues/75)) ([4049bc3](https://github.com/claudiom248/quorum/commit/4049bc35a3ab842e6ae8f7e3cd1afbd52344a962))
* Qdrant lookups computed through the store contract (spec 007) ([#80](https://github.com/claudiom248/quorum/issues/80)) ([f278218](https://github.com/claudiom248/quorum/commit/f27821876da5026ccdec3074c631f008e20bfbd7))


### Bug Fixes

* guide the model through the lookup tool definition and results (spec 007) ([#87](https://github.com/claudiom248/quorum/issues/87)) ([cb37b7d](https://github.com/claudiom248/quorum/commit/cb37b7d11395b28fee8f51ca0645b8b8c9d970f5))

## [0.9.0](https://github.com/claudiom248/quorum/compare/v0.8.0...v0.9.0) (2026-10-22)


### Features

* admin pages for knowledge bases and rag tools (spec 003) ([#51](https://github.com/claudiom248/quorum/issues/51)) ([3388912](https://github.com/claudiom248/quorum/commit/33889122a09067fbbe3b3255ec65998622108f63))
* citations (spec 003) ([#50](https://github.com/claudiom248/quorum/issues/50)) ([78a7ad0](https://github.com/claudiom248/quorum/commit/78a7ad002a8039992284187c4044468a4504a371))
* dictionary clusters and rag tool aliases (spec 003) ([#54](https://github.com/claudiom248/quorum/issues/54)) ([d5eed9f](https://github.com/claudiom248/quorum/commit/d5eed9f96f3460b1646c8a23f4ae01c40cf38312))
* knowledge base module and metadata dictionary (spec 003) ([#43](https://github.com/claudiom248/quorum/issues/43)) ([b41724b](https://github.com/claudiom248/quorum/commit/b41724bdf54c2be1ccbb965e5f302d4da884c3fb))
* knowledge base registry (spec 003) ([#46](https://github.com/claudiom248/quorum/issues/46)) ([aa89034](https://github.com/claudiom248/quorum/commit/aa890345bb26f8fd735cee870c00deda3737456c))
* knowledge base search (spec 003) ([#47](https://github.com/claudiom248/quorum/issues/47)) ([1994941](https://github.com/claudiom248/quorum/commit/19949415e34ebda1c427f34ef9ce68d4c4bb3654))
* one number per source, grouped by document, with the file named (spec 003) ([#59](https://github.com/claudiom248/quorum/issues/59)) ([2105e0b](https://github.com/claudiom248/quorum/commit/2105e0bee08278194ffcd99625fd3146d43477f8))
* Qdrant connector and inspect endpoint (spec 003) ([#45](https://github.com/claudiom248/quorum/issues/45)) ([6484cc1](https://github.com/claudiom248/quorum/commit/6484cc1f10c3799b50e397c08f89ce2f3e4d87ad))
* sources in the chat (spec 003) ([#57](https://github.com/claudiom248/quorum/issues/57)) ([6c94970](https://github.com/claudiom248/quorum/commit/6c94970b12b40a927ffbe02034422c12b44e792a))
* the rag tool (spec 003) ([#48](https://github.com/claudiom248/quorum/issues/48)) ([92e19da](https://github.com/claudiom248/quorum/commit/92e19da77c9354af002e82e228b7d1355db683ca))


### Bug Fixes

* apply the code review findings of the admin pages and clusters (spec 003) ([#56](https://github.com/claudiom248/quorum/issues/56)) ([64c48da](https://github.com/claudiom248/quorum/commit/64c48da204c5d468c648260738146c417f5aeee9))
* links always sit on the sections of a source, no file name beside a link (spec 003) ([#61](https://github.com/claudiom248/quorum/issues/61)) ([7ba6ebc](https://github.com/claudiom248/quorum/commit/7ba6ebcd4cf82b728c9775288a69bda3d1330178))
* make the model call ask_user instead of asking in plain text (spec 004) ([#60](https://github.com/claudiom248/quorum/issues/60)) ([d34bc0a](https://github.com/claudiom248/quorum/commit/d34bc0a661a85e43494faf3a2be84cad371300a7))
* stop blocking outbound addresses of registered servers (spec 003 A2, spec 004 D-18) ([#63](https://github.com/claudiom248/quorum/issues/63)) ([f00766b](https://github.com/claudiom248/quorum/commit/f00766b69fb7aba4084e28ffc5932bd5efc8ad06))
* tell fields of the same name apart in the rag tool editor and its definition (spec 003) ([#55](https://github.com/claudiom248/quorum/issues/55)) ([15f7f16](https://github.com/claudiom248/quorum/commit/15f7f16e064e03feed0a74d8bc01b8454b9b627f))
* use Regex.Count in the rag search tests (spec 003) ([#49](https://github.com/claudiom248/quorum/issues/49)) ([3db6510](https://github.com/claudiom248/quorum/commit/3db65108f7256b8f9d649f69578df3ec3f5e9e67))

## [0.8.0](https://github.com/claudiom248/quorum/compare/v0.7.0...v0.8.0) (2026-10-17)


### Features

* admin page for MCP servers and tools (spec 004) ([#40](https://github.com/claudiom248/quorum/issues/40)) ([8d91590](https://github.com/claudiom248/quorum/commit/8d91590bbbecf35d4b1224cc9327f45f245dd720))
* audit log API and retention purge (spec 004) ([#37](https://github.com/claudiom248/quorum/issues/37)) ([cd18270](https://github.com/claudiom248/quorum/commit/cd1827032e94fce51bf7c6e8bb212923857a80a5))
* confirmation of tool calls and ask_user (spec 004) ([#34](https://github.com/claudiom248/quorum/issues/34)) ([045ffc8](https://github.com/claudiom248/quorum/commit/045ffc818192f21249cebf7a291883d1b2d86180))
* MCP servers registry with discovery, review and scopes (spec 004) ([#38](https://github.com/claudiom248/quorum/issues/38)) ([2750d49](https://github.com/claudiom248/quorum/commit/2750d49309785835499a538fdeadc15262e6ca81))
* run tools of MCP servers in answers (spec 004) ([#39](https://github.com/claudiom248/quorum/issues/39)) ([b56cd7e](https://github.com/claudiom248/quorum/commit/b56cd7eee294cd985c501099b76979b5cc4232be))
* tool calls, confirmation and questions in the chat UI (spec 004) ([#35](https://github.com/claudiom248/quorum/issues/35)) ([6ffb7eb](https://github.com/claudiom248/quorum/commit/6ffb7ebacc8e40c60620ac22344cf2078f29eca2))
* tool foundations with permissions, native tools and activation (spec 004) ([#28](https://github.com/claudiom248/quorum/issues/28)) ([d2cffe8](https://github.com/claudiom248/quorum/commit/d2cffe8f8643aba3ce9339ddab39d7b680e2650d))
* tool-calling loop with native tools (spec 004) ([#31](https://github.com/claudiom248/quorum/issues/31)) ([948497e](https://github.com/claudiom248/quorum/commit/948497ee88fdd8e6d5cb34d5964445e972d5dec1))

## [0.7.0](https://github.com/claudiom248/quorum/compare/v0.6.1...v0.7.0) (2026-10-16)


### Features

* use the whole context budget and show a context gauge (spec 002) ([#25](https://github.com/claudiom248/quorum/issues/25)) ([00272cc](https://github.com/claudiom248/quorum/commit/00272ccf001fa89708b2adc9c15bbe26f6b519df))

## [0.6.1](https://github.com/claudiom248/quorum/compare/v0.6.0...v0.6.1) (2026-10-16)


### Bug Fixes

* make the Chat button in the app bar navigate ([#22](https://github.com/claudiom248/quorum/issues/22)) ([437858a](https://github.com/claudiom248/quorum/commit/437858ae93c2bba25096280d795135cf9b84e10f))

## [0.6.0](https://github.com/claudiom248/quorum/compare/v0.5.0...v0.6.0) (2026-10-15)


### Features

* attachments panel and context budget in the chat (spec 002) ([#20](https://github.com/claudiom248/quorum/issues/20)) ([e3e4571](https://github.com/claudiom248/quorum/commit/e3e45712b81d0935f1038fbb1a6c983397bd63f3))
* conversation attachments with text formats (spec 002) ([#17](https://github.com/claudiom248/quorum/issues/17)) ([2163c36](https://github.com/claudiom248/quorum/commit/2163c362cf42d5c7febc2f14eeb9ea241a70602e))
* read PDF and DOCX attachments (spec 002) ([#19](https://github.com/claudiom248/quorum/issues/19)) ([5417673](https://github.com/claudiom248/quorum/commit/5417673656be850a82172c2efe41881e01650eb4))

## [0.5.0](https://github.com/claudiom248/quorum/compare/v0.4.0...v0.5.0) (2026-10-15)


### Features

* attachments in the model context budget (spec 002) ([#15](https://github.com/claudiom248/quorum/issues/15)) ([c249f2c](https://github.com/claudiom248/quorum/commit/c249f2c0b6708fe91ef19aaba0727a85e8dfeb23))

## [0.4.0](https://github.com/claudiom248/quorum/compare/v0.3.0...v0.4.0) (2026-10-15)


### Features

* web UI, reasoning stream and container image (spec 001) ([#12](https://github.com/claudiom248/quorum/issues/12)) ([9bbbb7d](https://github.com/claudiom248/quorum/commit/9bbbb7dacea4535250e48aa2421382c1339fa307))

## [0.3.0](https://github.com/claudiom248/quorum/compare/v0.2.0...v0.3.0) (2026-10-15)


### Features

* authentication, assistant settings and streaming chat (spec 001) ([#10](https://github.com/claudiom248/quorum/issues/10)) ([f99643c](https://github.com/claudiom248/quorum/commit/f99643c4698e7bb3dc196dd1887818314101d349))

## [0.2.0](https://github.com/claudiom248/quorum/compare/v0.1.0...v0.2.0) (2026-10-15)


### Features

* setup mode and atomic first-run setup (spec 001) ([#7](https://github.com/claudiom248/quorum/issues/7)) ([e04114e](https://github.com/claudiom248/quorum/commit/e04114ed49259f67f14bf2f89813ce61858c7e31))

## 0.1.0 (2026-10-15)


### Features

* platform foundations (spec 001) ([#3](https://github.com/claudiom248/quorum/issues/3)) ([7b94d69](https://github.com/claudiom248/quorum/commit/7b94d691bc198f130fbba96753060dac78f4cfc9))
