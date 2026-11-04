using Quorum.Web.Client;

namespace Quorum.Integration.Tests.Web;

/// <summary>
/// Which fenced blocks get a Preview button and how the block is wrapped (spec 014, AC-1 to AC-5): only the info
/// string decides, the block's text stays in <c>pre code</c> and never in an attribute, and a block that is still
/// streaming has no button until its closing fence arrives.
/// </summary>
public sealed class PreviewMarkdownTests
{
    [Theory]
    [InlineData("html", "html", "HTML")]
    [InlineData("htm", "html", "HTML")]
    [InlineData("svg", "svg", "SVG")]
    [InlineData("json", "json", "JSON")]
    [InlineData("csv", "csv", "CSV")]
    [InlineData("mermaid", "mermaid", "Mermaid")]
    [InlineData("mmd", "mermaid", "Mermaid")]
    public void EverySupportedTagGetsAPreviewButton(string tag, string dataValue, string label)
    {
        var html = MarkdownRenderer.ToHtml($"```{tag}\ncontent\n```");

        Assert.Contains($"data-preview-kind=\"{dataValue}\"", html, StringComparison.Ordinal);
        Assert.Contains($"aria-label=\"Preview this {label} block\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"preview-open\"", html, StringComparison.Ordinal);
        Assert.Contains("content", html, StringComparison.Ordinal);
        Assert.Contains("<pre><code", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTagIsRecognisedWhateverItsCaseAndWhateverFollowsIt()
    {
        Assert.Contains("preview-open", MarkdownRenderer.ToHtml("```JSON\n{}\n```"), StringComparison.Ordinal);
        Assert.Contains("preview-open", MarkdownRenderer.ToHtml("```html title=\"page.html\"\n<p>x</p>\n```"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("xml")]
    [InlineData("text")]
    [InlineData("javascript")]
    [InlineData("json5")]
    [InlineData("htmlx")]
    public void UnsupportedTagsGetNoButton(string tag) =>
        Assert.DoesNotContain("preview-open", MarkdownRenderer.ToHtml($"```{tag}\ncontent\n```"), StringComparison.Ordinal);

    [Fact]
    public void AnUntaggedBlockGetsNoButtonEvenWhenItsContentIsPreviewable()
    {
        var html = MarkdownRenderer.ToHtml("```\n{\"valid\": \"json\"}\n```");

        Assert.DoesNotContain("preview-open", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-preview-kind", html, StringComparison.Ordinal);
    }

    [Fact]
    public void WhileStreamingTheBlockStillGrowingHasNoButtonUntilTheFenceArrives()
    {
        var partial = "```html\n<p>not finished";

        Assert.DoesNotContain("preview-open", MarkdownRenderer.ToHtml(partial, streaming: true), StringComparison.Ordinal);

        // The same text, once the answer is complete, is a closed block as far as markdown goes.
        Assert.Contains("preview-open", MarkdownRenderer.ToHtml(partial, streaming: false), StringComparison.Ordinal);
        Assert.Contains("preview-open", MarkdownRenderer.ToHtml(partial + "\n```", streaming: true), StringComparison.Ordinal);
    }

    [Fact]
    public void WhileStreamingTheBlocksAlreadyClosedKeepTheirButtons()
    {
        var html = MarkdownRenderer.ToHtml("```json\n{\"a\":1}\n```\ntext\n```html\n<p>still open", streaming: true);

        Assert.Equal(1, Count(html, "preview-open"));
        Assert.Contains("data-preview-kind=\"json\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RawHtmlStaysEscapedAndTheToolbarIsTheOnlyAddedMarkup()
    {
        var html = MarkdownRenderer.ToHtml(
            "<script>alert(1)</script>\n\n```html\n<img src=x onerror=alert(1)>\n```");

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img src=x", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        // Inside the block the text is code: escaped, not an element.
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html, StringComparison.Ordinal);
        Assert.Contains("<div class=\"code-block\"><div class=\"code-toolbar\">", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TheButtonCarriesTheKindOnlyNeverTheContent()
    {
        var html = MarkdownRenderer.ToHtml("```json\n{\"secret\":\"value\"}\n```");

        var button = html[html.IndexOf("<button", StringComparison.Ordinal)..];
        button = button[..button.IndexOf('>')];
        Assert.DoesNotContain("secret", button, StringComparison.Ordinal);
    }

    private static int Count(string text, string needle) => text.Split(needle).Length - 1;
}

/// <summary>The source scan that tells an unfinished fenced block from a closed one (spec 014, D-2).</summary>
public sealed class FenceScannerTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData("plain text", false)]
    [InlineData("```html\n<p>\n```", false)]
    [InlineData("```html\n<p>", true)]
    [InlineData("```html\n<p>\n```\nmore\n```json\n{", true)]
    [InlineData("````html\n```\nstill inside\n````", false)]
    [InlineData("~~~\nx\n~~~", false)]
    [InlineData("~~~\nx", true)]
    [InlineData("   ```\nx\n   ```", false)]
    [InlineData("    ```\nx", false)]
    [InlineData("```\nx\n```   ", false)]
    [InlineData("```\nx\n~~~", true)]
    [InlineData("```html\r\n<p>\r\n```\r\n", false)]
    [InlineData("```html\r\n<p>", true)]
    [InlineData("``\nx", false)]
    [InlineData("text ``` not a fence", false)]
    public void LastFenceIsOpenDetectsTheBlockStillGrowing(string markdown, bool expected) =>
        Assert.Equal(expected, FenceScanner.LastFenceIsOpen(markdown));
}

/// <summary>The info strings, labels, extensions and data values of the previewable kinds (spec 014, D-1, D-4).</summary>
public sealed class PreviewKindsTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("html", PreviewKind.Html)]
    [InlineData("HTML", PreviewKind.Html)]
    [InlineData("htm", PreviewKind.Html)]
    [InlineData("svg", PreviewKind.Svg)]
    [InlineData("json", PreviewKind.Json)]
    [InlineData("csv", PreviewKind.Csv)]
    [InlineData("mermaid", PreviewKind.Mermaid)]
    [InlineData("mmd", PreviewKind.Mermaid)]
    [InlineData("xml", null)]
    [InlineData("html title=\"x\"", PreviewKind.Html)]
    [InlineData(" json ", PreviewKind.Json)]
    public void FromInfoStringRecognisesOnlyTheSupportedTags(string? info, PreviewKind? expected) =>
        Assert.Equal(expected, PreviewKinds.FromInfoString(info));

    [Fact]
    public void MermaidIsNamedMermaidButDownloadsAsMmd()
    {
        Assert.Equal("Mermaid", PreviewKinds.Label(PreviewKind.Mermaid));
        Assert.Equal("mmd", PreviewKinds.Extension(PreviewKind.Mermaid));
        Assert.Equal("mermaid", PreviewKinds.DataValue(PreviewKind.Mermaid));
    }

    [Fact]
    public void ParseReadsBackWhatDataValueWrites()
    {
        foreach (var kind in Enum.GetValues<PreviewKind>())
        {
            Assert.Equal(kind, PreviewKinds.Parse(PreviewKinds.DataValue(kind)));
        }

        Assert.Null(PreviewKinds.Parse("nope"));
        Assert.Null(PreviewKinds.Parse(null));
    }
}
