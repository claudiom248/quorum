using Quorum.Web.Client;
using Quorum.Web.Client.Api;

namespace Quorum.Integration.Tests.Web;

public sealed class ContextBreakdownTests
{
    private static AttachmentInfo File(string name, int characters) =>
        new(Guid.NewGuid(), name, "application/octet-stream", characters, characters, DateTimeOffset.UnixEpoch);

    private static ConversationContext Context(int usable, int system, int attachments, int messages) =>
        new(ContextTokenBudget: (int)(usable / 0.8), UsableTokens: usable, system, attachments, messages, usable - system - attachments - messages);

    [Fact]
    public void TheSegmentsFillTheUsableContextAndNameWhatIsLeft()
    {
        var breakdown = ContextBreakdown.From(Context(usable: 1000, system: 100, attachments: 300, messages: 100), []);

        Assert.Equal(
            [SegmentKind.SystemPrompt, SegmentKind.Attachments, SegmentKind.Messages, SegmentKind.Free],
            breakdown.Segments.Select(s => s.Kind));
        Assert.Equal([100, 300, 100, 500], breakdown.Segments.Select(s => s.Tokens));
        Assert.Equal([10.0, 30.0, 10.0, 50.0], breakdown.Segments.Select(s => s.Percent));
        Assert.Equal(100.0, breakdown.Segments.Sum(s => s.Percent), precision: 6);
        Assert.False(breakdown.IsOver);
    }

    [Fact]
    public void WhenTheContextOverflowsTheSegmentsStillAddUpToTheBar()
    {
        var breakdown = ContextBreakdown.From(Context(usable: 1000, system: 100, attachments: 1100, messages: 0), []);

        Assert.True(breakdown.IsOver);
        Assert.Equal(0, breakdown.Segments.Single(s => s.Kind == SegmentKind.Free).Tokens);
        Assert.Equal(100.0, breakdown.Segments.Sum(s => s.Percent), precision: 6);
        Assert.Contains("over by 200", breakdown.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void EachAttachmentShowsAnEstimateOfItsShare()
    {
        var breakdown = ContextBreakdown.From(
            Context(usable: 3000, system: 50, attachments: 2100, messages: 0),
            [File("meal_plan.pdf", 7931), File("notes.md", 40)]);

        Assert.Equal(["meal_plan.pdf", "notes.md"], breakdown.Files.Select(f => f.FileName));
        Assert.Equal(["PDF", "MD"], breakdown.Files.Select(f => f.Kind));
        Assert.Equal([1982, 10], breakdown.Files.Select(f => f.Tokens));
    }

    [Theory]
    [InlineData("report.pdf", "PDF")]
    [InlineData("REPORT.DOCX", "DOCX")]
    [InlineData("a.txt", "TXT")]
    [InlineData("a.md", "MD")]
    [InlineData("a.markdown", "MD")]
    [InlineData("data.csv", "CSV")]
    [InlineData("mystery.bin", "FILE")]
    [InlineData("noextension", "FILE")]
    public void TheFileKindComesFromTheExtension(string fileName, string kind)
    {
        Assert.Equal(kind, AttachmentRules.Kind(fileName));
    }

    [Fact]
    public void TheSummaryNamesTheUsedShareAndTheMargin()
    {
        var breakdown = ContextBreakdown.From(Context(usable: 3300, system: 48, attachments: 2100, messages: 0), []);

        Assert.Equal("2.1k of 3.3k tokens", breakdown.Summary);
        Assert.Equal("1.2k left", breakdown.Margin);
    }
}
