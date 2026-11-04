using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace Quorum.Web.Client;

/// <summary>
/// Draws fenced code blocks like Markdig does, and adds the Preview button of spec 014 (D-23) around the ones whose
/// info string names a previewable kind. The block's text stays where it always was, inside <c>pre code</c>: the
/// button carries only the kind, and the page's delegated click handler reads the text from the DOM node.
/// </summary>
internal sealed class PreviewCodeBlockRenderer(FencedCodeBlock? suppressed) : HtmlObjectRenderer<CodeBlock>
{
    private static readonly CodeBlockRenderer Fallback = new();

    protected override void Write(HtmlRenderer renderer, CodeBlock obj)
    {
        var kind = obj is FencedCodeBlock fenced ? PreviewKinds.FromInfoString(fenced.Info) : null;

        // While the answer streams, the block whose closing fence has not arrived yet has no button (D-2).
        if (kind is not { } preview || ReferenceEquals(obj, suppressed))
        {
            Fallback.Write(renderer, obj);
            return;
        }

        var label = PreviewKinds.Label(preview);
        renderer.Write("<div class=\"code-block\"><div class=\"code-toolbar\">");
        renderer.Write(
            $"<button type=\"button\" class=\"preview-open\" data-preview-kind=\"{PreviewKinds.DataValue(preview)}\" " +
            $"aria-label=\"Preview this {label} block\" title=\"Preview\">Preview</button>");
        renderer.Write("</div>");
        Fallback.Write(renderer, obj);
        renderer.Write("</div>");
    }
}
