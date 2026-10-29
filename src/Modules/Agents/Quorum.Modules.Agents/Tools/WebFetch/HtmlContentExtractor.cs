using System.Text;
using AngleSharp.Html.Parser;
using Quorum.Shared.Results;
using SmartReader;

namespace Quorum.Modules.Agents.Tools.WebFetch;

/// <summary>What a page says once the noise is out: its title and its main text.</summary>
/// <param name="Title">The page's title, as the parser read it.</param>
/// <param name="Text">The main text of the page, without the navigation, the ads and the footer.</param>
internal sealed record ExtractedContent(string Title, string Text);

/// <summary>
/// The main text of one HTML page, read by Mozilla's Readability (spec 006, AC-3): the same algorithm the browser extensions
/// use to switch a site into a plain article. A page the algorithm cannot make sense of (a listing, a shell) falls back to
/// the plain text of the body, so a model can still see what the page holds even when it is not an article.
/// </summary>
internal static class HtmlContentExtractor
{
    /// <summary>How long the fallback text may be: a page that is only navigation would give the model nothing useful.</summary>
    private const int FallbackLimitCharacters = 20_000;

    private static readonly Error Empty = Error.Unprocessable("web_fetch.no_content", "The page has no readable text.");

    /// <param name="html">The whole HTML of the page.</param>
    /// <param name="url">The page's own address: SmartReader uses it to resolve relative links.</param>
    public static Result<ExtractedContent> Extract(string html, string url)
    {
        ArgumentException.ThrowIfNullOrEmpty(html);
        ArgumentException.ThrowIfNullOrEmpty(url);

        try
        {
            var article = Reader.ParseArticle(url, html);
            if (article is { IsReadable: true })
            {
                var text = Clean(article.TextContent);
                if (text.Length > 0)
                {
                    return new ExtractedContent(Title(article.Title, url), text);
                }
            }
        }
        catch
        {
            // A malformed page is not a reason to fail: the fallback below still reads what AngleSharp understood.
        }

        return Fallback(html, url);
    }

    /// <summary>What the page says when there is no article in it: the plain text of its body, or of its whole document.</summary>
    private static Result<ExtractedContent> Fallback(string html, string url)
    {
        var document = new HtmlParser().ParseDocument(html);
        var title = Clean(document.QuerySelector("title")?.TextContent ?? string.Empty);
        var body = document.Body ?? document.DocumentElement;
        var text = Clean(body?.TextContent ?? string.Empty);
        if (text.Length == 0)
        {
            return Empty;
        }

        if (text.Length > FallbackLimitCharacters)
        {
            text = text[..FallbackLimitCharacters];
        }

        return new ExtractedContent(Title(title, url), text);
    }

    /// <summary>Whitespace the HTML leaves behind squeezed out: runs of blank collapse, leading and trailing blank removed.</summary>
    private static string Clean(string raw)
    {
        var builder = new StringBuilder(raw.Length);
        var previousBlank = true;
        foreach (var character in raw)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousBlank && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                previousBlank = true;
            }
            else
            {
                builder.Append(character);
                previousBlank = false;
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>The title the page offers, or the address itself when it offers nothing.</summary>
    private static string Title(string title, string url) =>
        string.IsNullOrWhiteSpace(title) ? Shorten(url) : title;

    private static string Shorten(string url) => url.Length <= 200 ? url : url[..200];
}
