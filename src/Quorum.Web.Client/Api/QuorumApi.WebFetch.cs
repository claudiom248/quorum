namespace Quorum.Web.Client.Api;

// The web fetch tools (spec 006). Their defaults and the web properties live in the one web configuration, in QuorumApi.Web.cs.
public sealed partial class QuorumApi
{
    public Task<ApiResult<List<WebFetchToolListItem>>> ListWebFetchToolsAsync(CancellationToken cancellationToken = default) =>
        ListAllAsync<WebFetchToolListItem>("api/tools/web-fetch", cancellationToken);

    public Task<ApiResult<Page<WebFetchToolListItem>>> ListWebFetchToolsPageAsync(ListQuery query, int limit, string? cursor, CancellationToken cancellationToken = default) =>
        GetAsync<Page<WebFetchToolListItem>>(PageRoute("api/tools/web-fetch", query, limit, cursor), cancellationToken);

    public Task<ApiResult<WebFetchToolDetail>> GetWebFetchToolAsync(Guid id, CancellationToken cancellationToken = default) =>
        GetAsync<WebFetchToolDetail>($"api/tools/web-fetch/{id}", cancellationToken);

    public Task<ApiResult<CreatedWebFetchTool>> CreateWebFetchToolAsync(WebFetchToolRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<CreatedWebFetchTool>(HttpMethod.Post, "api/tools/web-fetch", request, setupToken: null, cancellationToken);

    public Task<ApiResult> UpdateWebFetchToolAsync(Guid id, WebFetchToolRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"api/tools/web-fetch/{id}", request, setupToken: null, cancellationToken);

    public Task<ApiResult> DeleteWebFetchToolAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/tools/web-fetch/{id}", body: null, setupToken: null, cancellationToken);
}
