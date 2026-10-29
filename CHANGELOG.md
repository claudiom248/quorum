# Changelog

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
