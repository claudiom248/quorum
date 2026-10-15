# Plan 002: Conversation Attachments

**Spec:** `docs/specs/002-conversation-attachments.md` (Approved 2026-10-15)
**Architecture:** Modular Monolith; Vertical Slice inside each module.
**Mode:** `/tdd`: every acceptance criterion becomes a failing test first.
**Status:** Approved (2026-10-15)

## Decisions
| Topic | Choice |
|---|---|
| Ownership | Attachments belong to **Conversations** (entity, endpoints, extraction). The context budget and the prompt belong to **Agents**, which already owns the model endpoint, the system prompt and `ContextWindow` |
| Cross-module contract | `IAssistantChat` (Agents.Contracts) gains attachments: `StartAnswerAsync(assistantId, history, attachments, ct)` and `MeasureContextAsync(assistantId, history, attachments, ct)` returning `ContextUsage`. Conversations decides on upload and renders the indicator from `ContextUsage`; it never reads the model endpoint configuration |
| Prompt | Agents builds the effective system prompt: the configured prompt + a data block per attachment (file name, delimiter generated per request from a random token so content cannot close it, instruction that it is reference material). The characters of the whole block, delimiters included, count against the budget |
| Budget rule | Usable characters = `ContextTokenBudget × 0.8 × 4` (unchanged). Order: system prompt, then **all attachments**, then the most recent messages that fit. Last message must fit on its own (`chat.message_too_long`, with a hint when there are attachments); attachments that do not fit with the system prompt fail with `chat.attachments_exceed_budget` |
| Extraction placement | Internal to Conversations behind `IDocumentTextExtractor` (one implementation per format). Moved to its own project when knowledge bases (spec 003) need it; not placed in `Quorum.Shared` (no business logic there) |
| Libraries | PdfPig (PDF, Apache-2.0), DocumentFormat.OpenXml (DOCX, MIT), CsvHelper (CSV, Apache-2.0/MS-PL). Versions pinned in `Directory.Packages.props` at implementation, no copyleft licenses (spec NFR) |
| Detection | By content: `%PDF-` header; DOCX = zip containing `word/document.xml`; TXT, Markdown, CSV = strict UTF-8 (BOM stripped). The extension only chooses between TXT, Markdown and CSV, which share the UTF-8 check |
| Hardening | A file signature is identification, not safety: the attack surface is the parser. Nothing is extracted to disk and nothing from the file is executed or resolved. DOCX: entries are read through a bounded stream that counts the bytes actually read (declared sizes are not trusted), with caps on total decompressed size, number of entries and compression ratio; only the known parts (`word/document.xml`, numbering, styles) are read; XML is read with `DtdProcessing.Prohibit` and no resolver; external relationships are never followed (no network access during extraction); a package containing `vbaProject.bin` or embedded OLE objects is rejected as `attachment.unreadable`. PDF: managed parser, no script execution. No antivirus scan in this spec (deferred) |
| Timeout | `CancellationTokenSource` of 60 s linked to the request; extraction runs off the request thread (`Task.Run`) and checks the token between pages/paragraphs. A single pathological page that never yields is a known limit (see Risks) |
| Storage | `conversations.attachments`: Text (`text`), ContentHash (SHA-256 hex), unique index `(ConversationId, ContentHash)` so duplicates are enforced atomically, FK to conversations with cascade delete, `TenantId` global filter like the other entities |
| Upload endpoint | Minimal API with `IFormFile`, `RequestSizeLimit` of 5 MB + multipart overhead, and a streaming read capped at 5 MB + 1 byte so an oversized body is never buffered whole |
| Concurrency | The existing per-conversation lock (`ConversationLocks`) is acquired **first** by upload and delete (fast 409 while an answer streams), then extraction and checks run under it; the same lock serializes concurrent uploads, so the 10-file limit and the budget check cannot race |
| Logging | Ids, sizes, media type and result codes only; never file names or text |
| UI | A conversation panel component (list, remove, preview dialog, budget meter) next to the chat; `InputFile` limited to 5 MB; attaching before the first message creates the conversation through the existing `POST /conversations` |
| Tests | xUnit v3 + Testcontainers as in plan 001; PDF and DOCX fixtures are generated in tests (PdfPig document builder, OpenXml SDK); a small committed binary fixture for a password-protected PDF; fake model server asserts the request body (attachment block, truncated history) |

