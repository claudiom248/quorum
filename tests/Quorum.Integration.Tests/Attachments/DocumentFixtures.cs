using System.IO.Compression;
using System.Text;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Quorum.Integration.Tests.Attachments;

/// <summary>Small PDF and DOCX files built in memory, so tests do not depend on binary fixtures.</summary>
internal static class DocumentFixtures
{
    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>A PDF with one page per text; an empty text makes a page without a text layer (as a scan would be).</summary>
    public static byte[] Pdf(params string[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages)
        {
            var page = builder.AddPage(595, 842);
            if (text.Length > 0)
            {
                page.AddText(text, 12, new PdfPoint(40, 780), font);
            }
        }

        return builder.Build();
    }

    /// <summary>A PDF that declares the standard security handler: it cannot be read without the owner's password.</summary>
    public static byte[] EncryptedPdf()
    {
        const string objects =
            "%PDF-1.4\n" +
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n" +
            "4 0 obj\n<< /Filter /Standard /V 1 /R 2 /P -4 " +
            "/O (01234567890123456789012345678901) /U (01234567890123456789012345678901) >>\nendobj\n";
        return Encoding.Latin1.GetBytes(
            objects + "trailer\n<< /Root 1 0 R /Size 5 /Encrypt 4 0 R /ID [<00112233445566778899aabbccddeeff> <00112233445566778899aabbccddeeff>] >>\n%%EOF\n");
    }

    /// <summary>A DOCX whose body is the given WordprocessingML, plus any extra package parts.</summary>
    public static byte[] Docx(string bodyXml, IReadOnlyDictionary<string, byte[]>? extraParts = null) =>
        Package(
            $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"{WordNamespace}\"><w:body>{bodyXml}</w:body></w:document>",
            extraParts);

    /// <summary>A DOCX package around an arbitrary <c>word/document.xml</c>.</summary>
    public static byte[] Package(string documentXml, IReadOnlyDictionary<string, byte[]>? extraParts = null)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", Encoding.UTF8.GetBytes(
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>"));
            Add(zip, "_rels/.rels", Encoding.UTF8.GetBytes(
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>"));
            Add(zip, "word/document.xml", Encoding.UTF8.GetBytes(documentXml));

            foreach (var (name, content) in extraParts ?? new Dictionary<string, byte[]>())
            {
                Add(zip, name, content);
            }
        }

        return output.ToArray();
    }

    public static string Paragraph(string text, string? style = null, int? listLevel = null)
    {
        var properties = string.Empty;
        if (style is not null || listLevel is not null)
        {
            properties = "<w:pPr>" +
                (style is null ? string.Empty : $"<w:pStyle w:val=\"{style}\"/>") +
                (listLevel is null ? string.Empty : $"<w:numPr><w:ilvl w:val=\"{listLevel}\"/><w:numId w:val=\"1\"/></w:numPr>") +
                "</w:pPr>";
        }

        var escaped = global::System.Security.SecurityElement.Escape(text);
        return $"<w:p>{properties}<w:r><w:t xml:space=\"preserve\">{escaped}</w:t></w:r></w:p>";
    }

    public static string Table(params string[][] rows) =>
        "<w:tbl>" +
        string.Concat(rows.Select(row => "<w:tr>" + string.Concat(row.Select(cell => $"<w:tc>{Paragraph(cell)}</w:tc>")) + "</w:tr>")) +
        "</w:tbl>";

    private static void Add(ZipArchive zip, string name, byte[] content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content);
    }
}
