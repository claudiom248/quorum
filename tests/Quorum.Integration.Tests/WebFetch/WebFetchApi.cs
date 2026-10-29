using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quorum.Integration.Tests.KnowledgeBases;
using Quorum.Integration.Tests.WebSearch;

namespace Quorum.Integration.Tests.WebFetch;

/// <summary>Routes and body shapes of the web fetch API, as a client would use them (spec 006).</summary>
/// <remarks>The web fetch settings are part of the unified <see cref="WebSearchApi.WebSettings"/>: this helper only reads and writes the fetch fields there.</remarks>
internal static class WebFetchApi
{
    public const int MaxUserAgentLength = 128;

    /// <summary>The web fetch routes live under the same API as the web search ones; the platform's configuration is a single web row.</summary>
    public static Uri WebFetchTools { get; } = new("/api/tools/web-fetch", UriKind.Relative);

    public static Uri WebFetchTool(Guid id) => new($"/api/tools/web-fetch/{id}", UriKind.Relative);

    public static object OwnTenant { get; } = new { kind = "Tenant" };

    /// <summary>Reads the whole platform configuration through the unified endpoint (spec 006).</summary>
    public static async Task<WebSearchApi.WebConfigurationBody> GetConfigurationAsync(HttpClient client) =>
        await WebSearchApi.GetConfigurationAsync(client);

    public static async Task<HttpResponseMessage> UpdateConfigurationAsync(HttpClient client, WebSearchApi.WebConfigurationBody request) =>
        await WebSearchApi.UpdateConfigurationAsync(client, request);

    public static object Body(
        string name = "read",
        string description = "Fetches the content of a list of web pages.",
        int? tokenBudgetPerPage = null,
        int? totalTokenBudget = null,
        int? maxUrlsPerInvocation = null,
        int? timeout = null,
        object? scope = null,
        string? policy = null) => new
        {
            name,
            description,
            tokenBudgetPerPage,
            totalTokenBudget,
            maxUrlsPerInvocation,
            timeout,
            scope = scope ?? OwnTenant,
            policy,
        };

    public static async Task<HttpResponseMessage> CreateAsync(HttpClient client, object body) =>
        await client.PostAsJsonAsync(WebFetchTools, body, TestContext.Current.CancellationToken);

    public static async Task<CreatedBody> CreateOkAsync(HttpClient client, object body)
    {
        using var response = await CreateAsync(client, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await response.Content.ReadFromJsonAsync<CreatedBody>(TestContext.Current.CancellationToken))!;
    }

    public static async Task<ToolBody> GetAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<ToolBody>(WebFetchTool(id), TestContext.Current.CancellationToken))!;

    public static async Task<IReadOnlyList<ToolBody>> ListAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<KnowledgeBaseApi.PageBody<ToolBody>>(WebFetchTools, TestContext.Current.CancellationToken))!.Items;

    public static async Task<HttpResponseMessage> UpdateAsync(HttpClient client, Guid id, object body) =>
        await client.PutAsJsonAsync(WebFetchTool(id), body, TestContext.Current.CancellationToken);

    public static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    public sealed record ScopeBody(string Kind, List<Guid> TenantIds, List<Guid> AssistantIds);

    public sealed record PreviewBody(string Name, string Description, JsonElement InputSchema, int EstimatedTokens)
    {
        public string[] Parameters => [.. InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name)];

        public JsonElement Parameter(string name) => InputSchema.GetProperty("properties").GetProperty(name);
    }

    public sealed record CreatedBody(Guid Id, PreviewBody? Definition);

    public sealed record ToolBody(
        Guid Id,
        string Name,
        string Description,
        int TokenBudgetPerPage,
        int TotalTokenBudget,
        int MaxUrlsPerInvocation,
        int Timeout,
        ScopeBody Scope,
        string State,
        string Policy,
        bool Shared,
        PreviewBody? Definition = null);
}
