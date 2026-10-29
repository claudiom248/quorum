namespace Quorum.Web.Client.Api;

// The web search tools (spec 005). The platform's SearXNG instance is part of the one web configuration, in QuorumApi.Web.cs.
public sealed partial class QuorumApi
{
    public Task<ApiResult<List<WebSearchToolListItem>>> ListWebSearchToolsAsync(CancellationToken cancellationToken = default) =>
        ListAllAsync<WebSearchToolListItem>("api/tools/web-search", cancellationToken);

    public Task<ApiResult<Page<WebSearchToolListItem>>> ListWebSearchToolsPageAsync(ListQuery query, int limit, string? cursor, CancellationToken cancellationToken = default) =>
        GetAsync<Page<WebSearchToolListItem>>(PageRoute("api/tools/web-search", query, limit, cursor), cancellationToken);

    public Task<ApiResult<WebSearchToolDetail>> GetWebSearchToolAsync(Guid id, CancellationToken cancellationToken = default) =>
        GetAsync<WebSearchToolDetail>($"api/tools/web-search/{id}", cancellationToken);

    public Task<ApiResult<CreatedWebSearchTool>> CreateWebSearchToolAsync(WebSearchToolRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<CreatedWebSearchTool>(HttpMethod.Post, "api/tools/web-search", request, setupToken: null, cancellationToken);

    public Task<ApiResult> UpdateWebSearchToolAsync(Guid id, WebSearchToolRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"api/tools/web-search/{id}", request, setupToken: null, cancellationToken);

    public Task<ApiResult> DeleteWebSearchToolAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/tools/web-search/{id}", body: null, setupToken: null, cancellationToken);
}
