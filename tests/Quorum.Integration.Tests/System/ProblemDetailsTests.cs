using System.Net;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.System;

public sealed class ProblemDetailsTests(PostgresFixture postgres)
{
    [Fact]
    public async Task UnknownApiRouteReturnsProblemDetails()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = app.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/does-not-exist", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
