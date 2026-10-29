using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.KnowledgeBases.Features.Lookups;
using Quorum.Modules.KnowledgeBases.Storage;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBases.Lookups;

/// <summary>A live entity list: switched to the store, its rows leave the platform database and a filter on an attribute is read from the store (spec 012 D-4, D-5).</summary>
public sealed class EntityLiveFilterTests(PostgresFixture postgres, QdrantFixture qdrant)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly object[] SupplierFields =
    [
        new { name = "country", type = "Text" },
        new { name = "contract_count", type = "Number" },
        new { name = "supplier_since", type = "Date" },
    ];

    private static readonly string[] Attributes = ["country", "contract_count", "supplier_since"];

    [Fact] // AC-4
    public async Task AFilterOnAnAttributeOfALiveEntityListIsEvaluatedAgainstTheStore()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        app.LookupComputer.Handler = new LookupComputer(app.Services.GetRequiredService<StoreAdapters>()).ComputeAsync;
        var collection = await qdrant.SeedSharedAsync(SampleCollections.CreateSuppliers);
        var mapping = new
        {
            roles = new { text = "text" },
            fields = Attributes.Select(name => new { path = name, name, entityAttribute = true }).ToArray(),
            entityName = "name",
        };
        var registered = await KnowledgeBaseApi.RegisterAsync(admin, (await qdrant.GetUrlAsync()).ToString(), collection, name: "suppliers", description: "The suppliers of the company.", mapping: mapping, createFields: SupplierFields);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var knowledgeBase = (await registered.Content.ReadFromJsonAsync<KnowledgeBaseApi.RegisteredBody>(Ct))!.Id;
        var lookups = await admin.GetFromJsonAsync<JsonElement>(LookupApi.KnowledgeBaseLookups(knowledgeBase), Ct);
        var entity = lookups.EnumerateArray().Single(l => l.GetProperty("kind").GetString() == "Entity").GetProperty("id").GetGuid();

        using var storage = await admin.PutAsJsonAsync($"/api/knowledge-bases/lookups/{entity}", new { storage = "Live" }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, storage.StatusCode);

        using var response = await admin.GetAsync(LookupApi.Values(entity, "filters=" + Uri.EscapeDataString("{\"contract_count\":{\"min\":3}}")), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var values = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("values").EnumerateArray().ToList();
        Assert.NotEmpty(values);
        Assert.All(values, v => Assert.True(v.GetProperty("attributes").GetProperty("contract_count").GetDouble() >= 3));
    }
}
