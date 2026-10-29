using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quorum.Integration.Tests.KnowledgeBases;

namespace Quorum.Integration.Tests.WebSearch;

/// <summary>Routes and body shapes of the web search API, as a client would use them.</summary>
internal static class WebSearchApi
{
    /// <summary>How many engines one tool may name, as the server enforces it.</summary>
    public const int MaxEngines = 10;

    /// <summary>The platform's one web configuration (spec 005, folded into spec 006).</summary>
    public static Uri WebSettings { get; } = new("/api/settings/web", UriKind.Relative);

    /// <summary>The web search API keeps the same name for the route; <see cref="WebSettings"/> is the shape of the unified endpoint.</summary>
    public static Uri WebSearchSettings => WebSettings;

    public static Uri WebSearchTools { get; } = new("/api/tools/web-search", UriKind.Relative);

    public static Uri WebSearchTool(Guid id) => new($"/api/tools/web-search/{id}", UriKind.Relative);

    public static object OwnTenant { get; } = new { kind = "Tenant" };

    /// <summary>The SearXNG instance every web search tool needs (spec 005, AC-7). Saves the whole web configuration with the platform's defaults and only the given search URL.</summary>
    public static async Task ConfigureAsync(HttpClient client, string url = "https://searxng.test") =>
        (await UpdateConfigurationAsync(client, DefaultConfiguration with { SearchUrl = url })).EnsureSuccessStatusCode();

    public static async Task<WebConfigurationBody> GetConfigurationAsync(HttpClient client)
        => (await client.GetFromJsonAsync<WebConfigurationBody>(WebSettings, TestContext.Current.CancellationToken))!;

    public static async Task<HttpResponseMessage> UpdateConfigurationAsync(HttpClient client, WebConfigurationBody request)
        => await client.PutAsJsonAsync(WebSettings, request, TestContext.Current.CancellationToken);

    /// <summary>A body carrying the platform's defaults, ready to have a few fields changed (with <c>with</c>).</summary>
    public static readonly WebConfigurationBody DefaultConfiguration = new(
        SearchUrl: null,
        SearchTimeoutSeconds: 90,
        FetchTimeoutSeconds: 30,
        FetchMaxUrlsPerInvocation: 5,
        FetchTokenBudgetPerPage: 4000,
        FetchTotalTokenBudget: 20000,
        UserAgent: "Quorum/1.0",
        FetchDelayMs: 1000,
        RespectRobotsTxt: true,
        CacheEnabled: true,
        CacheTtlMinutes: 10);

    public static object Body(
        string name = "web",
        string description = "Searches the web for current information.",
        int? topN = null,
        string[]? searchEngines = null,
        string? language = null,
        int? safeSearch = null,
        string? timeRange = null,
        int? maxInvocationsPerConversation = null,
        object? scope = null,
        string? policy = null) => new
        {
            name,
            description,
            topN,
            searchEngines,
            language,
            safeSearch,
            timeRange,
            maxInvocationsPerConversation,
            scope = scope ?? OwnTenant,
            policy,
        };

    public static async Task<HttpResponseMessage> CreateAsync(HttpClient client, object body) =>
        await client.PostAsJsonAsync(WebSearchTools, body, TestContext.Current.CancellationToken);

    /// <summary>Creates a web search tool that must be accepted and returns what the API answered.</summary>
    public static async Task<CreatedBody> CreateOkAsync(HttpClient client, object body)
    {
        using var response = await CreateAsync(client, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await response.Content.ReadFromJsonAsync<CreatedBody>(TestContext.Current.CancellationToken))!;
    }

    public static async Task<ToolBody> GetAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<ToolBody>(WebSearchTool(id), TestContext.Current.CancellationToken))!;

    public static async Task<IReadOnlyList<ToolBody>> ListAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<KnowledgeBaseApi.PageBody<ToolBody>>(WebSearchTools, TestContext.Current.CancellationToken))!.Items;

    public static async Task<HttpResponseMessage> UpdateAsync(HttpClient client, Guid id, object body) =>
        await client.PutAsJsonAsync(WebSearchTool(id), body, TestContext.Current.CancellationToken);

    /// <summary>The stable <c>code</c> of a ProblemDetails response.</summary>
    public static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>The platform's one web configuration (spec 005, folded into spec 006).</summary>
    public sealed record WebConfigurationBody(
        string? SearchUrl,
        int SearchTimeoutSeconds,
        int FetchTimeoutSeconds,
        int FetchMaxUrlsPerInvocation,
        int FetchTokenBudgetPerPage,
        int FetchTotalTokenBudget,
        string UserAgent,
        int FetchDelayMs,
        bool RespectRobotsTxt,
        bool CacheEnabled,
        int CacheTtlMinutes);

    public sealed record ScopeBody(string Kind, List<Guid> TenantIds, List<Guid> AssistantIds);

    public sealed record PreviewBody(string Name, string Description, JsonElement InputSchema, int EstimatedTokens)
    {
        /// <summary>The names of the parameters of the generated schema.</summary>
        public string[] Parameters => [.. InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name)];

        public JsonElement Parameter(string name) => InputSchema.GetProperty("properties").GetProperty(name);
    }

    public sealed record CreatedBody(Guid Id, PreviewBody? Definition);

    /// <param name="Shared"><see langword="true"/> for a tool of another tenant made visible by its scope: read-only, and its configuration is not shown.</param>
    public sealed record ToolBody(
        Guid Id,
        string Name,
        string Description,
        int TopN,
        List<string>? SearchEngines,
        string? Language,
        int SafeSearch,
        string? TimeRange,
        int MaxInvocationsPerConversation,
        ScopeBody Scope,
        string State,
        string Policy,
        bool Shared,
        PreviewBody? Definition = null);
}
