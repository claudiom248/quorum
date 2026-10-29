using Quorum.Shared.Extraction;

namespace Quorum.Integration.Tests.Attachments;

public sealed class PdfLigatureTests
{
    [Fact]
    public void LigatureGlyphsBecomeTheirLetters()
    {
        // Many PDF generators store "fi", "fl", "ff"... as single presentation characters (U+FB00 to U+FB06).
        var extracted = "ﬁsh ﬂour oﬀer oﬃce baﬄe moﬅ ﬆory";

        var text = PdfText.NormalizeLigatures(extracted);

        Assert.Equal("fish flour offer office baffle most story", text);
    }

    [Fact]
    public void OtherTextIsLeftAsItIs()
    {
        var text = "Caffè ﬁne? Naïve café 100% — “quotes” and 日本語";

        Assert.Equal(text.Replace("ﬁ", "fi", StringComparison.Ordinal), PdfText.NormalizeLigatures(text));
    }

    [Fact]
    public void AMixOfLigaturesAndPlainLettersKeepsItsLength()
    {
        Assert.Equal("different", PdfText.NormalizeLigatures("diﬀerent"));
        Assert.Equal(string.Empty, PdfText.NormalizeLigatures(string.Empty));
    }
}
