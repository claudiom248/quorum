using System.IO.Compression;
using System.Text;
using System.Xml;
using Quorum.Shared.Results;

namespace Quorum.Modules.Conversations.Extraction;

/// <summary>
/// Reads the text of a DOCX as Markdown (headings, lists, tables). The file is untrusted, so nothing is extracted to disk or
/// executed and only <c>word/document.xml</c> is read:
/// entries are read through a stream that counts the bytes actually produced (declared sizes are not trusted), the package is
/// capped in parts and in decompressed size, XML is read with DTDs prohibited and no resolver, relationships (links, remote
/// templates) are never opened, and packages carrying macros or embedded objects are refused.
/// </summary>
internal static class DocxText
{
    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string MainDocument = "word/document.xml";

    private const int MaxParts = 1000;
    private const long MaxDocumentBytes = 25L * 1024 * 1024;
    private const long MaxDeclaredPackageBytes = 100L * 1024 * 1024;

    // Real XML compresses 10 to 20 times; bombs go beyond a thousand.
    private const long MaxCompressionRatio = 200;
    private const long RatioCheckedAbove = 1024 * 1024;

    private static readonly Error Unreadable = Error.Unprocessable(
        DocumentTextExtractor.UnreadableCode,
        "The document could not be read: it is damaged, password-protected, or contains macros or embedded objects.");

    private static readonly Error NoText = Error.Unprocessable(DocumentTextExtractor.NoTextCode, "The document has no text to use.");

    public static Result<string> Extract(byte[] content, CancellationToken cancellationToken)
    {
        try
        {
            using var package = new ZipArchive(new MemoryStream(content, writable: false), ZipArchiveMode.Read);

            if (!IsAcceptable(package) || package.GetEntry(MainDocument) is not { } main)
            {
                return Unreadable;
            }

            var xml = ReadBounded(main);
            if (xml is null)
            {
                return Unreadable;
            }

            var text = ToMarkdown(xml, cancellationToken);
            return string.IsNullOrWhiteSpace(text) ? NoText : text;
        }
        catch (Exception exception) when (exception is InvalidDataException or XmlException or IOException or NotSupportedException or ArgumentException)
        {
            // Not a zip, truncated, an XML error, or a DTD (prohibited): all unreadable, whatever the cause.
            return Unreadable;
        }
    }

    private static bool IsAcceptable(ZipArchive package)
    {
        if (package.Entries.Count > MaxParts)
        {
            return false;
        }

        long declared = 0;
        foreach (var entry in package.Entries)
        {
            declared += entry.Length;
            var name = entry.FullName;

            if (declared > MaxDeclaredPackageBytes
                || name.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("word/embeddings/", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("word/activeX/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (entry.Length > RatioCheckedAbove && entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > MaxCompressionRatio)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The entry's bytes, or <see langword="null"/> when it produces more than the limit, whatever it declared.</summary>
    private static byte[]? ReadBounded(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;

        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > MaxDocumentBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static string ToMarkdown(byte[] xml, CancellationToken cancellationToken)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxDocumentBytes,
        };

        using var reader = XmlReader.Create(new MemoryStream(xml, writable: false), settings);
        var output = new DocumentWriter();
        var paragraph = new StringBuilder();
        string? style = null;
        var listLevel = -1;
        var inNumbering = false;
        var inText = false;

        var tableDepth = 0;
        List<List<string>> rows = [];
        List<string> row = [];
        var cell = new StringBuilder();

        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element when reader.NamespaceURI == WordNamespace:
                    switch (reader.LocalName)
                    {
                        case "tbl":
                            if (++tableDepth == 1)
                            {
                                rows = [];
                            }

                            break;
                        case "tr" when tableDepth == 1:
                            row = [];
                            break;
                        case "tc" when tableDepth == 1:
                            cell.Clear();
                            break;
                        case "p":
                            paragraph.Clear();
                            style = null;
                            listLevel = -1;
                            break;
                        case "pStyle":
                            style = reader.GetAttribute("val", WordNamespace);
                            break;
                        case "numPr":
                            inNumbering = true;
                            listLevel = 0;
                            break;
                        case "ilvl" when inNumbering && int.TryParse(reader.GetAttribute("val", WordNamespace), out var level):
                            listLevel = Math.Clamp(level, 0, 8);
                            break;
                        case "t":
                            inText = !reader.IsEmptyElement;
                            break;
                        case "tab":
                        case "br":
                        case "cr":
                            paragraph.Append(' ');
                            break;
                    }

                    break;

                case XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace when inText:
                    paragraph.Append(reader.Value);
                    break;

                case XmlNodeType.EndElement when reader.NamespaceURI == WordNamespace:
                    switch (reader.LocalName)
                    {
                        case "t":
                            inText = false;
                            break;
                        case "numPr":
                            inNumbering = false;
                            break;
                        case "p":
                            var text = Collapse(paragraph);
                            if (text.Length == 0)
                            {
                                break;
                            }

                            if (tableDepth > 0)
                            {
                                cell.Append(cell.Length == 0 ? string.Empty : " ").Append(text);
                            }
                            else
                            {
                                output.Paragraph(text, style, listLevel);
                            }

                            cancellationToken.ThrowIfCancellationRequested();
                            break;
                        case "tc" when tableDepth == 1:
                            row.Add(cell.ToString());
                            break;
                        case "tr" when tableDepth == 1:
                            rows.Add(row);
                            break;
                        case "tbl":
                            if (--tableDepth == 0)
                            {
                                output.Block(CsvTable.Table(rows), isListItem: false);
                            }

                            break;
                    }

                    break;
            }
        }

        return output.ToString();
    }

    /// <summary>Whitespace runs as one space, so a paragraph is one line.</summary>
    private static string Collapse(StringBuilder text)
    {
        var collapsed = new StringBuilder(text.Length);
        var previousWasSpace = true;
        foreach (var character in text.ToString())
        {
            var space = char.IsWhiteSpace(character);
            if (!space || !previousWasSpace)
            {
                collapsed.Append(space ? ' ' : character);
            }

            previousWasSpace = space;
        }

        return collapsed.ToString().TrimEnd();
    }

    /// <summary>Joins blocks with blank lines, keeping the items of a list on consecutive lines.</summary>
    private sealed class DocumentWriter
    {
        private readonly StringBuilder _output = new();
        private bool _lastWasListItem;

        public void Paragraph(string text, string? style, int listLevel)
        {
            if (listLevel >= 0)
            {
                Block($"{new string(' ', listLevel * 2)}- {text}", isListItem: true);
                return;
            }

            Block(Heading(style) is var level and > 0 ? $"{new string('#', level)} {text}" : text, isListItem: false);
        }

        public void Block(string markdown, bool isListItem)
        {
            if (markdown.Length == 0)
            {
                return;
            }

            if (_output.Length > 0)
            {
                _output.Append(isListItem && _lastWasListItem ? "\n" : "\n\n");
            }

            _output.Append(markdown);
            _lastWasListItem = isListItem;
        }

        public override string ToString() => _output.ToString();

        /// <summary>1 to 6 for "Heading1".."Heading9" and "Title" styles, 0 for anything else.</summary>
        private static int Heading(string? style)
        {
            if (style is null)
            {
                return 0;
            }

            if (style.Equals("Title", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            var digits = style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) ? style["Heading".Length..].Trim() : string.Empty;
            return int.TryParse(digits, out var level) ? Math.Clamp(level, 1, 6) : 0;
        }
    }
}
