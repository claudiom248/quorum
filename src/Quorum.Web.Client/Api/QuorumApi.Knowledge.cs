namespace Quorum.Web.Client.Api;

// Knowledge bases (spec 003): metadata dictionary, registration, search and rag tools.
public sealed partial class QuorumApi
{
    public Task<ApiResult<List<DictionaryField>>> ListDictionaryAsync(CancellationToken cancellationToken = default) =>
        GetAsync<List<DictionaryField>>("api/knowledge-bases/dictionary", cancellationToken);

    public Task<ApiResult<CreatedId>> CreateDictionaryFieldAsync(DictionaryFieldRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<CreatedId>(HttpMethod.Post, "api/knowledge-bases/dictionary", request, setupToken: null, cancellationToken);

    /// <summary>Only the description can change: name and type are what knowledge bases and tools were built on.</summary>
    public Task<ApiResult> UpdateDictionaryFieldAsync(Guid id, string? description, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"api/knowledge-bases/dictionary/{id}", new { description }, setupToken: null, cancellationToken);

    public Task<ApiResult> DeleteDictionaryFieldAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/knowledge-bases/dictionary/{id}", body: null, setupToken: null, cancellationToken);

    /// <summary>Looks at a collection before it is registered: reachability, vectors, a draft mapping and warnings.</summary>
    public Task<ApiResult<InspectResult>> InspectKnowledgeBaseAsync(InspectRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<InspectResult>(HttpMethod.Post, "api/knowledge-bases/inspect", request, setupToken: null, cancellationToken);

    public Task<ApiResult<List<KnowledgeBaseListItem>>> ListKnowledgeBasesAsync(CancellationToken cancellationToken = default) =>
        GetAsync<List<KnowledgeBaseListItem>>("api/knowledge-bases", cancellationToken);

    public Task<ApiResult<KnowledgeBaseDetail>> GetKnowledgeBaseAsync(Guid id, CancellationToken cancellationToken = default) =>
        GetAsync<KnowledgeBaseDetail>($"api/knowledge-bases/{id}", cancellationToken);

    public Task<ApiResult<RegisteredKnowledgeBase>> CreateKnowledgeBaseAsync(KnowledgeBaseRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<RegisteredKnowledgeBase>(HttpMethod.Post, "api/knowledge-bases", request, setupToken: null, cancellationToken);

    public Task<ApiResult<KnowledgeBaseVerification>> UpdateKnowledgeBaseAsync(Guid id, KnowledgeBaseRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<KnowledgeBaseVerification>(HttpMethod.Put, $"api/knowledge-bases/{id}", request, setupToken: null, cancellationToken);

    /// <summary>Removes the registration only: the data in the store is never touched.</summary>
    public Task<ApiResult> DeleteKnowledgeBaseAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/knowledge-bases/{id}", body: null, setupToken: null, cancellationToken);

    public Task<ApiResult<KnowledgeBaseVerification>> VerifyKnowledgeBaseAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync<KnowledgeBaseVerification>(HttpMethod.Post, $"api/knowledge-bases/{id}/verify", body: null, setupToken: null, cancellationToken);

    /// <summary>The admin's test search: scored results to judge a knowledge base before a tool exposes it. Not audited.</summary>
    public Task<ApiResult<TestSearchResult>> SearchKnowledgeBaseAsync(Guid id, TestSearchRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<TestSearchResult>(HttpMethod.Post, $"api/knowledge-bases/{id}/search", request, setupToken: null, cancellationToken);

    public Task<ApiResult<List<RagToolListItem>>> ListRagToolsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<List<RagToolListItem>>("api/tools/rag", cancellationToken);

    public Task<ApiResult<RagToolDetail>> GetRagToolAsync(Guid id, CancellationToken cancellationToken = default) =>
        GetAsync<RagToolDetail>($"api/tools/rag/{id}", cancellationToken);

    public Task<ApiResult<CreatedRagTool>> CreateRagToolAsync(RagToolRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<CreatedRagTool>(HttpMethod.Post, "api/tools/rag", request, setupToken: null, cancellationToken);

    public Task<ApiResult> UpdateRagToolAsync(Guid id, RagToolRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"api/tools/rag/{id}", request, setupToken: null, cancellationToken);

    public Task<ApiResult> DeleteRagToolAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/tools/rag/{id}", body: null, setupToken: null, cancellationToken);

    // Lookups (spec 007).

    public Task<ApiResult<List<KnowledgeBaseLookupInfo>>> ListKnowledgeBaseLookupsAsync(Guid knowledgeBaseId, CancellationToken cancellationToken = default) =>
        GetAsync<List<KnowledgeBaseLookupInfo>>($"api/knowledge-bases/{knowledgeBaseId}/lookups", cancellationToken);

    /// <summary>Asks for a new computation of every lookup of the knowledge base; the refresh service runs it, so the answer is only "accepted".</summary>
    public Task<ApiResult> RefreshKnowledgeBaseLookupsAsync(Guid knowledgeBaseId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"api/knowledge-bases/{knowledgeBaseId}/lookups/refresh", body: null, setupToken: null, cancellationToken);

    public Task<ApiResult> RefreshLookupAsync(Guid lookupId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"api/knowledge-bases/lookups/{lookupId}/refresh", body: null, setupToken: null, cancellationToken);

    /// <param name="slice">The hash of a slice; the whole knowledge base when left out.</param>
    public Task<ApiResult<LookupValuesPage>> ReadLookupValuesAsync(
        Guid lookupId,
        string? slice = null,
        string? search = null,
        string? sort = null,
        int? limit = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        AddQuery(query, "slice", slice);
        AddQuery(query, "search", search);
        AddQuery(query, "sort", sort);
        AddQuery(query, "limit", limit?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddQuery(query, "cursor", cursor);
        var suffix = query.Count > 0 ? "?" + string.Join('&', query) : string.Empty;
        return GetAsync<LookupValuesPage>($"api/knowledge-bases/lookups/{lookupId}/values{suffix}", cancellationToken);
    }

    public Task<ApiResult<List<ManualLookupInfo>>> ListManualLookupsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<List<ManualLookupInfo>>("api/knowledge-bases/manual-lookups", cancellationToken);

    public Task<ApiResult<CreatedId>> CreateManualLookupAsync(ManualLookupRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<CreatedId>(HttpMethod.Post, "api/knowledge-bases/manual-lookups", request, setupToken: null, cancellationToken);

    public Task<ApiResult> UpdateManualLookupAsync(Guid id, ManualLookupRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"api/knowledge-bases/manual-lookups/{id}", request, setupToken: null, cancellationToken);

    public Task<ApiResult> DeleteManualLookupAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/knowledge-bases/manual-lookups/{id}", body: null, setupToken: null, cancellationToken);

    private static void AddQuery(List<string> query, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            query.Add($"{name}={Uri.EscapeDataString(value)}");
        }
    }
}
