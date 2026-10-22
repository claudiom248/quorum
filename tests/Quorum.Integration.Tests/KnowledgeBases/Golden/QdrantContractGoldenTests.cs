using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.KnowledgeBases.Search;
using Quorum.Integration.Tests.Tools;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBases.Golden;

/// <summary>
/// Pins the JSON of inspect, registration, detail, verification and search for a Qdrant knowledge base (spec 008 AC-1). The
/// expectations were written before Qdrant moved behind the per-kind contract and must not change with it: a change here is a
/// change of what a client sees.
/// </summary>
public sealed class QdrantContractGoldenTests(PostgresFixture postgres, QdrantFixture qdrant)
{
    private const string Secret = "s3cret-qdrant-key-golden";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Text(ulong chunk) => SampleCollections.CreateFlat().Points.Single(p => p.Id == chunk).EmbeddingText;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly string[] Hr = ["hr"];
    private static readonly string[] Finance = ["finance"];

    private async Task<GoldenFile> NewGoldenAsync(string name)
    {
        var plain = await qdrant.GetUrlAsync();
        var keyed = await qdrant.GetUrlAsync(Secret);
        return new GoldenFile(name)
            .Replace(keyed.AbsoluteUri, "<keyed-url>")
            .Replace(keyed.ToString(), "<keyed-url>")
            .Replace(plain.AbsoluteUri, "<url>")
            .Replace(plain.ToString(), "<url>")
            // Qdrant sends the named vectors of a collection in no fixed order, and the messages list them as they come.
            .Replace("'summary', 'dense'", "'dense', 'summary'");
    }

    private static Task<HttpResponseMessage> InspectAsync(HttpClient client, object connection, string? kind = "Qdrant", string? model = InspectApi.EmbeddingModel, object? schema = null) =>
        client.PostAsJsonAsync(InspectApi.Inspect, new { kind, connection, embeddingModel = model, queryPrefix = (string?)null, schema }, Ct);

    private static object Connection(string url, string? collection, string? apiKey = null, string? vectorName = null) => new { url, apiKey, collection, vectorName };

    [Fact]
    public async Task InspectingACollectionAnswersAsItAlwaysDid()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        InspectApi.ServeEmbeddings(app);
        var prefix = QdrantFixture.NewPrefix();
        await qdrant.SeedAsync(
        [
            SampleCollections.CreateFlat(prefix + SampleCollections.Flat),
            SampleCollections.CreateNested(prefix + SampleCollections.Nested),
            SampleCollections.CreateTags(prefix + SampleCollections.Tags),
            SampleCollections.CreateUnindexed(prefix + SampleCollections.Unindexed),
            SampleCollections.CreateNamedVectors(prefix + SampleCollections.NamedVectors),
            SampleCollections.CreateOtherVectorSize(prefix + SampleCollections.OtherVectorSize),
            SampleCollections.CreateEmpty(prefix + SampleCollections.Empty),
        ]);
        await qdrant.SeedAsync([SampleCollections.CreateFlat(prefix + SampleCollections.Flat)], Secret);
        var url = (await qdrant.GetUrlAsync()).ToString();
        var golden = (await NewGoldenAsync("inspect")).Replace(prefix, "<prefix>");

        async Task RecordAsync(string scenario, string collection, string? vectorName = null, string? model = InspectApi.EmbeddingModel, object? schema = null, string? apiKey = null, string? atUrl = null)
        {
            using var response = await InspectAsync(admin, Connection(atUrl ?? url, prefix + collection, apiKey, vectorName), model: model, schema: schema);
            await golden.RecordAsync(scenario, response);
        }

