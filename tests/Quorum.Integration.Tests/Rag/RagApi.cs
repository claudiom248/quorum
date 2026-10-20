using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.KnowledgeBases;
using Quorum.Integration.Tests.Tools;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.Rag;

/// <summary>Route and body shapes of the rag tool API, as a client would use them, and the setup most rag tests share.</summary>
internal static class RagApi
{
    public static Uri All { get; } = new("/api/tools/rag", UriKind.Relative);

    public static Uri One(Guid id) => new($"/api/tools/rag/{id}", UriKind.Relative);

    public static object OwnTenant { get; } = new { kind = "Tenant" };

    public static object Body(
        string name = "company",
        string description = "Searches the company policies.",
        IEnumerable<Guid>? knowledgeBaseIds = null,
        IEnumerable<Guid>? exposedFieldIds = null,
        object? fixedFilters = null,
        int? topK = null,
        double? scoreThreshold = null,
        int? maxResultCharacters = null,
        object? scope = null,
        string? policy = null) => new
        {
            name,
            description,
            knowledgeBaseIds = knowledgeBaseIds?.ToArray() ?? [],
            exposedFieldIds = exposedFieldIds?.ToArray() ?? [],
            fixedFilters,
            topK,
            scoreThreshold,
            maxResultCharacters,
            scope = scope ?? OwnTenant,
            policy,
        };

    public static async Task<HttpResponseMessage> CreateAsync(HttpClient client, object body) =>
        await client.PostAsJsonAsync(All, body, TestContext.Current.CancellationToken);

    /// <summary>Creates a rag tool that must be accepted and returns it.</summary>
    public static async Task<CreatedBody> CreateOkAsync(HttpClient client, object body)
    {
        using var response = await CreateAsync(client, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await response.Content.ReadFromJsonAsync<CreatedBody>(TestContext.Current.CancellationToken))!;
    }

    public static async Task<DetailBody> GetAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<DetailBody>(One(id), TestContext.Current.CancellationToken))!;

    public static async Task<IReadOnlyList<ListItemBody>> ListAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<ListItemBody>>(All, TestContext.Current.CancellationToken))!;

    /// <summary>Activates the named tools (exposed names) on the default assistant, in addition to nothing else.</summary>
    public static Task ActivateAsync(HttpClient client, params string[] exposedNames) => ToolFixtures.ActivateOnlyAsync(client, exposedNames);

    public sealed record PreviewBody(string Name, string Description, JsonElement InputSchema, int EstimatedTokens)
    {
        /// <summary>The names of the parameters of the generated schema.</summary>
        public string[] Parameters => [.. InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name)];

        public JsonElement Parameter(string name) => InputSchema.GetProperty("properties").GetProperty(name);
    }

    public sealed record CreatedBody(Guid Id, PreviewBody? Definition);

    public sealed record KnowledgeBaseBody(Guid Id, string Name);

    public sealed record ScopeBody(string Kind, List<Guid> TenantIds, List<Guid> AssistantIds);

    public sealed record WarningBody(string Code, string Message);

    public sealed record ListItemBody(Guid Id, string Name, List<KnowledgeBaseBody> KnowledgeBases, ScopeBody Scope, string State, string Policy, bool Shared, List<WarningBody> Warnings);

    public sealed record DetailBody(
        Guid Id,
        string Name,
        string Description,
        List<Guid> KnowledgeBaseIds,
        List<Guid> ExposedFieldIds,
        JsonElement FixedFilters,
        int TopK,
        double? ScoreThreshold,
        int MaxResultCharacters,
        ScopeBody Scope,
        string State,
        string Policy,
        PreviewBody? Definition,
        List<WarningBody> Warnings);

    /// <summary>An application with a tenant, an administrator, the fake embedding model and a Qdrant to register collections from.</summary>
    public sealed class Setup : IAsyncDisposable
    {
        private readonly QdrantFixture _qdrant;

        private Setup(QuorumApp app, HttpClient admin, QdrantFixture qdrant, Guid tenantId, Guid assistantId, string url)
        {
            App = app;
            Admin = admin;
            _qdrant = qdrant;
            TenantId = tenantId;
            AssistantId = assistantId;
            Url = url;
        }

        public QuorumApp App { get; }

        public HttpClient Admin { get; }

        public Guid TenantId { get; }

        public Guid AssistantId { get; }

        public string Url { get; }

        public static async Task<Setup> StartAsync(PostgresFixture postgres, QdrantFixture qdrant)
        {
            var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
            var admin = await app.SetUpAsync();
            KnowledgeBaseApi.ServeEmbeddings(app);
            return new Setup(
                app,
                admin,
                qdrant,
                await TenantScope.TenantIdAsync(app),
                await ToolsApi.DefaultAssistantIdAsync(admin),
                (await qdrant.GetUrlAsync()).ToString());
        }

        /// <summary>Seeds a sample collection under a name of its own and registers it as a knowledge base.</summary>
        public async Task<Guid> RegisterAsync(
            string name,
            Func<string, SampleDataset>? dataset = null,
            string standardName = SampleCollections.Flat,
            object? mapping = null,
            object[]? createFields = null,
            object? scope = null,
            string? description = null,
            string? queryPrefix = null,
            string? url = null)
        {
            var collection = QdrantFixture.NewPrefix() + standardName;
            await _qdrant.SeedAsync([(dataset ?? SampleCollections.CreateFlat)(collection)]);
            using var response = await KnowledgeBaseApi.RegisterAsync(Admin, url ?? Url, collection, name, description ?? $"Policies about {name}", mapping: mapping, createFields: createFields, scope: scope, queryPrefix: queryPrefix);
            Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            return (await response.Content.ReadFromJsonAsync<KnowledgeBaseApi.RegisteredBody>(TestContext.Current.CancellationToken))!.Id;
        }

        /// <summary>The dictionary fields by name.</summary>
        public async Task<IReadOnlyDictionary<string, Guid>> FieldsAsync() =>
            (await DictionaryApi.ListAsync(Admin)).ToDictionary(f => f.Name, f => f.Id);

        public async ValueTask DisposeAsync()
        {
            Admin.Dispose();
            await App.DisposeAsync();
        }
    }
}
