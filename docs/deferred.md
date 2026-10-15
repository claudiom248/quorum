# Deferred work

Backlog of things decided *not* to do yet, so they are not forgotten. An item enters here when a spec or a conversation explicitly postpones it; it leaves when a spec picks it up. Specs keep their own "Deferred Decisions" table with the fallback in force: this file is the cross-spec index.

| Item | Origin | Why deferred | Revisit when |
|---|---|---|---|
| Keep the original uploaded file (not only the extracted text) | Spec 002 | The first iteration stores only the extracted text: less sensitive data at rest, no file storage to manage | Re-extraction, re-download of the original or knowledge base ingestion (spec 003) need it |
| XLSX attachments | Spec 002 | Out of the MVP formats; CSV covers tabular text | A user need for spreadsheets with several sheets or formulas |
| OCR for scanned PDFs | Spec 002 | Heavy dependency in the image, language-dependent; scanned PDFs are rejected with a clear error | Real requests for scanned documents |
| Image attachments | Spec 002 | Needs a vision-capable model, not guaranteed with the local models in the presets | A vision model in the presets |
| Text encodings other than UTF-8 (e.g. Windows-1252 CSV from Excel) | Spec 002 | Rejected with a clear error asking to save as UTF-8; detection can guess wrong on accented text | Real user need |
| Room for the model's answer in the context budget | Specs 001/002 | Nothing is reserved and no maximum output is set; keeps the budget rule simple | Truncated or failed answers with large attachments |
| Documents longer than the context budget | Spec 002 | Rejected with a clear error; no silent truncation or automatic summarization | Knowledge bases (spec 003) |
| Antivirus/malware scan of uploaded files (e.g. ClamAV) | Spec 002 | Not considered for now: other mitigations come first (extraction never executes or resolves file content, bounded reads, hardened XML); a scanner is an extra service to operate | Other mitigations prove insufficient, compliance requirements, or any feature that stores or re-serves the original file |
| License key verification | Spec 001 | Runs as implicit Community without a key | Dedicated Licensing spec |

## Planned specs

Decided on 2026-10-15 when splitting the MVP; each gets its own spec, plan and PRs.

| Spec | Topic |
|---|---|
| 002 | Conversation attachments: text extracted from uploaded files used as context of one conversation |
| 003 | Knowledge bases (RAG): admin-defined collections on Qdrant/pgvector, ingestion, chunking, citations |
| 004 | MCP tools: client, registry, per-agent/role permissions, human confirmation, audit log |
| 005 | Declarative config and topology: format for assistants, agents and topologies, multi-agent execution |
| 006 | Web search as a tool (after 004) |
| - | Licensing (no number yet) |
