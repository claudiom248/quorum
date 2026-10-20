using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.Tools;
using Quorum.Modules.KnowledgeBases.Contracts;
using Quorum.Shared.Results;

namespace Quorum.Integration.Tests.KnowledgeBases.Search;

/// <summary>Route and body shapes of the test search of the registry API, and a way to call the search the rag tool will use.</summary>
internal static class SearchApi
{
    public static Uri Search(Guid id) => new($"/api/knowledge-bases/{id}/search", UriKind.Relative);

    /// <summary>The roles and metadata of the nested sample collection, bound to the same dictionary fields as the flat one.</summary>
    public static object NestedMapping { get; } = new
    {
        roles = new { text = "chunk_text", documentId = "doc_id", title = "file_name", position = "page", sourceUrl = "source_url" },
        fields = new[]
        {
            new { path = "meta.department", name = "department" },
            new { path = "meta.year", name = "year" },
            new { path = "meta.published_on", name = "published_on" },
        },
    };

    /// <summary>The tags sample collection: the text is called <c>content</c> and the tags are a list.</summary>
    public static object TagsMapping { get; } = new
    {
        roles = new { text = "content", title = "title" },
        fields = new[] { new { path = "tags", name = "tags" } },
    };

    public static object[] TagsCreateFields { get; } = [new { name = "tags", type = "TextList" }];

    public static Task<HttpResponseMessage> PostAsync(HttpClient client, Guid id, string? query, object? filters = null, int? limit = null) =>
        client.PostAsJsonAsync(Search(id), new { query, filters, limit }, TestContext.Current.CancellationToken);

    public static async Task<SearchBody> SearchOkAsync(HttpClient client, Guid id, string query, object? filters = null, int? limit = null)
    {
        using var response = await PostAsync(client, id, query, filters, limit);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SearchBody>(TestContext.Current.CancellationToken))!;
    }

    public static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>The first assistant the setup created: the one a rag tool would be answering for.</summary>
    public static Task<Guid> AssistantIdAsync(QuorumApp app) =>
        ToolFixtures.ScalarAsync<Guid>(app, "SELECT \"Id\" FROM agents.assistants LIMIT 1");

    /// <summary>Calls <see cref="IKnowledgeBaseSearch"/> as the Agents module does, in a scope that runs as the tenant.</summary>
    public static async Task<Result<KnowledgeBaseSearchResponse>> RunAsync(QuorumApp app, Guid tenantId, KnowledgeBaseSearchRequest request)
    {
        await using var scope = TenantScope.Open(app, tenantId);
        return await scope.ServiceProvider.GetRequiredService<IKnowledgeBaseSearch>().SearchAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>A request with the defaults the rag tool starts from.</summary>
    public static KnowledgeBaseSearchRequest Request(
        Guid tenantId,
        Guid assistantId,
        string query,
        IEnumerable<Guid> knowledgeBaseIds,
        object? filters = null,
        object? fixedFilters = null,
        int topK = 5,
        double? scoreThreshold = null,
        int? maxResultCharacters = null) => new()
        {
            TenantId = tenantId,
            AssistantId = assistantId,
            KnowledgeBaseIds = [.. knowledgeBaseIds],
            Query = query,
            Filters = Arguments(filters),
            FixedFilters = Arguments(fixedFilters),
            TopK = topK,
            ScoreThreshold = scoreThreshold,
            MaxResultCharacters = maxResultCharacters,
        };

    private static Dictionary<string, JsonElement> Arguments(object? values) =>
        values is null
            ? []
            : JsonSerializer.SerializeToElement(values).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);

    public sealed record SourceBody(string KnowledgeBase, string? DocumentId, string? Title, string? Position, string? Url);

    public sealed record HitBody(double Score, string Text, SourceBody Source);

    public sealed record SearchBody(List<HitBody> Results, List<string> Notes);
}
