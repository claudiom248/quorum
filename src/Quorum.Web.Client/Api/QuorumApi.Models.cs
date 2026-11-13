namespace Quorum.Web.Client.Api;

// Models (spec 019): the models of the installation, selected by agents and knowledge bases. Writes need models.manage; reads need models.read.
public sealed partial class QuorumApi
{
    public Task<ApiResult<List<ModelInfo>>> ListModelsAsync(string? capability = null, CancellationToken cancellationToken = default) =>
        GetAsync<List<ModelInfo>>(string.IsNullOrEmpty(capability) ? "api/models" : $"api/models?capability={capability}", cancellationToken);

    public Task<ApiResult<ModelInfo>> CreateModelAsync(ModelRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<ModelInfo>(HttpMethod.Post, "api/models", request, setupToken: null, cancellationToken);

    public Task<ApiResult> UpdateModelAsync(Guid id, ModelRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"api/models/{id}", request, setupToken: null, cancellationToken);

    public Task<ApiResult> DeleteModelAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/models/{id}", body: null, setupToken: null, cancellationToken);

    /// <summary>The connection test: the server's models, the suggested capabilities and the dimensions of an embedding model (spec 019 D-5).</summary>
    public Task<ApiResult<ModelTestInfo>> TestModelAsync(string baseUrl, string? apiKey, string? modelName = null, Guid? modelId = null, CancellationToken cancellationToken = default) =>
        SendAsync<ModelTestInfo>(HttpMethod.Post, "api/models/test", new { baseUrl, apiKey, modelName, modelId }, setupToken: null, cancellationToken);
}
