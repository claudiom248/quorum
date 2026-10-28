namespace Quorum.Web.Client.Api;

// Tenancy (spec 011): service tokens an external process uses to reach the knowledge base refresh.
public sealed partial class QuorumApi
{
    public Task<ApiResult<List<ServiceTokenInfo>>> ListServiceTokensAsync(CancellationToken cancellationToken = default) =>
        GetAsync<List<ServiceTokenInfo>>("api/tenants/me/service-tokens", cancellationToken);

    /// <summary>The plaintext <c>token</c> in the answer is shown once: the server stores only its hash.</summary>
    public Task<ApiResult<CreatedServiceToken>> CreateServiceTokenAsync(CancellationToken cancellationToken = default) =>
        SendAsync<CreatedServiceToken>(HttpMethod.Post, "api/tenants/me/service-tokens", body: null, setupToken: null, cancellationToken);

    public Task<ApiResult> RevokeServiceTokenAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/tenants/me/service-tokens/{id}", body: null, setupToken: null, cancellationToken);
}
