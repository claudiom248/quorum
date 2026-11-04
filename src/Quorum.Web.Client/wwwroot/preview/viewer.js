// The preview viewer (spec 014, D-6 to D-14). Runs inside <iframe sandbox="allow-scripts"> with an opaque origin:
// no access to the app, and its served CSP (D-7) allows no network at all. Untrusted text arrives as strings in
// postMessage payloads and is only ever inserted as textContent, srcdoc or a data URI — never evaluated, never
// parsed as HTML in this document. The HTML kind renders in a second, nested sandboxed frame (D-9).
(() => {
    "use strict";

    const root = document.getElementById("root");
    const scripts = new Map();

    // The viewer talks only with its parent and only sends data (D-8). Its origin is opaque, so the target is "*";
    // nothing it sends is sensitive: state, a height and error messages.
    const send = (message) => window.parent.postMessage(message, "*");

    const loadScript = (src) => {
        let promise = scripts.get(src);
        if (!promise) {
            promise = new Promise((resolve, reject) => {
                const element = document.createElement("script");
                element.src = src;
                element.onload = resolve;
                element.onerror = () => reject(new Error("Cannot load " + src));
                document.head.appendChild(element);
            }).catch((error) => {
                scripts.delete(src); // a retry after a failure may try again
                throw error;
            });
            scripts.set(src, promise);
        }
        return promise;
    };

    const applyTheme = (theme) => {
        if (!theme) {
            return;
        }

        const style = document.documentElement.style;
        for (const [name, value] of Object.entries(theme)) {
            if (name !== "dark" && typeof value === "string" && value) {
                style.setProperty("--pv-" + name.replace(/[A-Z]/g, (c) => "-" + c.toLowerCase()), value);
            }
        }
        document.body.dataset.theme = theme.dark ? "dark" : "light";
    };

    const notice = (text) => {
        const div = document.createElement("div");
        div.className = "pv-notice";
        div.textContent = text;
        root.appendChild(div);
    };

    // ---- HTML (D-9): a nested frame, srcdoc, no scripts and no network unless the person consented (D-17, D-18).
    const framePolicy = (options) => [
        "default-src 'none'",
        options.runScripts ? "script-src 'unsafe-inline'" : "",
        "style-src 'unsafe-inline'",
        "img-src data:" + (options.loadRemoteImages ? " https:" : ""),
        "font-src data:",
        "form-action 'none'",
        "base-uri 'none'",
    ].filter(Boolean).join("; ");

    const renderHtml = (text, options) => {
        const frame = document.createElement("iframe");
        frame.className = "pv-html";
        frame.title = "The previewed HTML page, isolated";
        frame.setAttribute("sandbox", options.runScripts ? "allow-scripts" : "");
        // The policy meta comes first, so a looser policy inside the content can only intersect it, never widen it
        // (AC-26). White canvas whatever the theme (D-9).
        frame.srcdoc = '<!DOCTYPE html><html><head><meta charset="utf-8">' +
            '<meta http-equiv="Content-Security-Policy" content="' + framePolicy(options) + '">' +
            '<style>html,body{background:#fff;color:#111;margin:0;padding:.5rem}</style>' +
            "</head><body>" + text + "</body></html>";
        root.appendChild(frame);
    };

    // ---- SVG: an <img> with a data URI never runs script and loads nothing external (D-9).
    const renderSvg = (text) => {
        const image = document.createElement("img");
        image.className = "pv-svg";
        image.alt = "The previewed SVG image";
        image.src = "data:image/svg+xml;charset=utf-8," + encodeURIComponent(text);
        image.addEventListener("error", () => notice("This SVG could not be rendered as an image."));
        root.appendChild(image);
    };

    // ---- JSON (D-11): a collapsible tree, root open one level, values as text.
    const renderJson = async (text) => {
        let parsed;
        try {
            parsed = JSON.parse(text);
        } catch (error) {
            send({ type: "error", code: "parse", message: error.message });
            return;
        }

        await loadScript("lib/json-formatter-js/json-formatter.umd.js");
        const formatter = new window.JSONFormatter(parsed, 1, { hoverPreviewEnabled: false });
        root.appendChild(formatter.render());
    };

    // ---- CSV (D-12): RFC 4180, delimiter guessed from the first line, cells as text only.
    const maxCsvRows = 5000;

    const detectDelimiter = (text) => {
        const line = text.slice(0, text.indexOf("\n") === -1 ? text.length : text.indexOf("\n"));
        const count = (character) => line.split(character).length - 1;
        let best = ",";
        let bestCount = count(",");
        for (const candidate of [";", "\t"]) {
            const found = count(candidate);
            if (found > bestCount) {
                best = candidate;
                bestCount = found;
            }
        }
        return best;
    };

    const parseCsv = (text, delimiter) => {
        const rows = [];
        let row = [];
        let field = "";
        let inQuotes = false;

        for (let i = 0; i < text.length; i++) {
            const c = text[i];
            if (inQuotes) {
                if (c === '"') {
                    if (text[i + 1] === '"') {
                        field += '"';
                        i++;
                    } else {
                        inQuotes = false;
                    }
                } else {
                    field += c;
                }
            } else if (c === '"' && field.length === 0) {
                inQuotes = true;
            } else if (c === delimiter) {
                row.push(field);
                field = "";
            } else if (c === "\n") {
                row.push(field);
                rows.push(row);
                row = [];
                field = "";
            } else if (c !== "\r") {
                field += c;
            }
        }

        if (field.length > 0 || row.length > 0) {
            row.push(field);
            rows.push(row);
        }

        return rows;
    };

    const renderCsv = (text) => {
        const rows = parseCsv(text, detectDelimiter(text));
        if (rows.length > maxCsvRows) {
            send({
                type: "error",
                code: "limit",
                message: `This CSV has ${rows.length.toLocaleString("en-US")} rows; the preview limit is ${maxCsvRows.toLocaleString("en-US")}.`,
            });
            return;
        }

        const table = document.createElement("table");
        table.className = "pv-csv";
        rows.forEach((cells, index) => {
            const tr = document.createElement("tr");
            for (const cell of cells) {
                const element = document.createElement(index === 0 ? "th" : "td");
                element.textContent = cell; // never a formula, never a link (D-12)
                tr.appendChild(element);
            }
            table.appendChild(tr); // ragged rows are shown as they are
        });

        const scroll = document.createElement("div");
        scroll.className = "pv-csv-scroll";
        scroll.appendChild(table);
        root.appendChild(scroll);
    };

    // ---- Mermaid (D-13): strict security, no HTML labels, abandoned after 10 s.
    const renderMermaid = async (text, theme) => {
        await loadScript("lib/mermaid/mermaid.min.js");
        window.mermaid.initialize({
            startOnLoad: false,
            securityLevel: "strict",
            htmlLabels: false,
            theme: theme?.dark ? "dark" : "default",
        });

        const container = document.createElement("div");
        container.className = "pv-mermaid";
        root.appendChild(container);

        let timer = 0;
        try {
            const { svg } = await Promise.race([
                window.mermaid.render("pv-mermaid-graph", text),
                new Promise((_, reject) => {
                    timer = window.setTimeout(() => reject(new Error("timeout")), 10000);
                }),
            ]);
            container.innerHTML = svg; // the library's own sanitized output at securityLevel "strict"
        } catch (error) {
            container.remove();
            if (error?.message === "timeout") {
                send({ type: "error", code: "timeout", message: "The Mermaid render took longer than 10 seconds and was abandoned." });
            } else {
                send({ type: "error", code: "parse", message: error?.message ?? String(error) });
            }
        } finally {
            window.clearTimeout(timer);
        }
    };

    const render = async (message) => {
        root.textContent = "";
        applyTheme(message.theme);
        const text = typeof message.text === "string" ? message.text : "";
        const options = message.options ?? {};

        try {
            switch (message.kind) {
                case "html":
                    renderHtml(text, options);
                    break;
                case "svg":
                    renderSvg(text);
                    break;
                case "json":
                    await renderJson(text);
                    break;
                case "csv":
                    renderCsv(text);
                    break;
                case "mermaid":
                    await renderMermaid(text, message.theme);
                    break;
                default:
                    send({ type: "error", code: "parse", message: "This preview kind is not supported." });
                    return;
            }
        } catch (error) {
            // A library that fails to load or a renderer that throws ends here, never as an exception in the app (D-19).
            send({ type: "error", code: "library", message: error?.message ?? String(error) });
            return;
        }

        requestAnimationFrame(() => send({ type: "rendered", height: document.documentElement.scrollHeight }));
    };

    window.addEventListener("message", (event) => {
        if (event.source !== window.parent) {
            return; // D-8: nothing from the previewed page or any other window is acted on (AC-27).
        }

        const message = event.data ?? {};
        if (message.type === "render") {
            void render(message);
        } else if (message.type === "theme") {
            applyTheme(message.theme);
        }
    });

    document.addEventListener("keydown", (event) => {
        if (event.key === "Escape") {
            send({ type: "escape" }); // Esc closes while the focus is inside the viewer (D-3).
        }
    });

    send({ type: "ready" });
})();
