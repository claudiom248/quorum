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
