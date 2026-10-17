using System.Net;
using Quorum.Integration.Tests.Fixtures;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBase;

public sealed class InspectUrlGuardTests(PostgresFixture postgres, QdrantFixture qdrant)
{
    [Theory] // AC-15
    [InlineData("http://169.254.169.254")]
    [InlineData("http://169.254.0.7:6333")]
    [InlineData("http://[fe80::1]:6333")]
    [InlineData("http://100.100.100.200:6333")]
    [InlineData("http://[fd00:ec2::254]:6333")]
    [InlineData("http://[::ffff:169.254.169.254]:6333")]
    [InlineData("http://0.0.0.0:6333")]
    public async Task ABlockedAddressIsRefusedWhenInspecting(string url)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        InspectApi.ServeEmbeddings(app);

        using var response = await InspectApi.PostAsync(admin, url, "docs");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("knowledge_base.url_blocked", await InspectApi.ProblemCodeAsync(response));
    }

    [Fact] // AC-15
    public async Task AHostNameThatResolvesToABlockedAddressIsRefused()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        InspectApi.ServeEmbeddings(app);
        app.McpHosts.Script("metadata.test", ["169.254.169.254"]);

        using var response = await InspectApi.PostAsync(admin, "http://metadata.test:6333", "docs");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("knowledge_base.url_blocked", await InspectApi.ProblemCodeAsync(response));
    }

    [Fact] // AC-15: the name passes the up-front checks and resolves to a blocked address only when the connection is made
    public async Task ANameThatChangesItsAddressAfterTheChecksIsRefusedAtConnectionTime()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        InspectApi.ServeEmbeddings(app);
        var name = QdrantFixture.NewPrefix() + SampleCollections.Flat;
        await qdrant.SeedAsync([SampleCollections.CreateFlat(name)]);
        var port = (await qdrant.GetUrlAsync()).Port;
        // The inspection checks the name, the Qdrant client checks it again, and the connection resolves it a third time.
        app.McpHosts.Script("rebind.test", ["127.0.0.1"], ["127.0.0.1"], ["169.254.169.254"]);

        using var response = await InspectApi.PostAsync(admin, $"http://rebind.test:{port}", name);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("knowledge_base.url_blocked", await InspectApi.ProblemCodeAsync(response));
    }

    [Fact] // D-6: the internal network stays reachable
    public async Task AnInternalHostNameIsAccepted()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        InspectApi.ServeEmbeddings(app);
        var name = QdrantFixture.NewPrefix() + SampleCollections.Flat;
        await qdrant.SeedAsync([SampleCollections.CreateFlat(name)]);
        var port = (await qdrant.GetUrlAsync()).Port;
        app.McpHosts.Script("qdrant.internal.test", ["127.0.0.1"]);

        var report = await InspectApi.InspectOkAsync(admin, new Uri($"http://qdrant.internal.test:{port}"), name);

        Assert.True(report.Reachable);
        Assert.True(report.CollectionExists);
    }
}
