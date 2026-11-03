using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.LookupTools;
using Quorum.Integration.Tests.Rag;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBases.Lookups;

/// <summary>A knowledge base of entities only, with no text role (spec 003 Amendment 3, AC-A3-1 to AC-A3-4).</summary>
public sealed class EntityOnlyKnowledgeBaseTests(SharedQuorumApp host, QdrantFixture qdrant) : IClassFixture<SharedQuorumApp>, IAsyncLifetime
{
    public ValueTask InitializeAsync() => new(host.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The entities are named by <c>title</c>, and nothing is mapped as their text.</summary>
    public static readonly object Mapping = new
    {
        roles = new { title = "title" },
        fields = Array.Empty<object>(),
        entityName = "title",
    };

    [Fact] // AC-A3-1
    public async Task AMappingWithAnEntityNameAndNoTextIsAccepted()
    {
        await using var setup = await RagApi.Setup.OverAsync(host, qdrant);

        var knowledgeBase = await setup.RegisterAsync("vendors", mapping: Mapping);

        var detail = await KnowledgeBaseApi.GetAsync(setup.Admin, knowledgeBase);
        Assert.Null(detail.Mapping!.Roles.Text);
        Assert.Equal("title", detail.Mapping.EntityName);
    }

    [Fact] // AC-A3-2
    public async Task AMappingWithNeitherTextNorAnEntityNameIsRefused()
    {
        await using var setup = await RagApi.Setup.OverAsync(host, qdrant);
        var collection = await qdrant.SeedSharedAsync(SampleCollections.CreateFlat);

        using var response = await KnowledgeBaseApi.RegisterAsync(setup.Admin, setup.Url, collection, "nothing", mapping: new { roles = new { title = "title" }, fields = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("knowledge_base.invalid_mapping", await LookupToolApi.ProblemCodeAsync(response));
    }

    [Fact] // AC-A3-3
    public async Task ARagToolRefusesAKnowledgeBaseWithNoTextToSearch()
    {
        await using var setup = await RagApi.Setup.OverAsync(host, qdrant);
        var knowledgeBase = await setup.RegisterAsync("vendors", mapping: Mapping);

        using var response = await RagApi.CreateAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [knowledgeBase]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("rag.knowledge_base_not_searchable", await LookupToolApi.ProblemCodeAsync(response));
    }

    [Fact] // AC-A3-4
    public async Task VerifyingAnEntityKnowledgeBaseReportsNoMissingText()
    {
        await using var setup = await RagApi.Setup.OverAsync(host, qdrant);
        var knowledgeBase = await setup.RegisterAsync("vendors", mapping: Mapping);

        using var response = await setup.Admin.PostAsync(new Uri($"/api/knowledge-bases/{knowledgeBase}/verify", UriKind.Relative), null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.DoesNotContain(body.GetProperty("warnings").EnumerateArray(), w => w.TryGetProperty("path", out var path) && path.GetString() == "text");
    }
}