        await RecordAsync("flat", SampleCollections.Flat);
        await RecordAsync("nested", SampleCollections.Nested);
        await RecordAsync("tags", SampleCollections.Tags);
        await RecordAsync("unindexed", SampleCollections.Unindexed);
        await RecordAsync("named-vectors-none-chosen", SampleCollections.NamedVectors);
        await RecordAsync("named-vectors-chosen", SampleCollections.NamedVectors, vectorName: SampleCollections.DenseVectorName);
        await RecordAsync("named-vectors-unknown", SampleCollections.NamedVectors, vectorName: "nope");
        await RecordAsync("other-vector-size", SampleCollections.OtherVectorSize);
        await RecordAsync("empty", SampleCollections.Empty);
        await RecordAsync("missing-collection", "not_there");
        await RecordAsync("model-not-served", SampleCollections.Flat, model: "not-served");
        await RecordAsync(
            "pasted-schema",
            SampleCollections.Flat,
            schema: JsonDocument.Parse("""{"type":"object","properties":{"text":{"type":"string"},"year":{"type":"integer"},"tags":{"type":"array","items":{"type":"string"}}}}""").RootElement);
        await RecordAsync("invalid-schema", SampleCollections.Flat, schema: "{not json");
        await RecordAsync("unreachable", "docs", atUrl: "http://[::1]:1");
        await RecordAsync("wrong-key", SampleCollections.Flat, apiKey: "not-the-key", atUrl: (await qdrant.GetUrlAsync(Secret)).ToString());
        await RecordAsync("right-key", SampleCollections.Flat, apiKey: Secret, atUrl: (await qdrant.GetUrlAsync(Secret)).ToString());

        using (var unsupported = await InspectAsync(admin, Connection(url, "docs"), kind: "Pinecone"))
        {
            await golden.RecordAsync("unsupported-kind", unsupported);
        }

        using (var noKind = await InspectAsync(admin, Connection(url, "docs"), kind: null))
        {
            await golden.RecordAsync("no-kind", noKind);
        }

        foreach (var (scenario, connection) in new (string, object?)[]
        {
            ("no-connection", null),
            ("no-url", Connection(string.Empty, "docs")),
            ("relative-url", Connection("qdrant:6334", "docs")),
            ("ftp-url", Connection("ftp://qdrant:6334", "docs")),
            ("url-with-credentials", Connection("http://user:pass@qdrant:6334", "docs")),
            ("no-collection", Connection(url, null)),
            ("blank-collection", Connection(url, "   ")),
            ("non-ascii-key", Connection(url, "docs", apiKey: "key-with-é-in-it")),
        })
        {
            using var response = await InspectAsync(admin, connection!);
            await golden.RecordAsync(scenario, response);
        }

        using (var noModel = await InspectAsync(admin, Connection(url, "docs"), model: null))
        {
            await golden.RecordAsync("no-model", noModel);
        }

