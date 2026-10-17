using System.Text.Json;
using Quorum.Web.Client;
using Quorum.Web.Client.Api;

namespace Quorum.Integration.Tests.Web;

/// <summary>The logic behind the tools admin page: hints, headers, ids and the words for a scope (spec 004, AC-48).</summary>
public sealed class ToolAdminViewTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void OnlyTheHintsTheServerSetToTrueAreDescribed()
    {
        var labels = ToolAdminView.DescribeAnnotations(Json("""{"readOnlyHint":true,"destructiveHint":false,"openWorldHint":true,"title":"x"}"""));

        Assert.Equal(["read-only", "reaches outside systems"], labels);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""[true]""")]
    [InlineData("""{}""")]
    [InlineData("""{"readOnlyHint":"yes"}""")]
    public void NoUsableAnnotationsDescribeNothing(string? json)
    {
        Assert.Empty(ToolAdminView.DescribeAnnotations(json is null ? null : Json(json)));
    }

    [Fact]
    public void HeadersAreReadOnePerLineAndTheValueKeepsItsColons()
    {
        var (headers, error) = ToolAdminView.ParseHeaders("Authorization: Bearer abc\r\n\r\nX-Url:  http://x:1/y  \n");

        Assert.Null(error);
        Assert.Equal("Bearer abc", headers!["Authorization"]);
        Assert.Equal("http://x:1/y", headers["x-url"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n \r\n")]
    public void NoHeadersTypedMeansNull(string? text)
    {
        var (headers, error) = ToolAdminView.ParseHeaders(text);

        Assert.Null(headers);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("no colon here")]
    [InlineData(": value")]
    [InlineData("Bad Name: value")]
    [InlineData("A: 1\na: 2")]
    public void AMalformedOrRepeatedHeaderIsRefused(string text)
    {
        var (headers, error) = ToolAdminView.ParseHeaders(text);

        Assert.Null(headers);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void IdsAreSplitOnAnySeparatorAndRepeatedOnesCollapse()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var (ids, error) = ToolAdminView.ParseIds($"{a}, {b};\n{a}");

        Assert.Null(error);
        Assert.Equal([a, b], ids);
    }

    [Theory]
    [InlineData("not-an-id")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void AnInvalidIdIsRefused(string text)
    {
        var (ids, error) = ToolAdminView.ParseIds(text);

        Assert.Empty(ids);
        Assert.NotNull(error);
    }

    [Fact]
    public void AScopeIsSummarizedInWords()
    {
        var assistant = new AssistantSummary(Guid.NewGuid(), "Support");

        Assert.Equal("Every tenant", ToolAdminView.ScopeSummary(new ToolScope(ScopeKinds.Global, [], []), []));
        Assert.Equal("This tenant", ToolAdminView.ScopeSummary(new ToolScope(ScopeKinds.Tenant, [], []), []));
        Assert.Equal("This tenant and 2 other(s)", ToolAdminView.ScopeSummary(new ToolScope(ScopeKinds.Tenant, [Guid.NewGuid(), Guid.NewGuid()], []), []));
        Assert.Equal(
            "Assistants: Support, an assistant you cannot see",
            ToolAdminView.ScopeSummary(new ToolScope(ScopeKinds.Assistant, [], [assistant.Id, Guid.NewGuid()]), [assistant]));
    }
}
