
using System.Buffers;
using Quorum.Shared.Results;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Exceptions;

namespace Quorum.Modules.Conversations.Extraction;

/// <summary>
/// Reads the text layer of a PDF, page by page. The parser is managed code: it does not run scripts, open attachments or
/// fetch anything. A PDF without text (a scan) is rejected: OCR is out of scope.
/// </summary>
internal static class PdfText
{
    private static readonly Error Unreadable = Error.Unprocessable(
        DocumentTextExtractor.UnreadableCode,
        "The PDF could not be read: it is damaged or protected with a password.");

    private static readonly Error NoText = Error.Unprocessable(
        DocumentTextExtractor.NoTextCode,
        "The PDF has no text layer (it looks scanned), so there is no text to use.");

    public static Result<string> Extract(byte[] content, CancellationToken cancellationToken)
    {
        try
        {
            using var document = PdfDocument.Open(content, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
            var pages = new List<string>();

            foreach (var page in document.GetPages())
            {
                // The parser itself cannot be interrupted: the limit is checked between pages.
                cancellationToken.ThrowIfCancellationRequested();
                var text = NormalizeLigatures(ReadingOrderText(page)).Trim();
                if (text.Length > 0)
                {
                    pages.Add(text);
                }
            }

            return pages.Count == 0 ? NoText : string.Join("\n\n", pages);
        }
        catch (Exception exception) when (IsMalformedInput(exception))
        {
            return Unreadable;
        }
    }

    /// <summary>
    /// Replaces the ligature glyphs that PDF generators store as single characters (U+FB00 to U+FB06) with their letters, so
    /// that "ﬁsh" is read, searched and quoted as "fish".
    /// </summary>
    internal static string NormalizeLigatures(string text) =>
        text.AsSpan().IndexOfAny(LigatureCharacters) < 0
            ? text
            : text
                .Replace("ﬀ", "ff", StringComparison.Ordinal)
                .Replace("ﬁ", "fi", StringComparison.Ordinal)
                .Replace("ﬂ", "fl", StringComparison.Ordinal)
                .Replace("ﬃ", "ffi", StringComparison.Ordinal)
                .Replace("ﬄ", "ffl", StringComparison.Ordinal)
                .Replace("ﬅ", "st", StringComparison.Ordinal)
                .Replace("ﬆ", "st", StringComparison.Ordinal);

    private static readonly SearchValues<char> LigatureCharacters =
        SearchValues.Create(['ﬀ', 'ﬁ', 'ﬂ', 'ﬃ', 'ﬄ', 'ﬅ', 'ﬆ']);

    /// <summary>
    /// The page's text in reading order: words are grouped into blocks (columns, paragraphs) and the blocks are ordered, so a
    /// two-column page reads one column after the other instead of line by line across both.
    /// </summary>
    private static string ReadingOrderText(Page page)
    {
        var words = page.GetWords(NearestNeighbourWordExtractor.Instance);
        var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
        return string.Join("\n\n", UnsupervisedReadingOrderDetector.Instance.Get(blocks).Select(block => block.Text));
    }

    /// <summary>
    /// What a parser of untrusted bytes throws for damaged, truncated or encrypted input; cancellation and anything that
    /// signals a bug in Quorum are not among them.
    /// </summary>
    private static bool IsMalformedInput(Exception exception) =>
        exception is PdfDocumentFormatException
            or PdfDocumentEncryptedException
            or InvalidDataException
            or InvalidOperationException
            or ArgumentException
            or IndexOutOfRangeException
            or FormatException
            or NotSupportedException
            or OverflowException
            or KeyNotFoundException;
}
