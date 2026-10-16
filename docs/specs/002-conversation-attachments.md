# Spec 002: Conversation Attachments

**Status:** Approved (2026-10-15)
**Date:** 2026-10-15

## Problem
A user who wants the assistant to work on a document (summarize it, answer questions about it, extract something from it) has to copy and paste its text into the chat, losing structure (tables, headings) and with no idea whether it fits the model's context. Spec 001 delivers chat with a single assistant; this spec lets the user attach files to a conversation so the assistant can use their content, with a clear view of how much of the model's context is used.

Attachments are **conversation-scoped context**, not a knowledge base: nothing is indexed and nothing is reused across conversations. Reusable, searchable corpora are knowledge bases (spec 003).

## Scope
### In
- Attach one or more files to a conversation; the extracted text is part of the model's context for the whole conversation, while it fits the budget.
- Formats: **PDF** (with a text layer), **DOCX**, **TXT**, **Markdown**, **CSV**.
- List, preview (read-only) and remove the attachments of a conversation.
- Overall context budget indicator for a conversation (system prompt + messages + attachments).
- Default context budget raised to 16384 tokens in the wizard (existing installations keep theirs).

### Out
- Indexing, embeddings, vector store, reuse across conversations (knowledge bases, spec 003).
- XLSX, OCR of scanned PDFs, images, text encodings other than UTF-8 (see `docs/deferred.md`).
- Truncation or automatic summarization of documents that do not fit.
- Keeping the original file (only the extracted text is stored).
- Asynchronous extraction with progress state.

## Decisions
| # | Decision |
|---|---|
| D-1 | An attachment stays in the model context for the whole conversation, while it fits the budget; the user can remove it. |
| D-2 | If the extracted text does not fit the context budget, the attachment is rejected with a clear error. No silent truncation. |
| D-3 | Only the extracted text is stored; the original file is not kept (future need recorded in `docs/deferred.md`). |
| D-4 | Supported formats: PDF, DOCX, TXT, Markdown, CSV. A PDF without a text layer is rejected with a clear error. |
| D-5 | The UI shows the conversation's overall context budget usage (system prompt + messages + attachments against the model's budget), not only what the attachments take. |
| D-6 | Attachments take priority over message history in the context budget: older messages are dropped first to make room; an attachment is never dropped silently. |
| D-7 | A conversation can have several attachments, up to 10; the sum of their extracted text (with the system prompt) must fit the budget, otherwise the new attachment is rejected. |
| D-8 | The wizard's default context budget is 16384 tokens; existing installations keep their configured value. Rejection messages state how much is missing and that the model server must offer a context at least as large. |
| D-9 | Maximum uploaded file size: 5 MB. |
| D-10 | If the budget is lowered and the existing attachments no longer fit, sending is blocked with an error that says which attachments to remove. Nothing is dropped silently. |
| D-11 | Attachment text reaches the model as a delimited data block in the system prompt (file name + delimiters), with the instruction that it is reference material, not instructions to follow. |
| D-12 | An attachment whose content is identical to one already in the conversation is rejected; same name with different content is accepted. |
| D-13 | Upload is a multipart POST on an existing conversation; the budget is checked at upload. The UI creates an empty conversation when the user attaches before writing the first message. |
| D-14 | The user can read the extracted text of an attachment (read-only preview). |
| D-15 | Text extraction is synchronous within the upload request, with a 60 second limit; beyond it the attachment is rejected. |
| D-16 | The UI lists attachments in a conversation panel (with removal) next to the overall budget indicator; no per-message chips. |
| D-17 | Attaching or removing an attachment while a response is in progress in the same conversation is rejected with 409, like sending a message. |
| D-18 | Regeneration uses the attachments present at that moment. |
| D-19 | Extracted text is Markdown where the format has structure: DOCX with headings, lists and tables; CSV as a Markdown table; PDF as plain text per page, TXT/Markdown unchanged. |
| D-20 | No headroom is reserved for messages: attachments may fill the whole budget. A message that then does not fit is rejected by the spec 001 rule (`chat.message_too_long`), and the error tells the user to remove attachments. |

## Domain Model
Every application entity carries `TenantId`.

| Entity | Module | Main fields | Lifecycle |
|---|---|---|---|
| **Attachment** | Conversations | Id, TenantId, ConversationId, FileName, MediaType, SizeBytes, ContentHash (SHA-256 of the uploaded bytes), Text (extracted, Markdown), CharacterCount, CreatedAt | Created by an upload that passes every check; **hard delete** on removal or together with its conversation |