## Steps

### PR 1 - Budget and contract (`feat/002-budget`)
1. Default `ContextTokenBudget` becomes 16384 (`AssistantDefaults`, wizard default); amend spec 001's domain table. AC-19.
2. Agents.Contracts: `ChatAttachment`, `ContextUsage`, new `IAssistantChat` signatures; `ContextWindow` reserves attachments before history and returns the new errors; `AssistantChat` builds the data block with a per-request delimiter. Existing callers pass an empty attachments list. AC-16, AC-17, AC-18, AC-20, AC-21 (at `IAssistantChat` level, against the fake model server).

### PR 2 - Attachments API with text formats (`feat/002-attachments`)
3. Conversations: `Attachment` entity, DbContext mapping and filter, migration, `GET /conversations/{id}` returns `attachments[]`. AC-24, AC-25.
4. Upload (`POST .../attachments`) for TXT, Markdown, CSV: size, content detection, UTF-8, duplicate, 10-file limit, budget check via `MeasureContextAsync`, conversation lock (409). AC-1, AC-3, AC-4, AC-8, AC-9, AC-11, AC-12, AC-14, AC-22.
5. List, text preview, delete, `GET .../context`. AC-13, AC-15, AC-26.
6. Messages and regenerate pass the conversation's attachments to `IAssistantChat`; error `chat.attachments_exceed_budget`; log hygiene test. AC-16 to AC-18 end to end, AC-23, AC-27.

### PR 3 - PDF and DOCX (`feat/002-documents`)
7. PDF extractor (PdfPig): text per page, `attachment.no_text` for scanned PDFs, encrypted/corrupt → `attachment.unreadable`. AC-5, AC-6.
8. DOCX extractor (OpenXml): headings, lists, tables to Markdown; corrupt or protected → `attachment.unreadable`. Hardening as in the decision above, each with its own test: a zip bomb (declared size lying about the real one), a document with an external DTD/entity, one with an external relationship (asserts no network call), and a package with macros. AC-5, AC-10.
9. Extraction time limit and cancellation. AC-7.

### PR 4 - UI (`feat/002-ui`)
10. Panel component, `InputFile` upload, preview dialog, removal, budget meter fed by `GET .../context` (refreshed after upload, removal, message), lazy conversation creation, error messages per code. AC-2, AC-28.
11. Wizard and settings hint: the model server must offer a context at least as large as the budget (Ollama: `OLLAMA_CONTEXT_LENGTH` or `num_ctx`). Close with `/verify` and a manual run with a real PDF and DOCX against a real Ollama.

## Risks
- **Silent truncation by the model server**: Ollama ignores the Quorum budget and truncates at its own `num_ctx` (4096 unless configured), so a 16384 budget can still lose the document without any error. Mitigation: the hint in step 11 and docs; a runtime check is not possible through the OpenAI-compatible API.
- **Token estimate**: characters / 4 is rough for non-Latin text and for dense Markdown tables; the 20% margin of spec 001 stays. Tables are emitted without padding to keep them compact.
- **PDF extraction quality**: multi-column layouts and tables come out as plain text in reading order. Accepted by the spec (PDF is plain text per page); fixtures use realistic documents.
- **Non-cooperative extraction**: PdfPig does not accept a cancellation token, so the 60 s limit is checked between pages; one pathological page can exceed it. Mitigation: 5 MB upload cap; process isolation is out of scope.
- **Multipart and antiforgery in Minimal APIs**: `IFormFile` binding enables antiforgery validation by default and the app already has its own header-based middleware. Prototype the upload endpoint at the start of step 4 and decide how to reconcile them.
- **Lock held during extraction** (up to 60 s) makes message sends in that conversation return 409 meanwhile. Acceptable and consistent with D-17.
- **Library licenses and versions**: confirm each license (including transitive dependencies) when adding the packages; fall back to hand-written DOCX/CSV readers if one is not acceptable.
