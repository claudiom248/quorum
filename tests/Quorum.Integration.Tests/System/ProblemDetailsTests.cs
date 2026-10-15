using System.Net;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.System;

public sealed class ProblemDetailsTests(AppFixture app)
{
    [Fact]
    public async Task UnknownApiRouteReturnsProblemDetails()
    {
        using var client = app.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/does-not-exist", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
