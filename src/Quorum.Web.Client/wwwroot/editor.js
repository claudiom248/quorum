// Monaco is large: it is loaded on demand, only when a page with an editor opens.
window.quorumEditor = (() => {
    const base = "_content/BlazorMonaco";
    let loading = null;

    const script = (src) => new Promise((resolve, reject) => {
        const element = document.createElement("script");
        element.src = src;
        element.onload = resolve;
        element.onerror = () => reject(new Error("Cannot load " + src));
        document.head.appendChild(element);
    });

    return {
        load: () => {
            loading ??= (async () => {
                await script(`${base}/jsInterop.js`);
                await script(`${base}/lib/monaco-editor/min/vs/loader.js`);
                window.require.config({ paths: { vs: `${base}/lib/monaco-editor/min/vs` } });
                await script(`${base}/lib/monaco-editor/min/vs/editor/editor.main.js`);

                // editor.main.js pulls its modules asynchronously: the `monaco` global exists only once they are in.
                await new Promise((resolve, reject) => {
                    const started = Date.now();
                    const check = () => {
                        if (window.monaco) {
                            resolve();
                        } else if (Date.now() - started > 30000) {
                            reject(new Error("Monaco did not initialise"));
                        } else {
                            setTimeout(check, 25);
                        }
                    };
                    check();
                });
            })();
            return loading;
        }
    };
})();
