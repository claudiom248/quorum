# Changelog

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
