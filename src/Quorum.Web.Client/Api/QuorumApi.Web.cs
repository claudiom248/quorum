namespace Quorum.Web.Client.Api;

// The one web configuration of the platform (specs 005, 006): SearXNG instance + fetch defaults + web properties, in one row.
public sealed partial class QuorumApi
{
    public Task<ApiResult<WebConfigurationInfo>> GetWebConfigurationAsync(CancellationToken cancellationToken = default) =>
        GetAsync<WebConfigurationInfo>("api/settings/web", cancellationToken);

    public Task<ApiResult> UpdateWebConfigurationAsync(WebConfigurationInfo configuration, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, "api/settings/web", configuration, setupToken: null, cancellationToken);
}
