using System.Diagnostics;
using System.Text;
using Quorum.Modules.Conversations.Extraction;

namespace Quorum.Integration.Tests.Attachments;

public sealed class DocumentTextExtractorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-7
    public async Task WorkThatIgnoresTheTokenStillEndsAtTheLimit()
    {
        var release = new ManualResetEventSlim();
        var clock = Stopwatch.StartNew();

        // The work never looks at its token, like a parser that cannot be cancelled.
        var result = await DocumentTextExtractor.RunWithLimitAsync(
            _ =>
            {
                release.Wait(TimeSpan.FromSeconds(30), CancellationToken.None); // deliberately not the work token
                return new ExtractedDocument("late", "text/plain");
            },
            TimeSpan.FromMilliseconds(200),
            Ct);
        release.Set();

        Assert.False(result.IsSuccess);
        Assert.Equal("attachment.extraction_timeout", result.Error.Code);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"Waited {clock.Elapsed} for work that should have been abandoned.");
    }

    [Fact]
    public async Task ACancelledRequestIsNotReportedAsATimeout()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DocumentTextExtractor.RunWithLimitAsync(_ => new ExtractedDocument("x", "text/plain"), TimeSpan.FromSeconds(5), aborted.Token));
    }

    [Fact]
    public async Task FastWorkReturnsItsDocument()
    {
        var result = await DocumentTextExtractor.ExtractAsync("a.txt", Encoding.UTF8.GetBytes("hello"), TimeSpan.FromSeconds(5), Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal("hello", result.Value.Text);
    }

    [Fact] // AC-11
    public void CsvValuesKeepTheirSurroundingSpaces()
    {
        var result = DocumentTextExtractor.Extract("a.csv", Encoding.UTF8.GetBytes("name,note\n\" Anna \",  padded  \n"), Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal("| name | note |\n| --- | --- |\n|  Anna  |   padded   |", result.Value.Text);
    }

    [Fact] // AC-11
    public void ARowWithAnEmptyQuotedValueIsDataNotABlankLine()
    {
        var result = DocumentTextExtractor.Extract("a.csv", Encoding.UTF8.GetBytes("name\n\"\"\nAnna\n"), Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal("| name |\n| --- |\n|  |\n| Anna |", result.Value.Text);
    }

    [Fact]
    public void BlankLinesBetweenCsvRowsAreStillSkipped()
    {
        var result = DocumentTextExtractor.Extract("a.csv", Encoding.UTF8.GetBytes("a,b\n\n1,2\n"), Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal("| a | b |\n| --- | --- |\n| 1 | 2 |", result.Value.Text);
    }
}
