using Quorum.Web.Client;
using Quorum.Web.Client.Api;

namespace Quorum.Integration.Tests.Web;

public sealed class SourcesViewTests
{
    private static MessageSourceDto Source(int n, string? title = null, string? url = null, string? documentId = null, string? position = null) =>
        new(n, "handbook", documentId, title, position, url);

    [Fact]
    public void SourcesTheTextCitesComeFirstAndTheOthersAreSeparate()
    {
        var view = new SourcesView([Source(3, "C"), Source(1, "A"), Source(2, "B")], "Yes [3], as also said in [1].");

        Assert.Equal([1, 3], view.Cited.SelectMany(item => item.Numbers));
        Assert.Equal([2], view.Others.SelectMany(item => item.Numbers));
    }

    [Theory]
    [InlineData("see [1, 2]", new[] { 1, 2 })]
    [InlineData("see [1][2]", new[] { 1, 2 })]
    [InlineData("see [1](https://example.com)", new[] { 1 })]
    [InlineData("a list [x] and [ 1 ] and 2", new int[0])]
    [InlineData("see [99999] and [0]", new int[0])]
    public void MarkersAreReadWhereTheModelWritesThem(string text, int[] cited)
    {
        var view = new SourcesView([Source(1, "A"), Source(2, "B")], text);

        Assert.Equal(cited, view.Cited.SelectMany(item => item.Numbers));
        Assert.Equal(2, view.Cited.Count + view.Others.Count);
    }

    [Fact]
    public void WithoutMarkersEverySourceIsAmongTheOthers()
    {
        var view = new SourcesView([Source(1, "A"), Source(2, "B")], "No citations here.");

        Assert.Empty(view.Cited);
        Assert.Equal(2, view.Others.Count);
        Assert.True(view.HasSources);
    }

    [Theory]
    [InlineData(null)]
    public void NoSourcesMeansNothingToShow(IReadOnlyList<MessageSourceDto>? sources) =>
        Assert.False(new SourcesView(sources, "Text [1]").HasSources);

    [Theory]
    [InlineData("https://example.com/a?b=1", "https://example.com/a?b=1")]
    [InlineData("http://example.com/a", "http://example.com/a")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("JAVASCRIPT:alert(1)", null)]
    [InlineData("data:text/html,<script>alert(1)</script>", null)]
    [InlineData("file:///etc/passwd", null)]
    [InlineData("//example.com/a", null)]
    [InlineData("/relative/path", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void OnlyWebAddressesBecomeLinks(string? url, string? href) =>
        Assert.Equal(href, new SourcesView([Source(1, url: url)], "[1]").Cited.Single().Href);

    [Fact]
    public void TheLabelFallsBackFromTitleToDocumentIdToTheNumber()
    {
        var view = new SourcesView([Source(1, "Title", documentId: "doc-1"), Source(2, " ", documentId: "doc-2"), Source(3)], string.Empty);

        Assert.Equal(["Title", "doc-2", "Source 3"], view.Others.Select(item => item.Label));
    }

    [Fact]
    public void TheDetailSaysWhatEachPartIs()
    {
        var view = new SourcesView(
            [Source(1, "A", documentId: "a.html", position: "Core hours"), Source(2, "B", documentId: "b.pdf", position: "12"), Source(3, "C")],
            string.Empty);

        Assert.Equal(
            ["handbook · Section: Core hours · File: a.html", "handbook · Page: 12 · File: b.pdf", "handbook"],
            view.Others.Select(item => item.Detail));
    }

    [Fact]
    public void TheFileIsNamedOnlyWhenNothingElseSaysWhichDocumentItIs()
    {
        var view = new SourcesView(
            [
                Source(1, "Linked", url: "https://example.com/a", documentId: "a.html"),
                Source(2, url: "file:///srv/documents/b.html", documentId: "b.html"),
                Source(3, "Local", url: "file:///srv/documents/c.html", documentId: "c.html"),
            ],
            string.Empty);

        // The link says it for the first; the id already is the label of the second, which has no title.
        Assert.Equal(["handbook", "handbook", "handbook · File: c.html"], view.Others.Select(item => item.Detail));
    }

    [Fact]
    public void SourcesThatPointAtTheSamePlaceAreShownOnceUnderAllTheirNumbers()
    {
        var view = new SourcesView(
            [
                Source(1, "Remote work", documentId: "remote.html", position: "Core hours"),
                Source(2, "Remote work", documentId: "remote.html", position: "Equipment"),
                Source(3, "Leave", documentId: "leave.html", position: "Core hours"),
                Source(4, "Remote work", documentId: "remote.html", position: "Core hours"),
            ],
            "As said in [4].");

        var cited = Assert.Single(view.Cited);
        Assert.Equal([1, 4], cited.Numbers);
        Assert.Equal([[2], [3]], view.Others.Select(item => item.Numbers));
    }

    [Fact]
    public void ADifferentDocumentOrPositionIsNotMerged()
    {
        var view = new SourcesView(
            [Source(1, "A", documentId: "a", position: "x"), Source(2, "A", documentId: "b", position: "x"), Source(3, "A", documentId: "a", position: "y"), Source(4, "A", documentId: "a", position: "x", url: "https://example.com")],
            string.Empty);

        Assert.Equal(4, view.Others.Count);
    }
}
