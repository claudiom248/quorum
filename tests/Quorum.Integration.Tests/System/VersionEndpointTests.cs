using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.System;

public sealed class VersionEndpointTests(PostgresFixture postgres)
{
    [Fact]
    public async Task ReturnsTheVersionStampedOnTheHostAssembly()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = app.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/version", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<VersionBody>(TestContext.Current.CancellationToken);
        var expected = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion
            .Split('+')[0];
        Assert.Equal(expected, body?.Version);
    }

    private sealed record VersionBody(string Version);
}
