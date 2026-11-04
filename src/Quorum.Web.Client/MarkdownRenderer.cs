using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Quorum.Web.Client;

/// <summary>
/// Renders the assistant's markdown to HTML. The text comes from a model and may be steered by documents or web pages
/// it read, so it is treated as untrusted: raw HTML is off, only a fixed set of extensions is on (never generic
/// attributes, which would allow event handlers), only http(s) links are kept and images are shown as links so that
/// the browser never fetches an address chosen by the model.
/// Fenced code blocks whose tag names a previewable kind (spec 014, D-1) carry a Preview button in a toolbar; the
/// block's text is never put in an attribute, the click handler reads it from the DOM (D-23).
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseTaskLists()
        .UseAutoLinks()
        .UseSoftlineBreakAsHardlineBreak()
        .DisableHtml()
        .Build();

    public static string ToHtml(string markdown) => ToHtml(markdown, streaming: false);

    /// <param name="streaming">
    /// The text is an answer still arriving: the fenced block whose closing fence has not landed yet gets no Preview
    /// button (D-2), and shows it as soon as the fence arrives.
    /// </param>
    public static string ToHtml(string markdown, bool streaming)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        foreach (var link in document.Descendants<LinkInline>().Where(link => link.IsImage).ToList())
        {
            link.IsImage = false;
        }

        var suppressed = streaming && FenceScanner.LastFenceIsOpen(markdown)
            ? document.Descendants<FencedCodeBlock>().LastOrDefault()
            : null;

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer) { LinkRewriter = RestrictToHttp };
        Pipeline.Setup(renderer);
        renderer.ObjectRenderers.Replace<CodeBlockRenderer>(new PreviewCodeBlockRenderer(suppressed));
        renderer.Render(document);
        writer.Flush();
        return writer.ToString();
    }

    private static string RestrictToHttp(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? url
            : "#";
}