- The original bytes are never persisted: only `Text` and `ContentHash` (used for duplicate detection).
- `FileName` is for display and for the prompt only: it is never used to build a path, and it is sanitized (no path separators, control characters, length limit).
- **Context usage** is derived, not stored: system prompt + attachments + the messages that fit after them (rule D-6), in characters and as estimated tokens (characters / 4; the 20% margin of spec 001 was dropped, so the whole budget is usable).
- **Module boundaries**: attachments belong to Conversations. Text and the budget decision go through the `Quorum.Modules.Agents.Contracts` project: `IAssistantChat.StartAnswerAsync` receives the conversation's attachments, and a contract query returns the context usage for the indicator and the upload check. Conversations never reads the model endpoint configuration directly.

## API Contract
Errors use ProblemDetails with a stable `code`. All routes are under `/api`; authentication as in spec 001.

| Verb | Route | Request | Response | Errors |
|---|---|---|---|---|
| POST | `/conversations/{id}/attachments` | multipart, one `file` | 201 `{ id, fileName, mediaType, sizeBytes, characterCount, createdAt }` | 400 `attachment.exceeds_budget`; 401; 404; 409 `attachment.duplicate` / `attachment.limit_reached` / response in progress; 413 `attachment.too_large`; 415 `attachment.unsupported_type`; 422 `attachment.unreadable` / `attachment.no_text` / `attachment.extraction_timeout` |
| GET | `/conversations/{id}/attachments` | - | `[{ id, fileName, mediaType, sizeBytes, characterCount, createdAt }]` (oldest first) | 401; 404 |
| GET | `/conversations/{id}/attachments/{attachmentId}/text` | - | `text/markdown`, the extracted text | 401; 404 |
| DELETE | `/conversations/{id}/attachments/{attachmentId}` | - | 204 | 401; 404; 409 response in progress |
| GET | `/conversations/{id}/context` | - | `{ contextTokenBudget, usableTokens, systemPromptTokens, attachmentsTokens, messagesTokens, remainingTokens }` | 401; 404 |

- `GET /conversations/{id}` also returns `attachments[]` (same shape as the list).
- `POST /conversations/{id}/messages` and `/regenerate` gain the error `chat.attachments_exceed_budget` (400, D-10) listing the attachments to remove; `chat.message_too_long` keeps its meaning and its message mentions the attachments when there are any.
- `messagesTokens` counts only the messages that would be sent, not the whole stored history.

## Authorization
- Same as conversations: attachments are visible and editable only by the user of the conversation, within its tenant. Another user's or tenant's conversation or attachment returns 404.
- Only `Admin` exists in this iteration; no new role or permission.

## Edge Cases & Failure Modes
- **Type detection by content, not by name**: the file signature must match the format (PDF header, DOCX as a zip with `word/document.xml`); TXT, Markdown and CSV must be valid UTF-8 (BOM allowed). A mismatch is `attachment.unsupported_type`.
- **Unreadable files**: corrupt, password-protected or encrypted PDF/DOCX → `attachment.unreadable`; a PDF or DOCX with no extractable text (scanned) → `attachment.no_text`.
- **Zip bombs / pathological files**: the 5 MB limit applies to the upload; extraction also has the 60 second limit and a cap on the decompressed size of the DOCX, beyond which the file is `attachment.unreadable`.
- **Budget**: at upload, `system prompt + existing attachments + new attachment` must fit the usable budget, otherwise `attachment.exceeds_budget` stating how many characters are missing (D-2, D-8). With no headroom reserved (D-20) the next message may be rejected until the user removes attachments.
- **Budget lowered afterwards**: sending is blocked (D-10); nothing changes in the stored attachments.
- **Duplicates**: same `ContentHash` in the same conversation → `attachment.duplicate`; the same file in another conversation is allowed.
- **Concurrency**: attaching or removing during an in-progress response → 409 (D-17). Two concurrent uploads on the same conversation are serialized by the conversation lock; the second is checked against the budget after the first.
- **Prompt injection**: the content of a document is untrusted. It is delimited and labelled as data (D-11); delimiters inside the content cannot close the block (the delimiter is neutralized or generated per request). The spec does not claim that this removes the risk, only that the content is never presented as instructions.
- **Deleting the conversation** deletes its attachments; deleting during an in-progress response cancels it as in spec 001.
- **Regeneration** uses the attachments present now (D-18); removing an attachment does not change stored messages.
- **Model unreachable**: unchanged from spec 001; attachments are not affected.

## Non-Functional Requirements
- No performance targets (consistent with spec 001); extraction time is measured, with the hard limit of 60 seconds.
- The text of a document and its file name are never written to the logs; logs carry only ids, sizes and result codes.
- Extraction runs inside the app process (no external service) and honours request cancellation.
- Libraries used for extraction must have a license compatible with a commercial, self-hosted product (permissive; no AGPL/SSPL).
- No outbound telemetry.

## Integrations
- No external service. PDF and DOCX extraction libraries are chosen in the plan.
- Internal contract change between Conversations and Agents (see Domain Model); no events.