        golden.Verify();
    }

    [Fact]
    public async Task RegisteringAndReadingAKnowledgeBaseAnswersAsItAlwaysDid()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var prefix = QdrantFixture.NewPrefix();
        await qdrant.SeedAsync(
        [
            SampleCollections.CreateFlat(prefix + SampleCollections.Flat),
            SampleCollections.CreateNamedVectors(prefix + SampleCollections.NamedVectors),
            SampleCollections.CreateOtherVectorSize(prefix + SampleCollections.OtherVectorSize),
        ]);
        await qdrant.SeedAsync([SampleCollections.CreateFlat(prefix + SampleCollections.Flat)], Secret);
        var url = (await qdrant.GetUrlAsync()).ToString();
        var keyedUrl = (await qdrant.GetUrlAsync(Secret)).ToString();
        var golden = (await NewGoldenAsync("registry")).Replace(prefix, "<prefix>");

        // The first registration creates the dictionary fields the rest reuse.
        using (var created = await KnowledgeBaseApi.RegisterAsync(admin, url, prefix + SampleCollections.Flat))
        {
            await golden.RecordAsync("create-flat", created);
            var id = (await created.Content.ReadFromJsonAsync<KnowledgeBaseApi.RegisteredBody>(Ct))!.Id;
            using var detail = await admin.GetAsync(KnowledgeBaseApi.One(id), Ct);
            await golden.RecordAsync("detail-flat", detail);
        }

        async Task RecordRegisterAsync(string scenario, string collection, string name, string? atUrl = null, string? apiKey = null, string? vectorName = null, object? mapping = null, object[]? createFields = null, object? scope = null)
        {
            using var response = await KnowledgeBaseApi.RegisterAsync(
                admin,
                atUrl ?? url,
                collection,
                name,
                apiKey: apiKey,
                vectorName: vectorName,
                mapping: mapping ?? KnowledgeBaseApi.TextOnly(),
                createFields: createFields ?? [],
                scope: scope);
            await golden.RecordAsync(scenario, response);
        }

        await RecordRegisterAsync("create-name-taken", prefix + SampleCollections.Flat, "policies");
        await RecordRegisterAsync("create-text-only", prefix + SampleCollections.Flat, "text-only");
        await RecordRegisterAsync("create-vector-required", prefix + SampleCollections.NamedVectors, "vector-required");
        await RecordRegisterAsync("create-vector-unknown", prefix + SampleCollections.NamedVectors, "vector-unknown", vectorName: "nope");
        await RecordRegisterAsync("create-vector-chosen", prefix + SampleCollections.NamedVectors, "vector-chosen", vectorName: SampleCollections.DenseVectorName);
        await RecordRegisterAsync("create-other-vector-size", prefix + SampleCollections.OtherVectorSize, "other-size");
        await RecordRegisterAsync("create-missing-collection", prefix + "not_there", "missing");
        await RecordRegisterAsync("create-unreachable", "docs", "unreachable", atUrl: "http://[::1]:1");
        await RecordRegisterAsync("create-wrong-key", prefix + SampleCollections.Flat, "wrong-key", atUrl: keyedUrl, apiKey: "not-the-key");
        await RecordRegisterAsync("create-with-key", prefix + SampleCollections.Flat, "with-key", atUrl: keyedUrl, apiKey: Secret);
        await RecordRegisterAsync("create-invalid-name", prefix + SampleCollections.Flat, "Not Valid");
        await RecordRegisterAsync("create-invalid-scope", prefix + SampleCollections.Flat, "bad-scope", scope: new { kind = "Everyone" });
        await RecordRegisterAsync("create-agent-scope", prefix + SampleCollections.Flat, "agent-scope", scope: new { kind = "Agent" });
        await RecordRegisterAsync("create-no-text-role", prefix + SampleCollections.Flat, "no-text", mapping: new { roles = new { title = "title" }, fields = Array.Empty<object>() });
        await RecordRegisterAsync("create-role-path-twice", prefix + SampleCollections.Flat, "twice", mapping: new { roles = new { text = "text", title = "text" }, fields = Array.Empty<object>() });
        await RecordRegisterAsync(
            "create-values-do-not-fit",
            prefix + SampleCollections.Flat,
            "misfit",
            mapping: new { roles = new { text = "text" }, fields = new[] { new { path = "department", name = "year" } } });
        await RecordRegisterAsync("create-field-not-in-dictionary", prefix + SampleCollections.Flat, "unknown-field", mapping: new { roles = new { text = "text" }, fields = new[] { new { path = "department", name = "nothing_like_it" } } });

        using (var list = await admin.GetAsync(KnowledgeBaseApi.All, Ct))
        {
            await golden.RecordAsync("list", list);
        }

        // An update that does not send the key keeps the stored one, but only for the server it was saved for.
        var withKey = (await KnowledgeBaseApi.ListAsync(admin)).Single(k => k.Name == "with-key").Id;
        async Task RecordUpdateAsync(string scenario, string atUrl, string collection, string? apiKey = null)
        {
            using var response = await admin.PutAsJsonAsync(
                KnowledgeBaseApi.One(withKey),
                KnowledgeBaseApi.Body(atUrl, collection, "with-key", apiKey: apiKey, mapping: KnowledgeBaseApi.TextOnly(), createFields: []),
                Ct);
            await golden.RecordAsync(scenario, response);
        }

        await RecordUpdateAsync("update-keeps-the-key", keyedUrl, prefix + SampleCollections.Flat);
        using (var detail = await admin.GetAsync(KnowledgeBaseApi.One(withKey), Ct))
        {
            await golden.RecordAsync("detail-with-key", detail);
        }

        await RecordUpdateAsync("update-other-server-needs-the-key", "http://other.test:6334", prefix + SampleCollections.Flat);
        await RecordUpdateAsync("update-other-server-with-the-key", "http://[::1]:1", "docs", apiKey: Secret);
        await RecordUpdateAsync("update-back-to-the-server-with-a-new-key", keyedUrl, prefix + SampleCollections.Flat, apiKey: Secret);

        using (var missing = await admin.PutAsJsonAsync(KnowledgeBaseApi.One(Guid.NewGuid()), KnowledgeBaseApi.Body(url, prefix + SampleCollections.Flat), Ct))
        {
            await golden.RecordAsync("update-unknown", missing);
        }

        using (var missing = await admin.GetAsync(KnowledgeBaseApi.One(Guid.NewGuid()), Ct))
        {
            await golden.RecordAsync("detail-unknown", missing);
        }

        golden.Verify();
    }

    [Fact]
    public async Task VerifyingAKnowledgeBaseAnswersAsItAlwaysDid()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var prefix = QdrantFixture.NewPrefix();
        await qdrant.SeedAsync(
        [
            SampleCollections.CreateFlat(prefix + SampleCollections.Flat),
            SampleCollections.CreateFlat(prefix + "dropped"),
            SampleCollections.CreateNamedVectors(prefix + SampleCollections.NamedVectors),
            SampleCollections.CreateOtherVectorSize(prefix + SampleCollections.OtherVectorSize),
        ]);
        await qdrant.SeedAsync([SampleCollections.CreateFlat(prefix + SampleCollections.Flat)], Secret);
        var url = (await qdrant.GetUrlAsync()).ToString();
        var keyedUrl = (await qdrant.GetUrlAsync(Secret)).ToString();
        var golden = (await NewGoldenAsync("verify")).Replace(prefix, "<prefix>");

        async Task<Guid> RegisterAsync(string name, string atUrl, string collection, string? apiKey = null, string? vectorName = null, object? mapping = null, object[]? createFields = null)
        {
            using var response = await KnowledgeBaseApi.RegisterAsync(admin, atUrl, collection, name, apiKey: apiKey, vectorName: vectorName, mapping: mapping, createFields: createFields);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<KnowledgeBaseApi.RegisteredBody>(Ct))!.Id;
        }

        async Task VerifyAsync(string scenario, Guid id)
        {
            using var response = await admin.PostAsync(KnowledgeBaseApi.Verify(id), content: null, Ct);
            await golden.RecordAsync(scenario, response);
            using var detail = await admin.GetAsync(KnowledgeBaseApi.One(id), Ct);
            await golden.RecordAsync(scenario + "-detail", detail);
        }

        var flat = await RegisterAsync("flat", url, prefix + SampleCollections.Flat);
        await VerifyAsync("available", flat);

        var textOnly = KnowledgeBaseApi.TextOnly();
        var dropped = await RegisterAsync("dropped", url, prefix + "dropped", mapping: textOnly, createFields: []);
        using (var rest = new HttpClient { BaseAddress = await qdrant.GetRestUrlAsync() })
        {
            using var deleted = await rest.DeleteAsync($"collections/{prefix}dropped", Ct);
            deleted.EnsureSuccessStatusCode();
        }

        await VerifyAsync("collection-dropped", dropped);

        var otherSize = await RegisterAsync("other-size", url, prefix + SampleCollections.OtherVectorSize, mapping: textOnly, createFields: []);
        await VerifyAsync("dimension-mismatch", otherSize);

        var unreachable = await RegisterAsync("unreachable", "http://[::1]:1", "docs", mapping: textOnly, createFields: []);
        await VerifyAsync("unreachable", unreachable);

        var wrongKey = await RegisterAsync("wrong-key", keyedUrl, prefix + SampleCollections.Flat, apiKey: "not-the-key", mapping: textOnly, createFields: []);
        await VerifyAsync("wrong-key", wrongKey);

        var chosen = await RegisterAsync("named", url, prefix + SampleCollections.NamedVectors, vectorName: SampleCollections.DenseVectorName, mapping: textOnly, createFields: []);
        await VerifyAsync("named-vector", chosen);

        // A mapped path that none of the sampled points has only warns.
        var moved = await RegisterAsync(
            "moved",
            url,
            prefix + SampleCollections.Flat,
            mapping: new { roles = new { text = "text", title = "no_such_title" }, fields = Array.Empty<object>() },
            createFields: []);
        await VerifyAsync("mapped-path-missing", moved);

        var keyed = await RegisterAsync("keyed", keyedUrl, prefix + SampleCollections.Flat, apiKey: Secret, mapping: textOnly, createFields: []);
        await VerifyAsync("keyed", keyed);

        // The installation's key ring was replaced: the stored key cannot be read any more.
        await ToolFixtures.ExecuteAsync(
            app,
            $"UPDATE knowledge_base.knowledge_bases SET \"Connection\" = jsonb_set(\"Connection\", '{{ProtectedApiKey}}', '\"unreadable\"') WHERE \"Id\" = '{keyed}'");
        await VerifyAsync("key-unreadable", keyed);

        using (var unknown = await admin.PostAsync(KnowledgeBaseApi.Verify(Guid.NewGuid()), content: null, Ct))
        {
            await golden.RecordAsync("unknown", unknown);
        }

        golden.Verify();
    }

    [Fact]
    public async Task SearchingAKnowledgeBaseAnswersAsItAlwaysDid()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var tenantId = await TenantScope.TenantIdAsync(app);
        var assistantId = await SearchApi.AssistantIdAsync(app);
        var prefix = QdrantFixture.NewPrefix();
        await qdrant.SeedAsync(
        [
            SampleCollections.CreateFlat(prefix + SampleCollections.Flat),
            SampleCollections.CreateFlat(prefix + "dropped"),
            SampleCollections.CreateNested(prefix + SampleCollections.Nested),
            SampleCollections.CreateOtherVectorSize(prefix + SampleCollections.OtherVectorSize),
            SampleCollections.CreateEmpty(prefix + SampleCollections.Empty),
        ]);
        await qdrant.SeedAsync([SampleCollections.CreateFlat(prefix + SampleCollections.Flat)], Secret);
        var url = (await qdrant.GetUrlAsync()).ToString();
        var keyedUrl = (await qdrant.GetUrlAsync(Secret)).ToString();
        var golden = (await NewGoldenAsync("search")).Replace(prefix, "<prefix>");
        var textOnly = KnowledgeBaseApi.TextOnly();

        async Task<Guid> RegisterAsync(string name, string atUrl, string collection, string? apiKey = null, object? mapping = null, object[]? createFields = null)
        {
            using var response = await KnowledgeBaseApi.RegisterAsync(admin, atUrl, collection, name, apiKey: apiKey, mapping: mapping, createFields: createFields);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<KnowledgeBaseApi.RegisteredBody>(Ct))!.Id;
        }

        async Task TestSearchAsync(string scenario, Guid id, string? query, object? filters = null, int? limit = null)
        {
            using var response = await SearchApi.PostAsync(admin, id, query, filters, limit);
            await golden.RecordAsync(scenario, response);
        }

        var flat = await RegisterAsync("flat", url, prefix + SampleCollections.Flat);
        await TestSearchAsync("test-search", flat, Text(4), limit: 3);
        await TestSearchAsync("test-search-default-limit", flat, Text(1));
        await TestSearchAsync("test-search-text-filter", flat, Text(1), JsonDocument.Parse("""{"department": ["hr", "legal"]}""").RootElement, limit: 20);
        await TestSearchAsync("test-search-ranges", flat, Text(1), JsonDocument.Parse("""{"year_min": 2024, "year_max": 2024, "published_on_from": "2024-01-01"}""").RootElement, limit: 20);
        await TestSearchAsync("test-search-boolean", flat, Text(1), JsonDocument.Parse("""{"confidential": true}""").RootElement, limit: 20);
        await TestSearchAsync("test-search-invalid-filter", flat, Text(1), JsonDocument.Parse("""{"year_min": "recent"}""").RootElement);
        await TestSearchAsync("test-search-unknown-filter", flat, Text(1), JsonDocument.Parse("""{"nothing": 1}""").RootElement);
        await TestSearchAsync("test-search-no-query", flat, null);
        await TestSearchAsync("test-search-limit-out-of-range", flat, Text(1), limit: 21);
        await TestSearchAsync("test-search-unknown-knowledge-base", Guid.NewGuid(), Text(1));

        var empty = await RegisterAsync("empty", url, prefix + SampleCollections.Empty, mapping: textOnly, createFields: []);
        await TestSearchAsync("test-search-empty-collection", empty, "anything");

        var dropped = await RegisterAsync("dropped", url, prefix + "dropped", mapping: textOnly, createFields: []);
        using (var rest = new HttpClient { BaseAddress = await qdrant.GetRestUrlAsync() })
        {
            using var deleted = await rest.DeleteAsync($"collections/{prefix}dropped", Ct);
            deleted.EnsureSuccessStatusCode();
        }

        await TestSearchAsync("test-search-collection-dropped", dropped, "anything");

        var otherSize = await RegisterAsync("other-size", url, prefix + SampleCollections.OtherVectorSize, mapping: textOnly, createFields: []);
        await TestSearchAsync("test-search-dimension-mismatch", otherSize, "anything");

        var unreachable = await RegisterAsync("unreachable", "http://[::1]:1", "docs", mapping: textOnly, createFields: []);
        await TestSearchAsync("test-search-unreachable", unreachable, "anything");

        var wrongKey = await RegisterAsync("wrong-key", keyedUrl, prefix + SampleCollections.Flat, apiKey: "not-the-key", mapping: textOnly, createFields: []);
        await TestSearchAsync("test-search-wrong-key", wrongKey, "anything");

        var keyed = await RegisterAsync("keyed", keyedUrl, prefix + SampleCollections.Flat, apiKey: Secret, mapping: textOnly, createFields: []);
        await TestSearchAsync("test-search-with-key", keyed, Text(2), limit: 2);
        await ToolFixtures.ExecuteAsync(
            app,
            $"UPDATE knowledge_base.knowledge_bases SET \"Connection\" = jsonb_set(\"Connection\", '{{ProtectedApiKey}}', '\"unreadable\"') WHERE \"Id\" = '{keyed}'");
        await TestSearchAsync("test-search-key-unreadable", keyed, "anything");

        var nested = await RegisterAsync("nested", url, prefix + SampleCollections.Nested, mapping: SearchApi.NestedMapping, createFields: []);

        // The search the rag tool of the Agents module calls.
        async Task RecordRagAsync(string scenario, Guid[] ids, string query, object? filters = null, object? fixedFilters = null, int topK = 5, double? scoreThreshold = null, int? maxResultCharacters = null)
        {
            var result = await SearchApi.RunAsync(app, tenantId, SearchApi.Request(tenantId, assistantId, query, ids, filters, fixedFilters, topK, scoreThreshold, maxResultCharacters));
            golden.Record(
                scenario,
                result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.UnprocessableEntity,
                result.IsSuccess
                    ? JsonSerializer.SerializeToNode(result.Value, Web)
                    : new JsonObject { ["code"] = result.Error.Code, ["description"] = result.Error.Description, ["type"] = result.Error.Type.ToString() });
        }

        await RecordRagAsync("rag-one", [flat], Text(7));
        await RecordRagAsync("rag-interleaved", [nested, flat], Text(1), topK: 6);
        await RecordRagAsync("rag-threshold", [flat], Text(4), scoreThreshold: 0.99);
        await RecordRagAsync("rag-cap", [flat], Text(4), maxResultCharacters: 200);
        await RecordRagAsync("rag-cap-too-small", [flat], Text(4), maxResultCharacters: 1);
        await RecordRagAsync("rag-filters", [flat, nested], Text(1), filters: new { department = Hr, year_min = 2024 }, topK: 20);
        await RecordRagAsync("rag-fixed-filter", [flat], Text(1), filters: new { department = Finance }, fixedFilters: new { department = Hr }, topK: 20);
        await RecordRagAsync("rag-skipped-for-a-filter", [keyed, flat], Text(1), filters: new { year_min = 2024 }, topK: 20);
        await RecordRagAsync("rag-partial-failure", [flat, unreachable], Text(1));
        await RecordRagAsync("rag-all-fail-one", [unreachable], "anything");
        await RecordRagAsync("rag-all-fail-several", [unreachable, otherSize, wrongKey, dropped], "anything");
        await RecordRagAsync("rag-key-unreadable", [keyed], "anything");
        await RecordRagAsync("rag-invalid-filter", [flat], Text(1), filters: new { year_min = "recent" });
        await RecordRagAsync("rag-invalid-fixed-filter", [flat], Text(1), fixedFilters: new { year_min = "recent" });
        await RecordRagAsync("rag-no-filter-fits", [keyed], Text(1), filters: new { year_min = 2024 });
        await RecordRagAsync("rag-unknown-knowledge-base", [Guid.NewGuid()], Text(1));
        await RecordRagAsync("rag-empty-query", [flat], "   ");
        await RecordRagAsync("rag-top-k-out-of-range", [flat], Text(1), topK: 21);

        // A failure marks the knowledge base unavailable, a search that works brings it back.
        using (var list = await admin.GetAsync(KnowledgeBaseApi.All, Ct))
        {
            await golden.RecordAsync("list-after-the-failures", list);
        }

        golden.Verify();
    }
}
