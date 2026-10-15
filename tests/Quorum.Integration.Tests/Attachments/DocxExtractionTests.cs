using System.Text;
using Quorum.Modules.Conversations.Extraction;
using static Quorum.Integration.Tests.Attachments.DocumentFixtures;

namespace Quorum.Integration.Tests.Attachments;

/// <summary>DOCX and PDF extraction, including what a hostile file must not be able to do (spec 002, AC-5, AC-6, AC-10).</summary>
public sealed class DocxExtractionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string WrapBody(string body) =>
        "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" + body + "</w:body></w:document>";

    private static Quorum.Shared.Results.Result<ExtractedDocument> Extract(string name, byte[] content) =>
        DocumentTextExtractor.Extract(name, content, Ct);

    [Fact] // AC-10
    public void HeadingsListsAndTablesBecomeMarkdown()
    {
        var body =
            Paragraph("Service contract", style: "Heading1") +
            Paragraph("Parties", style: "Heading2") +
            Paragraph("This agreement is between two companies.") +
            Paragraph("First obligation", listLevel: 0) +
            Paragraph("Detail of the obligation", listLevel: 1) +
            Paragraph("Second obligation", listLevel: 0) +
            Table(["Item", "Price"], ["Setup", "100"], ["Support | monthly", "20"]) +
            Paragraph("Closing remarks.");

        var result = Extract("contract.docx", Docx(body));

        Assert.True(result.IsSuccess);
        Assert.Equal(
            "# Service contract\n\n## Parties\n\nThis agreement is between two companies.\n\n" +
            "- First obligation\n  - Detail of the obligation\n- Second obligation\n\n" +
            "| Item | Price |\n| --- | --- |\n| Setup | 100 |\n| Support \\| monthly | 20 |\n\n" +
            "Closing remarks.",
            result.Value.Text);
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", result.Value.MediaType);
    }

    [Fact] // AC-10
    public void RunsTabsAndBreaksStayInsideTheirParagraph()
    {
        var body = "<w:p><w:r><w:t>One</w:t></w:r><w:r><w:tab/></w:r><w:r><w:t>two</w:t></w:r><w:r><w:br/></w:r><w:r><w:t>three</w:t></w:r></w:p>";

        var result = Extract("a.docx", Docx(body));

        Assert.True(result.IsSuccess);
        Assert.Equal("One two three", result.Value.Text);
    }

    [Fact] // AC-10
    public void ATextBoxInsideAParagraphKeepsTheSurroundingText()
    {
        var body =
            "<w:p><w:r><w:t xml:space=\"preserve\">Before box </w:t></w:r>" +
            "<w:r><w:txbxContent><w:p><w:r><w:t>Inside box</w:t></w:r></w:p></w:txbxContent></w:r>" +
            "<w:r><w:t>after box</w:t></w:r></w:p>";

        var result = Extract("a.docx", Docx(body));

        Assert.True(result.IsSuccess);
        Assert.Equal("Before box after box\n\nInside box", result.Value.Text);
    }

    [Fact] // AC-10
    public void AnAlternateContentFallbackDoesNotRepeatTheChoice()
    {
        var body =
            "<w:p><w:r><mc:AlternateContent xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\">" +
            "<mc:Choice Requires=\"wps\"><w:txbxContent><w:p><w:r><w:t>Shape text</w:t></w:r></w:p></w:txbxContent></mc:Choice>" +
            "<mc:Fallback><w:txbxContent><w:p><w:r><w:t>Shape text</w:t></w:r></w:p></w:txbxContent></mc:Fallback>" +
            "</mc:AlternateContent></w:r></w:p>" + Paragraph("After the shape");

        var result = Extract("a.docx", Docx(body));

        Assert.True(result.IsSuccess);
        Assert.Equal("Shape text\n\nAfter the shape", result.Value.Text);
    }

    [Fact] // AC-10
    public void TheMainDocumentIsFoundThroughThePackageRelationshipsAndCaseInsensitively()
    {
        var package = Package(WrapBody(Paragraph("Found it")), entryName: "Word/Main.XML", relationshipTarget: "/word/main.xml");

        var result = Extract("a.docx", package);

        Assert.True(result.IsSuccess);
        Assert.Equal("Found it", result.Value.Text);
    }

    [Fact] // AC-5
    public void TwoPartsWithTheSameNameAreAmbiguousAndRefused()
    {
        using var output = new MemoryStream();
        using (var zip = new global::System.IO.Compression.ZipArchive(output, global::System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var text in new[] { "first", "second" })
            {
                using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
                writer.Write("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" + Paragraph(text) + "</w:body></w:document>");
            }
        }

        var result = Extract("twins.docx", output.ToArray());

        Assert.False(result.IsSuccess);
        Assert.Equal("attachment.unreadable", result.Error.Code);
    }

    [Fact] // AC-6
    public void ADocxWithoutTextIsRejected()
    {
        var result = Extract("empty.docx", Docx("<w:p/>"));

        Assert.False(result.IsSuccess);
        Assert.Equal("attachment.no_text", result.Error.Code);
    }

    [Fact] // AC-5
    public void ADamagedPackageIsUnreadable()
    {
        var damaged = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00, 0x08, 0x00, 1, 2, 3, 4, 5 };

        var result = Extract("broken.docx", damaged);

        Assert.False(result.IsSuccess);
        Assert.Equal("attachment.unreadable", result.Error.Code);
    }

    [Fact] // AC-5
    public void APasswordProtectedDocxIsUnreadable()
    {
        // Encrypted Office files are not zip packages but OLE compound files.
        var ole = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0 };

        var result = Extract("secret.docx", ole);

        Assert.False(result.IsSuccess);
        Assert.Equal("attachment.unreadable", result.Error.Code);
    }

    [Fact] // AC-5
    public void APackageWithoutTheMainDocumentIsUnreadable()
    {
        using var output = new MemoryStream();
        using (var zip = new global::System.IO.Compression.ZipArchive(output, global::System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("[Content_Types].xml").Open());
            writer.Write("<Types/>");
        }

        var result = Extract("hollow.docx", output.ToArray());

        Assert.False(result.IsSuccess);
        Assert.Equal("attachment.unreadable", result.Error.Code);
    }

    [Fact] // AC-5
    public void AZipBombIsUnreadableWithoutBeingExpanded()
    {
        // 60 MB of one repeated character compresses to a few tens of KB: a ratio no real document has.
        var bomb = "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
                   new string('a', 60 * 1024 * 1024) + "</w:body></w:document>";

        var result = Extract("bomb.docx", Package(bomb));

        Assert.False(result.IsSuccess);
        Assert.Equal("attachment.unreadable", result.Error.Code);
    }

    [Fact] // AC-5
    public void ADocumentTypeDeclarationIsRefusedSoNoEntityIsExpanded()
    {
        var xml =
            "<?xml version=\"1.0\"?><!DOCTYPE w:document [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;\">]>" +
            "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
            "<w:p><w:r><w:t>&b;</w:t></w:r></w:p></w:body></w:document>";

        var result = Extract("xxe.docx", Package(xml));

        Assert.False(result.IsSuccess);
        Assert.Equal("attachment.unreadable", result.Error.Code);
    }

    [Fact] // AC-5
    public void AnExternalEntityToALocalFileIsNeverRead()
    {
        var xml =
            "<?xml version=\"1.0\"?><!DOCTYPE w:document [<!ENTITY secret SYSTEM \"file:///etc/passwd\">]>" +
            "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
            "<w:p><w:r><w:t>&secret;</w:t></w:r></w:p></w:body></w:document>";

        var result = Extract("xxe2.docx", Package(xml));

        Assert.False(result.IsSuccess);
        Assert.Equal("attachment.unreadable", result.Error.Code);
    }

    [Fact] // AC-5
    public void AnExternalRelationshipIsNotFollowedNorReadAsContent()
    {
        var rels = Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId9\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/oleObject\" " +
            "Target=\"http://evil.test/payload\" TargetMode=\"External\"/></Relationships>");

        var result = Extract(
            "linked.docx",
            Docx(Paragraph("Visible text"), new Dictionary<string, byte[]> { ["word/_rels/document.xml.rels"] = rels }));

        Assert.True(result.IsSuccess);
        Assert.Equal("Visible text", result.Value.Text);
        Assert.DoesNotContain("evil.test", result.Value.Text, StringComparison.Ordinal);
    }

    [Theory] // AC-5
    [InlineData("word/vbaProject.bin")]
    [InlineData("word/embeddings/oleObject1.bin")]
    public void MacrosAndEmbeddedObjectsAreRefused(string part)
    {
        var result = Extract(
            "active.docx",
            Docx(Paragraph("Looks harmless"), new Dictionary<string, byte[]> { [part] = [1, 2, 3] }));

        Assert.False(result.IsSuccess);
        Assert.Equal("attachment.unreadable", result.Error.Code);
    }

    [Fact] // AC-5
    public void APackageWithTooManyPartsIsUnreadable()
    {
        var parts = Enumerable.Range(0, 3000).ToDictionary(i => $"word/media/image{i}.xml", _ => new byte[] { 1 });

        var result = Extract("many.docx", Docx(Paragraph("x"), parts));

        Assert.False(result.IsSuccess);
        Assert.Equal("attachment.unreadable", result.Error.Code);
    }

    [Fact]
    public void ADocxNameWithOtherContentIsNotADocx()
    {
        var result = Extract("photo.docx", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        Assert.False(result.IsSuccess);
        Assert.Equal("attachment.unsupported_type", result.Error.Code);
    }

    [Fact] // AC-4
    public void ADocxContentUnderATextNameIsNotText()
    {
        var result = Extract("report.txt", Docx(Paragraph("hello")));

        Assert.False(result.IsSuccess);
        Assert.Equal("attachment.unsupported_type", result.Error.Code);
    }

    [Fact]
    public void APdfReadsTheTextOfEveryPage()
    {
        var result = Extract("report.pdf", Pdf("First page text", "Second page text"));

        Assert.True(result.IsSuccess);
        Assert.Contains("First page text", result.Value.Text, StringComparison.Ordinal);
        Assert.Contains("Second page text", result.Value.Text, StringComparison.Ordinal);
        Assert.True(
            result.Value.Text.IndexOf("First page", StringComparison.Ordinal) < result.Value.Text.IndexOf("Second page", StringComparison.Ordinal));
        Assert.Equal("application/pdf", result.Value.MediaType);
    }

    [Fact]
    public void APdfWithBytesBeforeTheHeaderIsStillAPdf()
    {
        var padded = new byte[] { 0x0A, 0x0A, 0x20 }.Concat(Pdf("Printed output")).ToArray();

        var result = Extract("printed.pdf", padded);

        Assert.True(result.IsSuccess);
        Assert.Contains("Printed output", result.Value.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void APdfReadsOneColumnAfterTheOtherNotLineByLine()
    {
        var pdf = PdfTwoColumns(
            ["Left one", "Left two", "Left three"],
            ["Right one", "Right two", "Right three"]);

        var result = Extract("columns.pdf", pdf);

        Assert.True(result.IsSuccess);
        var text = result.Value.Text;
        Assert.True(
            text.IndexOf("Left three", StringComparison.Ordinal) < text.IndexOf("Right one", StringComparison.Ordinal),
            $"Expected the left column before the right one, got: {text.Replace("\n", " / ")}");
    }

    [Fact] // AC-6
    public void APdfWithoutATextLayerIsRejected()
    {
        var result = Extract("scan.pdf", Pdf(string.Empty, string.Empty));

        Assert.False(result.IsSuccess);
        Assert.Equal("attachment.no_text", result.Error.Code);
    }

    [Fact] // AC-5
    public void ACorruptPdfIsUnreadable()
    {
        var corrupt = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<< /Type /Catalog \nstream\ngarbage garbage\n");

        var result = Extract("broken.pdf", corrupt);

        Assert.False(result.IsSuccess);
        Assert.Equal("attachment.unreadable", result.Error.Code);
    }

    [Fact] // AC-5
    public void AnEncryptedPdfIsUnreadable()
    {
        var result = Extract("locked.pdf", EncryptedPdf());

        Assert.False(result.IsSuccess);
        Assert.Equal("attachment.unreadable", result.Error.Code);
    }

    [Fact]
    public void APdfNameWithOtherContentIsNotAPdf()
    {
        var result = Extract("notes.pdf", Encoding.UTF8.GetBytes("just some text"));

        Assert.False(result.IsSuccess);
        Assert.Equal("attachment.unsupported_type", result.Error.Code);
    }

    [Fact] // AC-4
    public void APdfUnderATextNameIsNotText()
    {
        var result = Extract("report.md", Pdf("hello"));

        Assert.False(result.IsSuccess);
        Assert.Equal("attachment.unsupported_type", result.Error.Code);
    }

    [Fact] // AC-7
    public async Task ExtractionHonoursTheRequestToStop()
    {
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DocumentTextExtractor.ExtractAsync("report.pdf", Pdf("text"), TimeSpan.FromSeconds(30), stop.Token));
    }
}
