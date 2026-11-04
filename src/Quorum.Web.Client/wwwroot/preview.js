// Host side of the preview panel (spec 014). This file runs in the app's origin; the viewer under preview/ runs
// sandboxed with an opaque origin, and this is the only bridge between the two (D-6, D-8). Messages are accepted
// only from the viewer's own window, and only data ever crosses: the content text travels as a string.
window.quorumPreview = (() => {
    // The Preview button that opened the current panel: closing returns the focus to it (AC-9).
    let opener = null;
    const viewers = new WeakMap();

    // D-20: between 320 px and 70% of the window.
    const clampWidth = (width) => Math.min(Math.max(width, 320), Math.round(window.innerWidth * 0.7));

    // The viewer document (D-6, D-7, D-15). A sandboxed frame without an origin is blocked from loading the app's own
    // address by browsers with local network access protection, so the document is built here from the files under
    // preview/ and given to the frame as srcdoc. The viewer's policy is permissive on purpose: a nested frame inherits
    // it, and the nested frame's own policy (spec 014, D-9) decides its scripts and images.
    const viewerPolicy = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data: https:; " +
        "font-src data:; form-action 'none'; base-uri 'none'; frame-src about:";

    // The libraries each kind needs, fetched only when a block of that kind is previewed (AC-35, AC-17).
    const kindAssets = {
        json: { styles: ["preview/lib/json-formatter-js/json-formatter.css"], scripts: ["preview/lib/json-formatter-js/json-formatter.umd.js"] },
        mermaid: { styles: [], scripts: ["preview/lib/mermaid/mermaid.min.js"] },
    };

    const texts = new Map();
    const fetchText = (path) => {
        let text = texts.get(path);
        if (!text) {
            text = fetch(path).then((response) => {
                if (!response.ok) {
                    throw new Error(`${path} answered ${response.status}`);
                }

                return response.text();
            });
            texts.set(path, text);
            text.catch(() => texts.delete(path)); // a Retry may fetch it again
        }

        return text;
    };

    const buildViewer = async (kind) => {
        const assets = kindAssets[kind] ?? { styles: [], scripts: [] };
        const [css, script, styles, scripts] = await Promise.all([
            fetchText("preview/viewer.css"),
            fetchText("preview/viewer.js"),
            Promise.all(assets.styles.map(fetchText)),
            Promise.all(assets.scripts.map(fetchText)),
        ]);

        // Embedded text must not close its own element; the vendored files are checked for it here.
        for (const text of [...scripts, script]) {
            if (/<\/?script/i.test(text)) {
                throw new Error("A vendored library contains a script tag and cannot be embedded.");
            }
        }

        return [
            "<!doctype html>",
            '<html lang="en"><head><meta charset="utf-8"><title>Quorum preview viewer</title>',
            `<meta http-equiv="Content-Security-Policy" content="${viewerPolicy}">`,
            `<style>${css}${styles.join("\n")}</style>`,
            '</head><body><div id="root"></div>',
            ...scripts.map((text) => `<script>${text}</script>`),
            `<script>${script}</script>`,
            "</body></html>",
        ].join("\n");
    };

    return {
        // One delegated handler for every Preview button of the message list (D-23): the block's text is read from
        // the DOM node, never from an attribute.
        bindButtons: (container, dotNetRef) => {
            if (!container || container.dataset.previewBound === "1") {
                return;
            }

            container.dataset.previewBound = "1";
            container.addEventListener("click", (event) => {
                const button = event.target.closest(".preview-open");
                if (!button || !container.contains(button)) {
                    return;
                }

                const block = button.closest(".code-block");
                const code = block?.querySelector("pre code") ?? block?.querySelector("pre");
                if (!code) {
                    return;
                }

                opener = button;
                dotNetRef.invokeMethodAsync("OpenPreviewFromDom", button.dataset.previewKind, code.textContent ?? "");
            });
        },

        focusOpener: () => {
            opener?.focus();
            opener = null;
        },

        // Watches the viewer's frame: messages from any other window are ignored (D-8, AC-27), and a viewer that
        // does not answer within 5 s counts as failed (D-19, AC-33).
        attachViewer: (iframe, dotNetRef) => {
            if (!iframe || viewers.has(iframe)) {
                return;
            }

            const state = { ready: false, failed: false, queued: [] };
            viewers.set(iframe, state);

            state.timer = window.setTimeout(() => {
                if (!state.ready) {
                    state.failed = true;
                    dotNetRef.invokeMethodAsync("OnViewerTimeout");
                }
            }, 5000);

            window.addEventListener("message", (event) => {
                if (state.failed || event.source !== iframe.contentWindow) {
                    return;
                }

                const message = event.data ?? {};
                switch (message.type) {
                    case "ready":
                        state.ready = true;
                        window.clearTimeout(state.timer);
                        for (const queued of state.queued) {
                            iframe.contentWindow?.postMessage(queued, "*");
                        }
                        state.queued = [];
                        break;
                    case "rendered":
                        dotNetRef.invokeMethodAsync("OnPreviewRendered", message.height ?? 0);
                        break;
                    case "error":
                        dotNetRef.invokeMethodAsync("OnPreviewError", message.code ?? "library", message.message ?? "");
                        break;
                    case "escape":
                        // The focus can be inside the viewer, where the page cannot see the key (D-3).
                        dotNetRef.invokeMethodAsync("OnPreviewEscape");
                        break;
                }
            });
        },

        // Sends to the viewer, queueing until it said ready. The viewer's origin is opaque, so the target is "*":
        // only that one sandboxed window can receive it, and the payload is model output, never app data.
        postToViewer: (iframe, message) => {
            const state = iframe ? viewers.get(iframe) : null;
            if (!state || state.failed) {
                return;
            }

            if (state.ready) {
                iframe.contentWindow?.postMessage(message, "*");
            } else {
                state.queued.push(message);
            }
        },

        // Builds the viewer document for the kind and gives it to the frame (the frame's messages are handled by
        // attachViewer). A file that does not load is the library failure of D-19.
        loadViewer: (iframe, kind, dotNetRef) => {
            buildViewer(kind).then(
                (html) => {
                    iframe.srcdoc = html;
                },
                (error) => {
                    const state = viewers.get(iframe);
                    if (state) {
                        state.failed = true;
                        window.clearTimeout(state.timer);
                    }

                    dotNetRef.invokeMethodAsync("OnPreviewError", "library", error?.message ?? String(error));
                });
        },

        copyText: async (text) => {
            try {
                await navigator.clipboard.writeText(text);
                return true;
            } catch {
                return false;
            }
        },

        // A local Blob and a transient link: the file is saved without any request (D-4, AC-20).
        download: (fileName, text) => {
            const url = URL.createObjectURL(new Blob([text], { type: "text/plain;charset=utf-8" }));
            const link = document.createElement("a");
            link.href = url;
            link.download = fileName;
            document.body.appendChild(link);
            link.click();
            link.remove();
            URL.revokeObjectURL(url);
        },

        storageGet: (key) => {
            try {
                return window.localStorage.getItem(key);
            } catch {
                return null; // AC-31: no storage, no stored choice, the dialog is shown every time.
            }
        },

        storageSet: (key, value) => {
            try {
                window.localStorage.setItem(key, value);
                return true;
            } catch {
                return false;
            }
        },

        storageRemove: (key) => {
            try {
                window.localStorage.removeItem(key);
            } catch {
                // Without storage there is nothing to remove.
            }
        },

        // The palette the viewer's chrome and renderers follow (D-14): colour values only, never CSS from the page.
        themeColors: () => {
            const style = getComputedStyle(document.documentElement);
            const read = (name) => style.getPropertyValue(name).trim();
            return {
                background: read("--mud-palette-background"),
                surface: read("--mud-palette-surface"),
                text: read("--mud-palette-text-primary"),
                textSecondary: read("--mud-palette-text-secondary"),
                lines: read("--mud-palette-lines-default"),
                primary: read("--mud-palette-primary"),
            };
        },

        // Restores the stored width and makes the left edge draggable (D-20).
        initPanel: (panel, handle, widthKey) => {
            if (!panel) {
                return;
            }

            try {
                const stored = parseInt(window.localStorage.getItem(widthKey) ?? "", 10);
                if (!Number.isNaN(stored)) {
                    panel.style.width = clampWidth(stored) + "px";
                }
            } catch {
                // Without storage the panel keeps its default width.
            }

            if (!handle || handle.dataset.bound === "1") {
                return;
            }

            handle.dataset.bound = "1";
            handle.addEventListener("pointerdown", (event) => {
                event.preventDefault();
                handle.setPointerCapture(event.pointerId);
                const startX = event.clientX;
                const startWidth = panel.getBoundingClientRect().width;

                const move = (e) => {
                    panel.style.width = clampWidth(startWidth + (startX - e.clientX)) + "px";
                };
                const up = () => {
                    handle.removeEventListener("pointermove", move);
                    handle.removeEventListener("pointerup", up);
                    try {
                        window.localStorage.setItem(widthKey, String(Math.round(panel.getBoundingClientRect().width)));
                    } catch {
                        // The width simply is not remembered.
                    }
                };

                handle.addEventListener("pointermove", move);
                handle.addEventListener("pointerup", up);
            });
        },
    };
})();
