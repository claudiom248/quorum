using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Quorum.Web.Client;

/// <summary>
/// Renders the assistant's markdown to HTML. The text comes from a model and may be steered by documents or web pages
/// it read, so it is treated as untrusted: raw HTML is off, only a fixed set of extensions is on (never generic
/// attributes, which would allow event handlers), only http(s) links are kept and images are shown as links so that
/// the browser never fetches an address chosen by the model.
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

    public static string ToHtml(string markdown)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        foreach (var link in document.Descendants<LinkInline>().Where(link => link.IsImage).ToList())
        {
            link.IsImage = false;
        }

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer) { LinkRewriter = RestrictToHttp };
        Pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();
        return writer.ToString();
    }

    private static string RestrictToHttp(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? url
            : "#";
}
