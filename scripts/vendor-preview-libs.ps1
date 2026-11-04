# Downloads the preview panel's vendored libraries from pinned URLs and rewrites the version note
# (spec 014, D-15). Rerun after changing a version; the SHA-256 of every file is recorded so an
# accidental or malicious edit of a vendored file is visible in the note.
$ErrorActionPreference = "Stop"

$root = [IO.Path]::GetFullPath([IO.Path]::Combine($PSScriptRoot, "..", "src", "Quorum.Web.Client", "wwwroot", "preview", "lib"))

$libraries = @(
    @{
        Name    = "json-formatter-js"
        Version = "2.5.23"
        License = "MIT"
        Files   = @(
            @{ Url = "https://cdn.jsdelivr.net/npm/json-formatter-js@2.5.23/dist/json-formatter.umd.js"; Name = "json-formatter.umd.js" },
            @{ Url = "https://cdn.jsdelivr.net/npm/json-formatter-js@2.5.23/dist/json-formatter.css";   Name = "json-formatter.css" },
            @{ Url = "https://cdn.jsdelivr.net/npm/json-formatter-js@2.5.23/LICENSE";                   Name = "LICENSE" }
        )
    }
)

$note = [System.Text.StringBuilder]::new()
[void]$note.AppendLine("# Vendored preview libraries")
[void]$note.AppendLine("")
[void]$note.AppendLine("Downloaded by ``scripts/vendor-preview-libs.ps1`` from the pinned URLs below (spec 014, D-15).")
[void]$note.AppendLine("Nothing is fetched from a CDN at runtime; the viewer only loads these files from the app's own origin.")
[void]$note.AppendLine("")

foreach ($library in $libraries) {
    $directory = Join-Path $root $library.Name
    New-Item -ItemType Directory -Force -Path $directory | Out-Null

    [void]$note.AppendLine("## $($library.Name) $($library.Version) ($($library.License))")
    [void]$note.AppendLine("")

    foreach ($file in $library.Files) {
        $destination = Join-Path $directory $file.Name
        Invoke-WebRequest -Uri $file.Url -OutFile $destination -UseBasicParsing
        $hash = (Get-FileHash -Algorithm SHA256 -Path $destination).Hash.ToLowerInvariant()
        [void]$note.AppendLine("- ``$($library.Name)/$($file.Name)``")
        [void]$note.AppendLine("  - source: ``$($file.Url)``")
        [void]$note.AppendLine("  - sha256: ``$hash``")
    }

    [void]$note.AppendLine("")
}

Set-Content -Path (Join-Path $root "VENDORED.md") -Value $note.ToString() -NoNewline -Encoding utf8
Write-Host "Vendored $($libraries.Count) librar(y/ies); note written to $root/VENDORED.md"
