using Quorum.Web.Client;

namespace Quorum.Integration.Tests.Web;

/// <summary>The limits, titles, sizes and download names of the panel (spec 014, D-4, D-16, AC-20, AC-32).</summary>
public sealed class PreviewRulesTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(3276, "3.2 KB")]
    [InlineData(1_048_576, "1 MB")]
    [InlineData(1_572_864, "1.5 MB")]
    public void SizesAreRoundedToOneDecimalWithADotWhateverTheLocale(int bytes, string expected) =>
        Assert.Equal(expected, PreviewRules.FormatSize(bytes));

    [Fact]
    public void ContentUpToOneMillionBytesIsWithinTheLimit()
    {
        Assert.Null(PreviewRules.LimitNotice(new PreviewContent(PreviewKind.Json, new string('a', PreviewRules.MaxTextBytes))));

        var notice = PreviewRules.LimitNotice(new PreviewContent(PreviewKind.Json, new string('a', PreviewRules.MaxTextBytes + 1)));

        Assert.NotNull(notice);
        // 1,000,000 bytes read as 976.6 KB: sizes are in 1024 units.
        Assert.Contains("This block is 976.6 KB; the preview limit is 976.6 KB.", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLimitCountsUtf8BytesNotCharacters()
    {
        // 500,001 "é" are 1,000,002 UTF-8 bytes: over the limit although the character count would fit.
        Assert.NotNull(PreviewRules.LimitNotice(new PreviewContent(PreviewKind.Html, new string('é', 500_001))));
    }

    [Fact]
    public void TheTitleIsTheKindAndTheSizeUnlessTheProducerNamedTheContent()
    {
        Assert.Equal("JSON · 7 B", PreviewRules.Title(new PreviewContent(PreviewKind.Json, """{"a":1}""")));
        Assert.Equal("report.html", PreviewRules.Title(new PreviewContent(PreviewKind.Html, "x", "report.html")));
    }

    [Theory]
    [InlineData(PreviewKind.Html, "preview.html")]
    [InlineData(PreviewKind.Svg, "preview.svg")]
    [InlineData(PreviewKind.Json, "preview.json")]
    [InlineData(PreviewKind.Csv, "preview.csv")]
    [InlineData(PreviewKind.Mermaid, "preview.mmd")]
    public void DownloadNamesTheFileAfterTheKind(PreviewKind kind, string expected) =>
        Assert.Equal(expected, PreviewRules.DownloadName(kind));
}

/// <summary>The storage keys of the consent choices and of the panel width (spec 014, D-17, D-20).</summary>
public sealed class PreviewConsentTests
{
    [Fact]
    public void TheKeysAreScopedToTheUserAndToTheSwitch()
    {
        Assert.Equal("quorum.preview.a@b.test.runScripts.dontAsk", PreviewConsent.StorageKey("a@b.test", PreviewConsentSwitch.RunScripts));
        Assert.Equal("quorum.preview.a@b.test.remoteResources.dontAsk", PreviewConsent.StorageKey("a@b.test", PreviewConsentSwitch.RemoteResources));
        Assert.Equal("quorum.preview.a@b.test.width", PreviewConsent.WidthKey("a@b.test"));
    }
}