## Acceptance Criteria
**Upload**
- **AC-1** Given a conversation, When a supported file within the limits is uploaded, Then it returns 201 and the attachment appears in the list with its character count.
- **AC-2** Given an upload before the first message, When the UI attaches a file, Then it creates the conversation first and the attachment belongs to it.
- **AC-3** Given a file larger than 5 MB, When it is uploaded, Then it returns 413 `attachment.too_large` and nothing is stored.
- **AC-4** Given a file whose content does not match a supported format (including a renamed file), When it is uploaded, Then it returns 415 `attachment.unsupported_type`.
- **AC-5** Given a PDF or DOCX that is corrupt or password-protected, When it is uploaded, Then it returns 422 `attachment.unreadable`.
- **AC-6** Given a PDF without a text layer, When it is uploaded, Then it returns 422 `attachment.no_text`.
- **AC-7** Given an extraction that exceeds 60 seconds, When uploading, Then it returns 422 `attachment.extraction_timeout` and nothing is stored.
- **AC-8** Given a conversation with 10 attachments, When another is uploaded, Then it returns 409 `attachment.limit_reached`.
- **AC-9** Given an attachment already in the conversation, When a file with identical content is uploaded, Then it returns 409 `attachment.duplicate`; a file with the same name and different content is accepted.

**Extraction**
- **AC-10** Given a DOCX with headings, lists and a table, When it is uploaded, Then the extracted text is Markdown preserving them.
- **AC-11** Given a CSV, When it is uploaded, Then the extracted text is a Markdown table.
- **AC-12** Given a TXT or Markdown file with invalid UTF-8, When it is uploaded, Then it returns 415 `attachment.unsupported_type`.
- **AC-13** Given an attachment, When its text is requested, Then the extracted Markdown is returned.

**Budget**
- **AC-14** Given attachments whose text plus the system prompt exceeds the usable budget, When another is uploaded, Then it returns 400 `attachment.exceeds_budget` stating the missing characters.
- **AC-15** Given a conversation, When its context is requested, Then it returns the budget, system prompt, attachments and retained messages usage and the remaining margin.
- **AC-16** Given attachments and a long history, When a message is sent, Then the model receives the system prompt, all attachments and only the most recent messages that fit after them.
- **AC-17** Given attachments that fill the budget, When a message that does not fit is sent, Then it returns 400 `chat.message_too_long` mentioning the attachments and nothing is stored.
- **AC-18** Given a budget lowered below the attachments' size, When a message is sent, Then it returns 400 `chat.attachments_exceed_budget` listing the attachments to remove and nothing is stored.
- **AC-19** Given a new installation, When the wizard is shown, Then the default context budget is 16384 tokens; an existing installation keeps its value.

**Model context**
- **AC-20** Given a conversation with attachments, When a message is sent, Then the request to the model carries each attachment as a delimited block with its file name and the instruction that it is reference material, and no content is placed in the messages.
- **AC-21** Given a document containing the delimiter text, When it is sent to the model, Then the block cannot be closed from inside the content.
- **AC-22** Given a response in progress, When an attachment is uploaded or removed in that conversation, Then it returns 409.
- **AC-23** Given a removed attachment, When the last response is regenerated, Then the model request no longer contains it.

**Authorization and lifecycle**
- **AC-24** Given an attachment of another user or tenant, When any attachment route is called, Then it returns 404.
- **AC-25** Given a conversation with attachments, When it is deleted, Then its attachments are deleted too.
- **AC-26** Given an unauthenticated caller, When any attachment route is called, Then it returns 401.
- **AC-27** Given any upload, When the logs are inspected, Then neither the document text nor the file name appears.

**UI**
- **AC-28** Given a conversation, When the user opens it, Then the panel lists the attachments (name, size, characters) with remove and preview, and the budget indicator shows the overall usage and remaining margin.

## Deferred Decisions
| Decision | Fallback chosen now | Revisit trigger |
|---|---|---|
| Original file kept | Only extracted text and content hash | Re-extraction, original re-download or knowledge base ingestion (spec 003) |
| XLSX, OCR, images, other text encodings | Rejected with `attachment.unsupported_type` / `attachment.no_text` | Real user need; a vision model in the presets |
| Documents longer than the budget | Rejected with a clear error | Knowledge bases (spec 003) |
| Headroom for messages | None (D-20) | Complaints that users fill the budget with attachments |
| Room for the model's answer | None reserved, as in spec 001 (no maximum output); a full budget may truncate or fail the answer | Truncated or failed answers reported with large attachments |
| Text encodings other than UTF-8 (e.g. Windows-1252 CSV from Excel) | Rejected with `attachment.unsupported_type`; the error asks to save the file as UTF-8 | Real user need; encoding detection |

See also `docs/deferred.md`.

## Open Questions
_None._
