using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Quorum.Integration.Tests.Web;

/// <summary>
/// The viewer and the vendored libraries ship inside the repository and the published output (spec 014, D-15,
/// AC-36, AC-37): the hashes in the vendored note match the files, the download script is committed, and nothing
/// in the viewer reaches an external address.
/// </summary>
public sealed class PreviewAssetTests
{
    private static string PreviewRoot { get; } = Path.Combine(RepoRoot(), "src", "Quorum.Web.Client", "wwwroot", "preview");

    [Theory]
    [InlineData("viewer.css")]
    [InlineData("viewer.js")]
    [InlineData("lib/VENDORED.md")]
    [InlineData("lib/json-formatter-js/json-formatter.umd.js")]
    [InlineData("lib/json-formatter-js/json-formatter.css")]
    [InlineData("lib/json-formatter-js/LICENSE")]
    [InlineData("lib/mermaid/mermaid.min.js")]
    [InlineData("lib/mermaid/LICENSE")]
    public void TheViewerAndTheVendoredLibrariesShipInTheRepository(string relative) =>
        Assert.True(File.Exists(Path.Combine(PreviewRoot, relative)), $"wwwroot/preview/{relative} is missing.");

    [Fact]
    public void TheVendoredNoteNamesVersionLicenceAndScriptAndTheHashesMatchTheFiles()
    {
        var note = File.ReadAllText(Path.Combine(PreviewRoot, "lib", "VENDORED.md"));

        Assert.Contains("json-formatter-js 2.5.23", note, StringComparison.Ordinal);
        Assert.Contains("mermaid 12.3.0", note, StringComparison.Ordinal);
        Assert.Contains("MIT", note, StringComparison.Ordinal);
        Assert.Contains("vendor-preview-libs.ps1", note, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(RepoRoot(), "scripts", "vendor-preview-libs.ps1")),
            "The download script that keeps the vendored files reproducible is not committed.");

        foreach (var (library, file) in new[]
                 {
                     ("json-formatter-js", "json-formatter.umd.js"),
                     ("json-formatter-js", "json-formatter.css"),
                     ("json-formatter-js", "LICENSE"),
                     ("mermaid", "mermaid.min.js"),
                     ("mermaid", "LICENSE"),
                 })
        {
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(
                Path.Combine(PreviewRoot, "lib", library, file)))).ToLowerInvariant();
            Assert.Contains(hash, note, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheHostFetchesTheLibrariesFromWhereTheyAreVendored()
    {
        var host = File.ReadAllText(Path.Combine(PreviewRoot, "..", "preview.js"));

        // AC-17/AC-35: the host fetches each library on the first preview of its kind, from the vendored path.
        Assert.Contains("preview/lib/mermaid/mermaid.min.js", host, StringComparison.Ordinal);
        Assert.Contains("preview/lib/json-formatter-js/json-formatter.umd.js", host, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(PreviewRoot, "lib", "mermaid", "mermaid.min.js")));
    }

    private static readonly string[] AllowedCdns =
    [
        "https://cdnjs.cloudflare.com", "https://cdn.jsdelivr.net", "https://unpkg.com",
        "https://fonts.googleapis.com", "https://fonts.gstatic.com",
    ];

    [Fact]
    public void TheCdnsAHtmlFrameMayLoadFromAreTheFiveWellKnownHttpsHosts()
    {
        var viewer = File.ReadAllText(Path.Combine(PreviewRoot, "viewer.js"));
        var host = File.ReadAllText(Path.Combine(PreviewRoot, "..", "preview.js"));

        // Spec 014 D-18: with the consent to remote resources, styles, fonts and scripts may come from these hosts only.
        foreach (var list in new[] { viewer, host })
        {
            Assert.Contains("https://cdnjs.cloudflare.com https://cdn.jsdelivr.net https://unpkg.com", list, StringComparison.Ordinal);
            Assert.Contains("https://fonts.googleapis.com https://fonts.gstatic.com", list, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NothingInTheViewerReachesAnExternalAddress()
    {
        foreach (var file in new[] { "viewer.css", "viewer.js", Path.Combine("lib", "json-formatter-js", "json-formatter.umd.js") })
        {
            var text = File.ReadAllText(Path.Combine(PreviewRoot, file));
            // Spec 014 D-18: the only external addresses the viewer names are the five CDNs a consented frame may load from.
            foreach (Match address in Regex.Matches(text, "https?://[^\\s\"')]+"))
            {
                Assert.True(AllowedCdns.Any(cdn => address.Value.StartsWith(cdn, StringComparison.Ordinal)),
                    $"{file} names {address.Value}, which is not an allowed CDN.");
            }
        }
    }

    [Fact]
    public void TheViewerKeepsTheFrameFromNavigatingAndSendsLinksToANewTab()
    {
        var viewer = File.ReadAllText(Path.Combine(PreviewRoot, "viewer.js"));

        // Spec 014 D-9/D-10: a refresh or a content base would make the frame navigate by itself; links keep their href
        // and open in a new tab through the base target the viewer adds.
        Assert.Contains("meta[http-equiv=\"refresh\" i], base", viewer, StringComparison.Ordinal);
        Assert.Contains("<base target=\"_blank\">", viewer, StringComparison.Ordinal);
        Assert.DoesNotContain("querySelectorAll(\"a, area\")", viewer, StringComparison.Ordinal);
    }

    [Fact]
    public void TheViewerPolicyClosesNetworkAndNavigationAndTheFrameIsSandboxedWithoutSameOrigin()
    {
        var host = File.ReadAllText(Path.Combine(PreviewRoot, "..", "preview.js"));
        var panel = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Quorum.Web.Client", "Components", "PreviewPanel.razor"));

        // D-7: no connect-src (so no fetch, XHR or WebSocket from any frame) and frame-src about: (no navigation).
        Assert.Contains("default-src 'none'", host, StringComparison.Ordinal);
        Assert.Contains("frame-src about:", host, StringComparison.Ordinal);
        Assert.DoesNotContain("connect-src", host, StringComparison.Ordinal);
        Assert.Contains("sandbox=\"allow-scripts allow-popups allow-popups-to-escape-sandbox\"", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("allow-top-navigation", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("allow-same-origin", panel, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHostPageLoadsTheGlueScriptAndNothingElseForThePreview()
    {
        var index = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Quorum.Web.Client", "wwwroot", "index.html"));

        // AC-35: preview.js binds clicks and creates the iframe only when a preview opens; no viewer or library is
        // requested before that.
        Assert.Contains("<script src=\"preview.js\"></script>", index, StringComparison.Ordinal);
        Assert.DoesNotContain("viewer.html", index, StringComparison.Ordinal);
        Assert.DoesNotContain("json-formatter", index, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Quorum.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (Quorum.slnx) was not found above the test directory.");
    }
}
