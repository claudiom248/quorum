namespace Quorum.Web.Client.Api;

// Model endpoints, agents and assistants (spec 013): all of them need the agents.manage permission.
public sealed partial class QuorumApi
{
    public Task<ApiResult<List<ModelEndpointInfo>>> ListModelEndpointsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<List<ModelEndpointInfo>>("api/model-endpoints", cancellationToken);

    public Task<ApiResult<ModelEndpointInfo>> CreateModelEndpointAsync(ModelEndpointRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<ModelEndpointInfo>(HttpMethod.Post, "api/model-endpoints", request, setupToken: null, cancellationToken);

    public Task<ApiResult> UpdateModelEndpointAsync(Guid id, ModelEndpointRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"api/model-endpoints/{id}", request, setupToken: null, cancellationToken);

    public Task<ApiResult> DeleteModelEndpointAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/model-endpoints/{id}", body: null, setupToken: null, cancellationToken);

    /// <param name="modelName">When given, the server also reports whether that model supports tools.</param>
    public Task<ApiResult<ModelConnection>> TestModelEndpointAsync(string baseUrl, string? apiKey, string? modelName = null, CancellationToken cancellationToken = default) =>
        SendAsync<ModelConnection>(HttpMethod.Post, "api/model-endpoints/test", new { baseUrl, apiKey, modelName }, setupToken: null, cancellationToken);

    public Task<ApiResult<List<AgentListItem>>> ListAgentItemsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<List<AgentListItem>>("api/agents", cancellationToken);

    public Task<ApiResult<AgentDocument>> GetAgentAsync(Guid id, CancellationToken cancellationToken = default) =>
        GetAsync<AgentDocument>($"api/agents/{id}", cancellationToken);

    public Task<ApiResult<AgentDocument>> CreateAgentAsync(AgentRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<AgentDocument>(HttpMethod.Post, "api/agents", request, setupToken: null, cancellationToken);

    public Task<ApiResult> UpdateAgentAsync(Guid id, AgentRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"api/agents/{id}", request, setupToken: null, cancellationToken);

    public Task<ApiResult> DeleteAgentAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/agents/{id}", body: null, setupToken: null, cancellationToken);

    public Task<ApiResult<List<AssistantInfo>>> ListAssistantInfosAsync(CancellationToken cancellationToken = default) =>
        GetAsync<List<AssistantInfo>>("api/assistants", cancellationToken);

    public Task<ApiResult<AssistantInfo>> CreateAssistantAsync(AssistantRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<AssistantInfo>(HttpMethod.Post, "api/assistants", request, setupToken: null, cancellationToken);

    public Task<ApiResult> UpdateAssistantAsync(Guid id, AssistantRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"api/assistants/{id}", request, setupToken: null, cancellationToken);

    public Task<ApiResult> DeleteAssistantAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/assistants/{id}", body: null, setupToken: null, cancellationToken);
}
