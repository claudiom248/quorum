using System.Net;
using System.Text.Json;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.Rag;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBases;

/// <summary>Mapping payload fields to dictionary fields identified by cluster and name (spec 003, Amendment 1, AC-53).</summary>
public sealed class FieldClusterMappingTests(PostgresFixture postgres, QdrantFixture qdrant)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Roles { get; } = new { text = "text" };

    [Fact] // AC-53
    public async Task APayloadNameThatSeveralClustersHavePreselectsNoneAndListsTheCandidates()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var hr = await DictionaryApi.CreateOkAsync(setup.Admin, "department", cluster: "hr");
        var legal = await DictionaryApi.CreateOkAsync(setup.Admin, "department", cluster: "legal");
        var finance = await DictionaryApi.CreateOkAsync(setup.Admin, "year", "Number", cluster: "finance");
        var collection = await qdrant.SeedSharedAsync(SampleCollections.CreateFlat);

        var report = await InspectApi.InspectOkAsync(setup.Admin, new Uri(setup.Url), collection);

        var department = report.DraftMapping.Fields.Single(f => f.Name == "department");
        Assert.Null(department.DictionaryFieldId);
        Assert.Equal(new[] { hr, legal }.Order(), department.Candidates!.Select(c => c.Id).Order());
        Assert.Equal(["hr", "legal"], department.Candidates!.Select(c => c.Cluster).Order());
        Assert.DoesNotContain(report.ProposedFields, p => p.Name == "department");

        // A name only one field has is preselected, with its cluster.
        var year = report.DraftMapping.Fields.Single(f => f.Name == "year");
        Assert.Equal(finance, year.DictionaryFieldId);
        Assert.Equal("finance", year.Cluster);
        Assert.Empty(year.Candidates ?? new List<InspectApi.CandidateBody>());
        Assert.Empty(year.Issues);
    }

    [Fact] // AC-53
    public async Task ChoosingACandidateByIdOrByClusterAndNameSavesWithoutATypeConflict()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var hr = await DictionaryApi.CreateOkAsync(setup.Admin, "department", cluster: "hr");
        await DictionaryApi.CreateOkAsync(setup.Admin, "department", cluster: "legal");
        var collection = await qdrant.SeedSharedAsync(SampleCollections.CreateFlat);

        var byId = await KnowledgeBaseApi.RegisterOkAsync(
            setup.Admin,
            setup.Url,
            collection,
            "by-id",
            mapping: new { roles = Roles, fields = new[] { new { path = "department", dictionaryFieldId = hr } } },
            createFields: []);
        var byName = await KnowledgeBaseApi.RegisterOkAsync(
            setup.Admin,
            setup.Url,
            collection,
            "by-name",
            mapping: new { roles = Roles, fields = new[] { new { path = "department", cluster = "legal", name = "department" } } },
            createFields: []);

        Assert.Equal("Available", byId.Status);
        Assert.Equal("Available", byName.Status);
        Assert.Equal("hr", await ClusterOfMappedFieldAsync(setup.Admin, byId.Id));
        Assert.Equal("legal", await ClusterOfMappedFieldAsync(setup.Admin, byName.Id));
    }

    [Fact] // AC-52
    public async Task AFieldToCreateCarriesItsClusterAndAMissingClusterIsNotMatchedByName()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        await DictionaryApi.CreateOkAsync(setup.Admin, "year", "Number", cluster: "finance");
        var collection = await qdrant.SeedSharedAsync(SampleCollections.CreateFlat);
        var fields = new[] { new { path = "year", cluster = "hr", name = "year" } };

        // `hr/year` does not exist and the administrator did not confirm it: the field of another cluster is not taken for it.
        using var unconfirmed = await KnowledgeBaseApi.RegisterAsync(setup.Admin, setup.Url, collection, "unconfirmed", mapping: new { roles = Roles, fields }, createFields: []);
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        Assert.Equal("knowledge_base.invalid_mapping", await KnowledgeBaseApi.ProblemCodeAsync(unconfirmed));

        var created = await KnowledgeBaseApi.RegisterOkAsync(
            setup.Admin,
            setup.Url,
            collection,
            "confirmed",
            mapping: new { roles = Roles, fields },
            createFields: [new { name = "year", type = "Number", cluster = "hr" }]);

        Assert.Equal("Available", created.Status);
        Assert.Equal(["finance", "hr"], (await DictionaryApi.ListAsync(setup.Admin)).Select(f => f.Cluster).Order());
    }

    [Fact]
    public async Task AClusterThatIsNotAValidNameIsRefusedInAFieldToCreate()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var collection = await qdrant.SeedSharedAsync(SampleCollections.CreateFlat);

        using var response = await KnowledgeBaseApi.RegisterAsync(
            setup.Admin,
            setup.Url,
            collection,
            "bad-cluster",
            mapping: new { roles = Roles, fields = new[] { new { path = "year", cluster = "Not Valid", name = "year" } } },
            createFields: [new { name = "year", type = "Number", cluster = "Not Valid" }]);

        Assert.Equal("knowledge_base.invalid_field_cluster", await KnowledgeBaseApi.ProblemCodeAsync(response));
    }

    private static async Task<string?> ClusterOfMappedFieldAsync(HttpClient admin, Guid id)
    {
        using var document = JsonDocument.Parse(await KnowledgeBaseApi.GetRawAsync(admin, id));
        var field = document.RootElement.GetProperty("mapping").GetProperty("fields")[0];
        return field.TryGetProperty("cluster", out var cluster) && cluster.ValueKind == JsonValueKind.String ? cluster.GetString() : null;
    }
}
