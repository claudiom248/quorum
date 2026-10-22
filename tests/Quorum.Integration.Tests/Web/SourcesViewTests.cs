using Quorum.Web.Client;
using Quorum.Web.Client.Api;

namespace Quorum.Integration.Tests.Web;

public sealed class SourcesViewTests
{
    private static MessageSourceDto Source(int n, string? title = null, string? url = null, string? documentId = null, string? position = null) =>
        new(n, "handbook", documentId, title, position, url);

    private static int[] NumbersOf(IEnumerable<SourceDocument> documents) =>
        [.. documents.SelectMany(document => document.Sections).SelectMany(section => section.Numbers)];

    [Fact]
    public void SourcesTheTextCitesComeFirstAndTheOthersAreSeparate()
    {
        var view = new SourcesView([Source(3, "C"), Source(1, "A"), Source(2, "B")], "Yes [3], as also said in [1].");

        Assert.Equal([1, 3], NumbersOf(view.Cited));
        Assert.Equal([2], NumbersOf(view.Others));
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

        Assert.Equal(cited, NumbersOf(view.Cited));
        Assert.Equal(2, NumbersOf(view.Cited).Length + NumbersOf(view.Others).Length);
    }

    [Fact]
    public void WithoutMarkersEverySourceIsAmongTheOthers()
    {
        var view = new SourcesView([Source(1, "A"), Source(2, "B")], "No citations here.");

        Assert.Empty(view.Cited);
        Assert.Equal(2, view.Others.Count);
        Assert.True(view.HasSources);
    }

    [Fact]
    public void NoSourcesMeansNothingToShow()
    {
        Assert.False(new SourcesView(null, "Text [1]").HasSources);
        Assert.False(new SourcesView([], "Text [1]").HasSources);
    }

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

        Assert.Equal(["Title", "doc-2", "Source 3"], view.Others.Select(document => document.Label));
    }

    [Fact]
    public void ThePositionSaysWhetherItIsAPageOrASection()
    {
        var view = new SourcesView(
            [Source(1, "A", documentId: "a.html", position: "Core hours"), Source(2, "A", documentId: "a.html", position: "12"), Source(3, "A", documentId: "a.html")],
            string.Empty);

        Assert.Equal(["Section: Core hours", "Page: 12", null], view.Others.Single().Sections.Select(section => section.Position));
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
        Assert.Equal(["handbook", "handbook", "handbook · File: c.html"], view.Others.Select(document => document.Detail));
    }

    [Fact]
    public void SectionsOfOneDocumentAreListedUnderItAndOtherDocumentsApart()
    {
        var view = new SourcesView(
            [
                Source(1, "Remote work", documentId: "remote.html", position: "Core hours"),
                Source(2, "Leave", documentId: "leave.html", position: "Carry-over"),
                Source(3, "Remote work", documentId: "remote.html", position: "Equipment"),
            ],
            string.Empty);

        Assert.Equal(["Remote work", "Leave"], view.Others.Select(document => document.Label));
        Assert.Equal([[1], [3]], view.Others[0].Sections.Select(section => section.Numbers));
        Assert.Equal([[2]], view.Others[1].Sections.Select(section => section.Numbers));
    }

    [Fact]
    public void ADocumentCitedInOnePlaceAndNotInAnotherAppearsInBothLists()
    {
        var view = new SourcesView(
            [Source(1, "Remote work", documentId: "remote.html", position: "Core hours"), Source(2, "Remote work", documentId: "remote.html", position: "Equipment")],
            "See [2].");

        Assert.Equal([2], NumbersOf(view.Cited));
        Assert.Equal([1], NumbersOf(view.Others));
    }

    [Fact]
    public void ADocumentCitedAsAWholeOnceFitsOnOneLine()
    {
        var view = new SourcesView([Source(1, "Alone"), Source(2, "Remote work", position: "Core hours")], string.Empty);

        Assert.True(view.Others[0].IsOneLine);
        Assert.False(view.Others[1].IsOneLine);
    }

    [Fact]
    public void ThePlaceTwiceIsShownOnceUnderAllItsNumbers()
    {
        // An answer stored before the server gave one number to a source: the same place came back under a new one.
        var view = new SourcesView(
            [
                Source(1, "Remote work", documentId: "remote.html", position: "Core hours"),
                Source(2, "Remote work", documentId: "remote.html", position: "Equipment"),
                Source(3, "Leave", documentId: "leave.html", position: "Core hours"),
                Source(4, "Remote work", documentId: "remote.html", position: "Core hours"),
            ],
            "As said in [4].");

        var cited = Assert.Single(view.Cited);
        Assert.Equal([[1, 4]], cited.Sections.Select(section => section.Numbers));
        Assert.Equal([2, 3], NumbersOf(view.Others));
    }

    [Fact]
    public void SourcesWithNeitherTitleNorIdAreNeverGroupedTogether()
    {
        var view = new SourcesView([Source(1, position: "a"), Source(2, position: "b")], string.Empty);

        Assert.Equal(["Source 1", "Source 2"], view.Others.Select(document => document.Label));
    }

    [Fact]
    public void ALinkAlwaysSitsOnTheSectionsAndNeverOnTheHeadingOfADocumentWithSections()
    {
        // The same address for every section and a different one for each must look alike: the heading is never the link.
        var same = new SourcesView(
            [Source(1, "A", url: "https://example.com/a", position: "x"), Source(2, "A", url: "https://example.com/a", position: "y")],
            string.Empty);
        var different = new SourcesView(
            [Source(1, "A", url: "https://example.com/a#x", position: "x"), Source(2, "A", url: "https://example.com/a#y", position: "y")],
            string.Empty);

        Assert.Null(same.Others.Single().Href);
        Assert.Equal(["https://example.com/a", "https://example.com/a"], same.Others.Single().Sections.Select(section => section.Href));
        Assert.Null(different.Others.Single().Href);
        Assert.Equal(["https://example.com/a#x", "https://example.com/a#y"], different.Others.Single().Sections.Select(section => section.Href));
    }

    [Fact]
    public void ADocumentCitedOnceAsAWholeLinksOnItsTitle()
    {
        var view = new SourcesView([Source(1, "Laptops", url: "https://example.com/laptops", documentId: "laptops.html")], string.Empty);

        var document = view.Others.Single();
        Assert.True(document.IsOneLine);
        Assert.Equal("https://example.com/laptops", document.Href);
    }

    [Fact]
    public void TheFileIsNotNamedWhenAnySectionHasALink()
    {
        var view = new SourcesView(
            [
                Source(1, "Linked", url: "https://example.com/a#x", documentId: "a.html", position: "x"),
                Source(2, "Linked", url: "file:///srv/a.html", documentId: "a.html", position: "y"),
                Source(3, "Local", url: "file:///srv/b.html", documentId: "b.html", position: "x"),
                Source(4, "Local", url: "file:///srv/b.html", documentId: "b.html", position: "y"),
            ],
            string.Empty);

        // One linked section is enough: the link says where the document is. Without any, the file is the only way to find it.
        Assert.Equal(["handbook", "handbook · File: b.html"], view.Others.Select(document => document.Detail));
    }
}
