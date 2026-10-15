using System.IO.Compression;
using System.Text;
using System.Xml;
using Quorum.Shared.Results;

namespace Quorum.Modules.Conversations.Extraction;

/// <summary>
/// Reads the text of a DOCX as Markdown (headings, lists, tables). The file is untrusted, so nothing is extracted to disk or
/// executed and only the main document part (and the package's own <c>_rels/.rels</c> that names it) is read:
/// entries are read through a stream that counts the bytes actually produced (declared sizes are not trusted), the package is
/// capped in parts and in decompressed size, XML is read with DTDs prohibited and no resolver, relationships (links, remote
/// templates) are never opened, and packages carrying macros or embedded objects are refused.
/// </summary>
internal static class DocxText
{
    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string MarkupCompatibilityNamespace = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private const string DefaultMainDocument = "word/document.xml";
    private const string PackageRelationships = "_rels/.rels";
    private const string OfficeDocumentRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";

    private const int MaxParts = 1000;
    private const long MaxDocumentBytes = 25L * 1024 * 1024;
    private const long MaxRelationshipsBytes = 1024 * 1024;
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

            if (!IsAcceptable(package) || MainDocument(package) is not { } main)
            {
                return Unreadable;
            }

            using var xml = ReadBounded(main, MaxDocumentBytes);
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

    /// <summary>
    /// The main document part: the target of the package's <c>officeDocument</c> relationship, or the conventional path when
    /// the package does not say. Part names are case-insensitive; a name that two parts share is ambiguous and refused.
    /// </summary>
    private static ZipArchiveEntry? MainDocument(ZipArchive package)
    {
        var path = RelationshipTarget(package) ?? DefaultMainDocument;
        var matches = package.Entries.Where(e => string.Equals(e.FullName, path, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static string? RelationshipTarget(ZipArchive package)
    {
        var relationships = package.Entries
            .Where(e => string.Equals(e.FullName, PackageRelationships, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        if (relationships.Count != 1 || ReadBounded(relationships[0], MaxRelationshipsBytes) is not { } stream)
        {
            return null;
        }

        using (stream)
        using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
        {
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element
                    && reader.NamespaceURI == RelationshipsNamespace
                    && reader.LocalName == "Relationship"
                    && reader.GetAttribute("Type") == OfficeDocumentRelationship
                    && !string.Equals(reader.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)
                    && reader.GetAttribute("Target") is { Length: > 0 } target)
                {
                    return target.TrimStart('/');
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The entry's bytes in a stream positioned at the start, or <see langword="null"/> when it produces more than the
    /// limit, whatever it declared.
    /// </summary>
    private static MemoryStream? ReadBounded(ZipArchiveEntry entry, long limit)
    {
        using var stream = entry.Open();
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;

        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                buffer.Dispose();
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        return buffer;
    }

    private static string ToMarkdown(Stream xml, CancellationToken cancellationToken)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxDocumentBytes,
        };

        using var reader = XmlReader.Create(xml, settings);
        var output = new DocumentWriter();

        // A paragraph can hold others (text boxes, shapes): each open paragraph keeps its own text.
        var open = new Stack<OpenParagraph>();
        var inNumbering = false;
        var inText = false;

        var tableDepth = 0;
        List<List<string>> rows = [];
        List<string> row = [];
        var cell = new StringBuilder();

        void Emit(string text, string? style, int listLevel)
        {
            if (tableDepth > 0)
            {
                cell.Append(cell.Length == 0 ? string.Empty : " ").Append(text);
            }
            else
            {
                output.Paragraph(text, style, listLevel);
            }
        }

        var more = reader.Read();
        while (more)
        {
            var skipped = false;

            switch (reader.NodeType)
            {
                // The Fallback of an AlternateContent repeats what its Choice already says.
                case XmlNodeType.Element when reader.NamespaceURI == MarkupCompatibilityNamespace && reader.LocalName == "Fallback":
                    reader.Skip();
                    skipped = true;
                    break;

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
                            open.Push(new OpenParagraph());
                            break;
                        case "pStyle" when open.Count > 0:
                            open.Peek().Style = reader.GetAttribute("val", WordNamespace);
                            break;
                        case "numPr" when open.Count > 0:
                            inNumbering = true;
                            open.Peek().ListLevel = 0;
                            break;
                        case "ilvl" when inNumbering && open.Count > 0 && int.TryParse(reader.GetAttribute("val", WordNamespace), out var level):
                            open.Peek().ListLevel = Math.Clamp(level, 0, 8);
                            break;
                        case "t":
                            inText = !reader.IsEmptyElement;
                            break;
                        case "tab" or "br" or "cr" when open.Count > 0:
                            open.Peek().Text.Append(' ');
                            break;
                    }

                    break;

                case XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace when inText && open.Count > 0:
                    open.Peek().Text.Append(reader.Value);
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
                        case "p" when open.Count > 0:
                            var finished = open.Pop();
                            var text = Collapse(finished.Text);

                            if (open.Count > 0)
                            {
                                // Inside another paragraph: it follows the outer paragraph's own text as separate blocks.
                                var outer = open.Peek();
                                if (text.Length > 0)
                                {
                                    outer.Nested.Add(new NestedText(text, finished.Style, finished.ListLevel));
                                }

                                outer.Nested.AddRange(finished.Nested);
                                break;
                            }

                            if (text.Length > 0)
                            {
                                Emit(text, finished.Style, finished.ListLevel);
                            }

                            foreach (var nested in finished.Nested)
                            {
                                Emit(nested.Text, nested.Style, nested.ListLevel);
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

            more = skipped ? !reader.EOF : reader.Read();
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

    private sealed class OpenParagraph
    {
        public StringBuilder Text { get; } = new();

        public string? Style { get; set; }

        public int ListLevel { get; set; } = -1;

        public List<NestedText> Nested { get; } = [];
    }

    private sealed record NestedText(string Text, string? Style, int ListLevel);

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
