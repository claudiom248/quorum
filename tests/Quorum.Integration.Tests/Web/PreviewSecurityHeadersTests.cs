using System.Net;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.Web;

/// <summary>
/// The viewer runs in an iframe sandboxed without <c>allow-same-origin</c>: its origin is opaque and <c>'self'</c>
/// would match nothing, so <c>/preview/**</c> answers carry a policy that names the app's own origin and allows no
/// network at all (spec 014, D-7, AC-36). No other path gets it.
/// </summary>
public sealed class PreviewSecurityHeadersTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/preview/viewer.html")]
    [InlineData("/preview/viewer.js")]
    [InlineData("/preview/lib/json-formatter-js/json-formatter.umd.js")]
    [InlineData("/preview/lib/json-formatter-js/json-formatter.css")]
    public async Task TheViewerAndItsVendoredFilesAnswerWithThePreviewPolicy(string path)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = app.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var policy = ContentSecurityPolicyOf(response);
        Assert.NotNull(policy);
        Assert.Contains("default-src 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("script-src http://localhost/preview/", policy, StringComparison.Ordinal);
        Assert.Contains("style-src http://localhost/preview/ 'unsafe-inline'", policy, StringComparison.Ordinal);
        Assert.Contains("img-src data:", policy, StringComparison.Ordinal);
        Assert.Contains("frame-src about:", policy, StringComparison.Ordinal);
        Assert.Contains("form-action 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("base-uri 'none'", policy, StringComparison.Ordinal);
        // No connect-src at all: fetch, XHR and WebSockets from the viewer have nowhere to go (AC-24, AC-36).
        Assert.DoesNotContain("connect-src", policy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRestOfTheAppKeepsItsHeadersUntouched()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = app.CreateClient();

        using var response = await client.GetAsync(new Uri("/css/app.css", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(ContentSecurityPolicyOf(response));
    }

    private static string? ContentSecurityPolicyOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Content-Security-Policy", out var values) ||
        response.Content.Headers.TryGetValues("Content-Security-Policy", out values)
            ? string.Join(" ", values)
            : null;
}
