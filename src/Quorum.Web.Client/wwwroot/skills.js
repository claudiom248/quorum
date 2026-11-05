// Reads the files picked in an <input type="file"> for the skill import (spec 017 D-19). A folder picked with webkitdirectory keeps
// each file's path inside the folder, which the server needs to find SKILL.md files; the bytes travel as base64.
window.quorumSkills = {
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
