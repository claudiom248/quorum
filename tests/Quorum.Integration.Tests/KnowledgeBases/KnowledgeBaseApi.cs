using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.KnowledgeBases;

/// <summary>Route and body shapes of the knowledge base registry API, as a client would use them.</summary>
internal static class KnowledgeBaseApi
{
    public const string EmbeddingModel = InspectApi.EmbeddingModel;

    public static Uri All { get; } = new("/api/knowledge-bases", UriKind.Relative);

    public static Uri One(Guid id) => new($"/api/knowledge-bases/{id}", UriKind.Relative);

    public static Uri Verify(Guid id) => new($"/api/knowledge-bases/{id}/verify", UriKind.Relative);

    /// <summary>The roles and metadata of the flat sample collection, with every metadata field proposed for creation.</summary>
    public static object FlatMapping { get; } = new
    {
        roles = new { text = "text", documentId = "document_id", title = "title", position = "page", sourceUrl = "source_url" },
        fields = new[]
        {
            new { path = "department", name = "department" },
            new { path = "year", name = "year" },
            new { path = "published_on", name = "published_on" },
            new { path = "confidential", name = "confidential" },
        },
    };

    public static object[] FlatCreateFields { get; } =
    [
        new { name = "department", type = "Text" },
        new { name = "year", type = "Number" },
        new { name = "published_on", type = "Date" },
        new { name = "confidential", type = "Boolean" },
    ];

    /// <summary>A mapping with only the text role, which is all that is required.</summary>
    public static object TextOnly(string text = "text") => new { roles = new { text }, fields = Array.Empty<object>() };

    public static object Body(
        string url,
        string collection,
        string name = "policies",
        string description = "Company policies.",
        string? apiKey = null,
        string? vectorName = null,
        object? mapping = null,
        object? scope = null,
        object[]? createFields = null,
        string model = EmbeddingModel,
        string? queryPrefix = null) => new
        {
            name,
            description,
            kind = "Qdrant",
            connection = new { url, apiKey, collection, vectorName },
            embeddingModel = model,
            queryPrefix,
            mapping = mapping ?? FlatMapping,
            scope = scope ?? new { kind = "Tenant" },
            createFields = createFields ?? FlatCreateFields,
        };

    public static Task<HttpResponseMessage> RegisterAsync(
        HttpClient client,
        string url,
        string collection,
        string name = "policies",
        string description = "Company policies.",
        string? apiKey = null,
        string? vectorName = null,
        object? mapping = null,
        object? scope = null,
        object[]? createFields = null,
        string model = EmbeddingModel,
        string? queryPrefix = null) =>
        client.PostAsJsonAsync(All, Body(url, collection, name, description, apiKey, vectorName, mapping, scope, createFields, model, queryPrefix), TestContext.Current.CancellationToken);

    /// <summary>Registers a knowledge base that must be accepted (even if it is saved unavailable).</summary>
    public static async Task<RegisteredBody> RegisterOkAsync(
        HttpClient client,
        string url,
        string collection,
        string name = "policies",
        string? apiKey = null,
        string? vectorName = null,
        object? mapping = null,
        object? scope = null,
        object[]? createFields = null)
    {
        using var response = await RegisterAsync(client, url, collection, name, apiKey: apiKey, vectorName: vectorName, mapping: mapping, scope: scope, createFields: createFields);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RegisteredBody>(TestContext.Current.CancellationToken))!;
    }

    public static async Task<IReadOnlyList<ListItemBody>> ListAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<ListItemBody>>(All, TestContext.Current.CancellationToken))!;

    public static async Task<DetailBody> GetAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<DetailBody>(One(id), TestContext.Current.CancellationToken))!;

    /// <summary>The raw JSON of a knowledge base as the API returns it, to prove what it does not contain.</summary>
    public static async Task<string> GetRawAsync(HttpClient client, Guid id) =>
        await client.GetStringAsync(One(id), TestContext.Current.CancellationToken);

    public static async Task<VerifiedBody> VerifyOkAsync(HttpClient client, Guid id)
    {
        using var response = await client.PostAsync(Verify(id), content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<VerifiedBody>(TestContext.Current.CancellationToken))!;
    }

    public static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    public sealed record WarningBody(string Code, string Message, string? Path);

    public sealed record RegisteredBody(Guid Id, string Status, List<WarningBody> Warnings);

    public sealed record VerifiedBody(string Status, List<WarningBody> Warnings);

    public sealed record ListItemBody(
        Guid Id,
        string Name,
        string? Description,
        string? Kind,
        string Status,
        string? Scope,
        DateTimeOffset? LastVerifiedAt,
        string? Warning,
        bool Shared);

    public sealed record ConnectionBody(string Url, string Collection, string? VectorName, bool HasApiKey);

    public sealed record RolesBody(string? Text, string? DocumentId, string? Title, string? Position, string? SourceUrl);

    public sealed record MappedFieldBody(string Path, Guid DictionaryFieldId, string Name);

    public sealed record MappingBody(RolesBody Roles, List<MappedFieldBody> Fields);

    public sealed record ScopeBody(string Kind, List<Guid> TenantIds, List<Guid> AssistantIds);

    public sealed record DetailBody(
        Guid Id,
        string Name,
        string? Description,
        string? Kind,
        ConnectionBody? Connection,
        string? EmbeddingModel,
        string? QueryPrefix,
        MappingBody? Mapping,
        ScopeBody? Scope,
        string Status,
        string? Warning,
        DateTimeOffset? LastVerifiedAt,
        bool Shared);

    /// <summary>Makes the fake model endpoint serve the embedding model the sample collections were built with.</summary>
    public static void ServeEmbeddings(QuorumApp app) => InspectApi.ServeEmbeddings(app);
}
