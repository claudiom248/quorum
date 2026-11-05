// Reads the files picked in an <input type="file"> for the skill import (spec 017 D-19). A folder picked with webkitdirectory keeps
// each file's path inside the folder, which the server needs to find SKILL.md files; the bytes travel as base64.
window.quorumSkills = {
    // The visible button opens the hidden file input, so the picker is styled by the app (spec 017 D-19).
    pick: function (input) {
        input.click();
    },
    // Shows what was picked next to the button: the names, or the folder's files.
    bindNames: function (input, outputId) {
        input.addEventListener('change', function () {
            const out = document.getElementById(outputId);
            if (!out) return;
            const names = Array.from(input.files || []).map(f => f.webkitRelativePath || f.name);
            if (names.length === 0) {
                out.textContent = input.hasAttribute('webkitdirectory') ? 'No folder selected' : 'No file selected';
                return;
            }
            const shown = names.slice(0, 3).join(', ') + (names.length > 3 ? ', …' : '');
            out.textContent = names.length + (names.length === 1 ? ' file: ' : ' files: ') + shown;
        });
    },
    readFiles: async function (input) {
        const result = [];
        for (const file of Array.from(input.files || [])) {
            const path = file.webkitRelativePath || file.name;
            const bytes = new Uint8Array(await file.arrayBuffer());
            result.push({ path: path, base64: toBase64(bytes) });
        }
        return result;
    }
};

function toBase64(bytes) {
    let binary = "";
    const chunk = 0x8000;
    for (let i = 0; i < bytes.length; i += chunk) {
        binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
    }
    return btoa(binary);
}
