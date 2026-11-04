// The preview viewer (spec 014, D-6 to D-14). The host builds the viewer document from viewer.css, this file and the
// libraries of the kind, and gives it to <iframe sandbox="allow-scripts"> as srcdoc: opaque origin, no access to the
// app, and a CSP (D-7) that allows no network. Untrusted text arrives as strings in postMessage payloads and is only
// ever inserted as textContent, srcdoc or a data URI — never evaluated, never parsed as HTML in this document. The
// HTML kind renders in a second, nested sandboxed frame (D-9).
(() => {
    "use strict";

    const root = document.getElementById("root");

    // The viewer talks only with its parent and only sends data (D-8). Its origin is opaque, so the target is "*";
    // nothing it sends is sensitive: state, a height and error messages.
    const send = (message) => window.parent.postMessage(message, "*");

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
    // With the consent to remote resources (D-18), styles, fonts and scripts may come from these five CDNs over HTTPS
    // only, and scripts also need the consent to run them. Any other host stays blocked.
    const cdns = "https://cdnjs.cloudflare.com https://cdn.jsdelivr.net https://unpkg.com " +
        "https://fonts.googleapis.com https://fonts.gstatic.com";

    const framePolicy = (options) => [
        "default-src 'none'",
        options.runScripts ? "script-src 'unsafe-inline'" + (options.loadRemoteResources ? " " + cdns : "") : "",
        "style-src 'unsafe-inline'" + (options.loadRemoteResources ? " " + cdns : ""),
        "img-src data:" + (options.loadRemoteResources ? " " + cdns : ""),
        "font-src data:" + (options.loadRemoteResources ? " " + cdns : ""),
        "form-action 'none'",
        "base-uri 'none'",
    ].filter(Boolean).join("; ");

    // A refresh or a base element in the content would make the frame navigate by itself: the CSP blocks that and the
    // frame is left empty or with the browser's error page, so the viewer leaves them out. Links keep their href. DOMParser
    // builds an inert document: no script runs and nothing is requested while it parses.
    const withoutFrameNavigation = (text) => {
        const parsed = new DOMParser().parseFromString(text, "text/html");
        parsed.querySelectorAll('meta[http-equiv="refresh" i], base').forEach((element) => element.remove());
        return parsed.head.innerHTML + parsed.body.innerHTML;
    };

    const renderHtml = (text, options) => {
        const frame = document.createElement("iframe");
        frame.className = "pv-html";
        frame.title = "The previewed HTML page, isolated";
        // Popups are allowed so that a link opens its address in a new tab (D-10); the new tab is not sandboxed and has
        // no access to the app. Scripts run only with the person's consent (D-17).
        frame.setAttribute("sandbox", (options.runScripts ? "allow-scripts " : "") + "allow-popups allow-popups-to-escape-sandbox");
        // The policy meta comes first, so a looser policy inside the content can only intersect it, never widen it
        // (AC-26). The base target sends every link, static or script-made, to a new tab (D-10). White canvas whatever the
        // theme (D-9).
        frame.srcdoc = '<!DOCTYPE html><html><head><meta charset="utf-8">' +
            '<meta http-equiv="Content-Security-Policy" content="' + framePolicy(options) + '">' +
            '<base target="_blank">' +
            '<style>html,body{background:#fff;color:#111;margin:0;padding:.5rem}</style>' +
            "</head><body>" + withoutFrameNavigation(text) + "</body></html>";
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
    const renderJson = (text) => {
        let parsed;
        try {
            parsed = JSON.parse(text);
        } catch (error) {
            send({ type: "error", code: "parse", message: error.message });
            return;
        }

        if (!window.JSONFormatter) {
            throw new Error("json-formatter-js is not part of this viewer");
        }

        const formatter = new window.JSONFormatter(parsed, 1, { hoverPreviewEnabled: false });
        const rendered = formatter.render();
        // A string that looks like a URL comes back as a link; the viewer shows it as text (AC-14, D-11).
        for (const anchor of rendered.querySelectorAll("a[href]")) {
            anchor.removeAttribute("href");
            anchor.classList.remove("json-formatter-url");
        }

        root.appendChild(rendered);
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
        if (!window.mermaid) {
            throw new Error("mermaid is not part of this viewer");
        }

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
                    renderJson(text);
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
