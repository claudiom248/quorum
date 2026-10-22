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

        Assert.Equal([1, 3], view.Cited.Select(item => item.N));
        Assert.Equal([2], view.Others.Select(item => item.N));
    }

    [Theory]
    [InlineData("see [1, 2]", new[] { 1, 2 })]
    [InlineData("see [1][2]", new[] { 1, 2 })]
    [InlineData("see [1](https://example.com)", new[] { 1 })]
    [InlineData("a list [x] and [ 1 ] and 2", new int[0])]
    [InlineData("see [99999] and [0]", new int[0])]
    public void MarkersAreReadWhereTheModelWritesThem(string text, int[] cited)
    {
        var view = new SourcesView([Source(1), Source(2)], text);

        Assert.Equal(cited, view.Cited.Select(item => item.N));
        Assert.Equal(2, view.Cited.Count + view.Others.Count);
    }

    [Fact]
    public void WithoutMarkersEverySourceIsAmongTheOthers()
    {
        var view = new SourcesView([Source(1), Source(2)], "No citations here.");

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
    public void TheDetailNamesTheKnowledgeBaseAndThePositionWhenThereIsOne()
    {
        var view = new SourcesView([Source(1, position: "p. 4"), Source(2)], string.Empty);

        Assert.Equal(["handbook · p. 4", "handbook"], view.Others.Select(item => item.Detail));
    }
}
