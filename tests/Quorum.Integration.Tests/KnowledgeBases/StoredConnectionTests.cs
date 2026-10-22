using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.KnowledgeBases.Search;
using Quorum.Integration.Tests.Tools;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBases;

/// <summary>
/// The connection of a knowledge base is one jsonb value per row, read and written whole (spec 008 D-1, A1): rows written before
/// the connection became kind-specific keep their shape and need no data migration.
/// </summary>
public sealed class StoredConnectionTests(PostgresFixture postgres, QdrantFixture qdrant)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Text(ulong chunk) => SampleCollections.CreateFlat().Points.Single(p => p.Id == chunk).EmbeddingText;

    private static Task<string> StoredAsync(QuorumApp app, Guid id) =>
        ToolFixtures.ScalarAsync<string>(app, $"SELECT \"Connection\"::text FROM knowledge_base.knowledge_bases WHERE \"Id\" = '{id}'");

    [Fact]
    public async Task AQdrantConnectionIsStoredWithTheMembersOfAlways()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var collection = await qdrant.SeedSharedAsync(SampleCollections.CreateNamedVectors);
        var url = await qdrant.GetUrlAsync();

        var plain = await KnowledgeBaseApi.RegisterOkAsync(admin, url.ToString(), collection, "plain", vectorName: SampleCollections.DenseVectorName, mapping: KnowledgeBaseApi.TextOnly(), createFields: []);
        var keyed = await KnowledgeBaseApi.RegisterOkAsync(admin, url.ToString(), collection, "keyed", apiKey: "a-key", vectorName: SampleCollections.DenseVectorName, mapping: KnowledgeBaseApi.TextOnly(), createFields: []);

        var withoutKey = JsonNode.Parse(await StoredAsync(app, plain.Id))!.AsObject();
        var withKey = JsonNode.Parse(await StoredAsync(app, keyed.Id))!.AsObject();
        Assert.Equal(["Url", "Collection", "VectorName", "ProtectedApiKey"], withoutKey.Select(p => p.Key));
        Assert.Equal(url.AbsoluteUri, withoutKey["Url"]!.GetValue<string>());
        Assert.Equal(collection, withoutKey["Collection"]!.GetValue<string>());
        Assert.Equal(SampleCollections.DenseVectorName, withoutKey["VectorName"]!.GetValue<string>());
        Assert.Null(withoutKey["ProtectedApiKey"]);
        Assert.NotNull(withKey["ProtectedApiKey"]);
        Assert.DoesNotContain("a-key", withKey.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARowWrittenInTheOldShapeIsReadVerifiedAndSearched()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var collection = await qdrant.SeedSharedAsync(SampleCollections.CreateFlat);
        var url = await qdrant.GetUrlAsync();
        var registered = await KnowledgeBaseApi.RegisterOkAsync(admin, url.ToString(), collection);

        // What a row looked like before the connection became kind-specific, written by hand.
        var oldShape = new JsonObject
        {
            ["Url"] = url.AbsoluteUri,
            ["Collection"] = collection,
            ["VectorName"] = null,
            ["ProtectedApiKey"] = null,
        };
        await ToolFixtures.ExecuteAsync(
            app,
            $"UPDATE knowledge_base.knowledge_bases SET \"Connection\" = '{oldShape.ToJsonString()}'::jsonb, \"Status\" = 'Unavailable' WHERE \"Id\" = '{registered.Id}'");

        var detail = await KnowledgeBaseApi.GetAsync(admin, registered.Id);
        Assert.Equal(new KnowledgeBaseApi.ConnectionBody(url.AbsoluteUri, collection, null, false), detail.Connection);

        var verified = await KnowledgeBaseApi.VerifyOkAsync(admin, registered.Id);
        Assert.Equal("Available", verified.Status);
        Assert.Empty(verified.Warnings);

        var found = await SearchApi.SearchOkAsync(admin, registered.Id, Text(4), limit: 1);
        Assert.Equal(Text(4), Assert.Single(found.Results).Text);

        // Saving it again writes the same members.
        using var update = await admin.PutAsJsonAsync(KnowledgeBaseApi.One(registered.Id), KnowledgeBaseApi.Body(url.ToString(), collection, mapping: KnowledgeBaseApi.TextOnly(), createFields: []), Ct);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var rewritten = JsonNode.Parse(await StoredAsync(app, registered.Id))!.AsObject();
        Assert.Equal(["Url", "Collection", "VectorName", "ProtectedApiKey"], rewritten.Select(p => p.Key));
    }
}
