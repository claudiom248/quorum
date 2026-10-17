window.quorumChat = {
    // Follows new content only while the reader is near the bottom, so older messages can be read
    // while an answer streams. `pin` turns following back on (new message, opened conversation).
    scrollToEnd: (element) => {
        if (!element) {
            return;
        }

        if (element.dataset.bound !== "1") {
            element.dataset.bound = "1";
            element.dataset.pinned = "1";
            element.addEventListener("scroll", () => {
                const distance = element.scrollHeight - element.scrollTop - element.clientHeight;
                element.dataset.pinned = distance < 80 ? "1" : "0";
            }, { passive: true });
        }

        if (element.dataset.pinned !== "0") {
            element.scrollTop = element.scrollHeight;
        }
    },

    // A modal dialog: focus goes to the safe button and Tab cycles inside it, so the page behind cannot be reached by keyboard.
    focusDialog: (dialog) => {
        if (!dialog) {
            return;
        }

        const buttons = () => [...dialog.querySelectorAll("button:not([disabled])")];
        (dialog.querySelector("button[data-initial-focus]") ?? buttons()[0] ?? dialog).focus();

        dialog.addEventListener("keydown", (event) => {
            if (event.key !== "Tab") {
                return;
            }

            const items = buttons();
            if (items.length === 0) {
                event.preventDefault();
                return;
            }

            const first = items[0];
            const last = items[items.length - 1];
            if (event.shiftKey && document.activeElement === first) {
                event.preventDefault();
                last.focus();
            } else if (!event.shiftKey && document.activeElement === last) {
                event.preventDefault();
                first.focus();
            }
        });
    },

    pin: (element) => {
        if (element) {
            element.dataset.pinned = "1";
            element.scrollTop = element.scrollHeight;
        }
    },

    // Enter sends, Shift+Enter inserts a new line.
    bindComposer: (element, dotNetRef) => {
        if (!element || element.dataset.bound === "1") {
            return;
        }

        element.dataset.bound = "1";
        element.addEventListener("keydown", (event) => {
            if (event.key === "Enter" && !event.shiftKey && !event.isComposing) {
                event.preventDefault();
                dotNetRef.invokeMethodAsync("SendFromKeyboard");
            }
        });
    },

    confirm: (message) => window.confirm(message)
};
