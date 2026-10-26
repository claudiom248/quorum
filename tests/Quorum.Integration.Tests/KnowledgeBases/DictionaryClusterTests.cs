using System.Net;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.KnowledgeBases;

/// <summary>A dictionary field is identified by cluster and name (spec 003, Amendment 1, AC-52).</summary>
public sealed class DictionaryClusterTests(PostgresFixture postgres)
{
    [Fact] // AC-52
    public async Task TheSameNameLivesInDifferentClustersWithDifferentTypes()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        var hr = await DictionaryApi.CreateOkAsync(admin, "year", "Text", cluster: "hr");
        var finance = await DictionaryApi.CreateOkAsync(admin, "year", "Number", cluster: "finance");
        var none = await DictionaryApi.CreateOkAsync(admin, "year", "Boolean");

        // Every list is in name order, then id (spec 010): the same name in three clusters comes in the order they were created.
        var fields = await DictionaryApi.ListAsync(admin);
        Assert.Equal(
            [(hr, "hr", "Text"), (finance, "finance", "Number"), (none, null, "Boolean")],
            fields.Select(f => (f.Id, f.Cluster, f.Type)).ToArray());
    }

    [Fact] // AC-52
    public async Task TheSameClusterAndNameTwiceIsRefusedAndNoClusterCountsAsOneValue()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        await DictionaryApi.CreateOkAsync(admin, "year", cluster: "hr");
        await DictionaryApi.CreateOkAsync(admin, "region");

        using var again = await DictionaryApi.CreateAsync(admin, "year", "Number", cluster: "hr");
        using var withoutCluster = await DictionaryApi.CreateAsync(admin, "region", "Number");
        using var emptyCluster = await DictionaryApi.CreateAsync(admin, "region", "Number", cluster: "  ");

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("knowledge_base.field_name_taken", await DictionaryApi.ProblemCodeAsync(again));
        Assert.Equal(HttpStatusCode.Conflict, withoutCluster.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, emptyCluster.StatusCode);
    }

    [Theory]
    [InlineData("HR")]
    [InlineData("1hr")]
    [InlineData("hr-docs")]
    [InlineData("a_cluster_name_that_is_longer_than_forty_chars")]
    public async Task AClusterThatIsNotAValidNameIsRefused(string cluster)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        using var response = await DictionaryApi.CreateAsync(admin, "year", cluster: cluster);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("knowledge_base.invalid_field_cluster", await DictionaryApi.ProblemCodeAsync(response));
    }
}
