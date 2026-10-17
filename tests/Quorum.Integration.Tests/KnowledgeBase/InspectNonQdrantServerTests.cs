using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.KnowledgeBase;

/// <summary>A reachable server that is not Qdrant (a web server, a proxy without the route, the REST port) must not look like a Qdrant with a missing collection.</summary>
public sealed class InspectNonQdrantServerTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("html404")]
    [InlineData("text200")]
    public async Task AnHttp2ServerThatDoesNotSpeakGrpcIsReportedAsUnexpected(string kind)
    {
        await using var server = await StartServerAsync(HttpProtocols.Http2);
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        InspectApi.ServeEmbeddings(app);

        var report = await InspectApi.InspectOkAsync(admin, new Uri($"{server.Urls.First()}/{kind}"), "docs");

        Assert.False(report.Reachable);
        Assert.False(report.CollectionExists);
        Assert.Contains("knowledge_base.qdrant_unexpected_response", report.WarningCodes);
        Assert.DoesNotContain("knowledge_base.collection_not_found", report.WarningCodes);
    }

    [Fact] // The common mistake: the REST port (HTTP/1.1) instead of the gRPC one.
    public async Task AnHttp1ServerIsReportedWithAHintAboutThePort()
    {
        await using var server = await StartServerAsync(HttpProtocols.Http1);
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        InspectApi.ServeEmbeddings(app);

        var report = await InspectApi.InspectOkAsync(admin, new Uri(server.Urls.First()), "docs");

        Assert.False(report.Reachable);
        var warning = Assert.Single(report.Warnings, w => w.Code == "knowledge_base.qdrant_unexpected_response");
        Assert.Contains("6334", warning.Message, StringComparison.Ordinal);
    }

    private static async Task<WebApplication> StartServerAsync(HttpProtocols protocols)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = protocols));
        var server = builder.Build();
        server.MapGet("/html404/{**rest}", () => Results.Content("<html><body>Not found</body></html>", "text/html", statusCode: StatusCodes.Status404NotFound));
        server.MapPost("/html404/{**rest}", () => Results.Content("<html><body>Not found</body></html>", "text/html", statusCode: StatusCodes.Status404NotFound));
        server.MapPost("/text200/{**rest}", () => Results.Text("hello"));
        server.MapFallback(() => Results.Text("hello"));
        await server.StartAsync(TestContext.Current.CancellationToken);
        return server;
    }
}
