namespace Quorum.Web.Client.Api;

// Knowledge bases (spec 003): metadata dictionary, registration, search and rag tools.
public sealed partial class QuorumApi
{
    public Task<ApiResult<List<DictionaryField>>> ListDictionaryAsync(CancellationToken cancellationToken = default) =>
        ListAllAsync<DictionaryField>("api/knowledge-bases/dictionary", cancellationToken);

    public Task<ApiResult<Page<DictionaryField>>> ListDictionaryPageAsync(ListQuery query, int limit, string? cursor, CancellationToken cancellationToken = default) =>
        GetAsync<Page<DictionaryField>>(PageRoute("api/knowledge-bases/dictionary", query, limit, cursor), cancellationToken);

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
        ListAllAsync<KnowledgeBaseListItem>("api/knowledge-bases", cancellationToken);

    public Task<ApiResult<Page<KnowledgeBaseListItem>>> ListKnowledgeBasesPageAsync(ListQuery query, int limit, string? cursor, CancellationToken cancellationToken = default) =>
        GetAsync<Page<KnowledgeBaseListItem>>(PageRoute("api/knowledge-bases", query, limit, cursor), cancellationToken);

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
        ListAllAsync<RagToolListItem>("api/tools/rag", cancellationToken);

    public Task<ApiResult<Page<RagToolListItem>>> ListRagToolsPageAsync(ListQuery query, int limit, string? cursor, CancellationToken cancellationToken = default) =>
        GetAsync<Page<RagToolListItem>>(PageRoute("api/tools/rag", query, limit, cursor), cancellationToken);

    public Task<ApiResult<RagToolDetail>> GetRagToolAsync(Guid id, CancellationToken cancellationToken = default) =>
        GetAsync<RagToolDetail>($"api/tools/rag/{id}", cancellationToken);

    public Task<ApiResult<CreatedRagTool>> CreateRagToolAsync(RagToolRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<CreatedRagTool>(HttpMethod.Post, "api/tools/rag", request, setupToken: null, cancellationToken);

    public Task<ApiResult> UpdateRagToolAsync(Guid id, RagToolRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"api/tools/rag/{id}", request, setupToken: null, cancellationToken);

    public Task<ApiResult> DeleteRagToolAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/tools/rag/{id}", body: null, setupToken: null, cancellationToken);

    // Lookup tools (spec 007 D-12).

    public Task<ApiResult<List<LookupToolListItem>>> ListLookupToolsAsync(CancellationToken cancellationToken = default) =>
        ListAllAsync<LookupToolListItem>("api/tools/lookup", cancellationToken);

    public Task<ApiResult<Page<LookupToolListItem>>> ListLookupToolsPageAsync(ListQuery query, int limit, string? cursor, CancellationToken cancellationToken = default) =>
        GetAsync<Page<LookupToolListItem>>(PageRoute("api/tools/lookup", query, limit, cursor), cancellationToken);

    public Task<ApiResult<LookupToolDetail>> GetLookupToolAsync(Guid id, CancellationToken cancellationToken = default) =>
        GetAsync<LookupToolDetail>($"api/tools/lookup/{id}", cancellationToken);

    public Task<ApiResult<CreatedLookupTool>> CreateLookupToolAsync(LookupToolRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<CreatedLookupTool>(HttpMethod.Post, "api/tools/lookup", request, setupToken: null, cancellationToken);

    public Task<ApiResult> UpdateLookupToolAsync(Guid id, LookupToolRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"api/tools/lookup/{id}", request, setupToken: null, cancellationToken);

    public Task<ApiResult> DeleteLookupToolAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/tools/lookup/{id}", body: null, setupToken: null, cancellationToken);

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
        ListAllAsync<ManualLookupInfo>("api/knowledge-bases/manual-lookups", cancellationToken);

    public Task<ApiResult<Page<ManualLookupInfo>>> ListManualLookupsPageAsync(ListQuery query, int limit, string? cursor, CancellationToken cancellationToken = default) =>
        GetAsync<Page<ManualLookupInfo>>(PageRoute("api/knowledge-bases/manual-lookups", query, limit, cursor), cancellationToken);

    /// <summary>One page of every lookup, manual and derived. The query's type filter is the kind (<c>Manual</c> or <c>Derived</c>).</summary>
    public Task<ApiResult<Page<LookupListItem>>> ListLookupsPageAsync(ListQuery query, int limit, string? cursor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var route = PageRoute("api/knowledge-bases/lookups", query with { Type = [] }, limit, cursor);
        foreach (var kind in query.Type)
        {
            route += $"&kind={Uri.EscapeDataString(kind)}";
        }

        return GetAsync<Page<LookupListItem>>(route, cancellationToken);
    }

    public Task<ApiResult<CreatedId>> CreateManualLookupAsync(ManualLookupRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<CreatedId>(HttpMethod.Post, "api/knowledge-bases/manual-lookups", request, setupToken: null, cancellationToken);

    public Task<ApiResult> UpdateManualLookupAsync(Guid id, ManualLookupRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"api/knowledge-bases/manual-lookups/{id}", request, setupToken: null, cancellationToken);

    public Task<ApiResult> DeleteManualLookupAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/knowledge-bases/manual-lookups/{id}", body: null, setupToken: null, cancellationToken);

    /// <summary>The route with the filters of the query, the page size and the cursor of the page to read.</summary>
    private static string PageRoute(string route, ListQuery query, int limit, string? cursor)
    {
        ArgumentNullException.ThrowIfNull(query);

        var parts = new List<string>();
        var filters = query.ToQueryString();
        if (filters.Length > 1)
        {
            parts.Add(filters[1..]);
        }

        parts.Add($"limit={limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        AddQuery(parts, "cursor", cursor);
        return $"{route}?{string.Join('&', parts)}";
    }

    /// <summary>
    /// Every item of a list, page after page, for the pages that have no paging of their own yet: nothing is silently cut at the
    /// first page.
    /// </summary>
    private async Task<ApiResult<List<T>>> ListAllAsync<T>(string route, CancellationToken cancellationToken)
    {
        var items = new List<T>();
        string? cursor = null;
        do
        {
            var page = await GetAsync<Page<T>>(PageRoute(route, ListQuery.Empty, limit: 200, cursor), cancellationToken);
            if (!page.IsSuccess)
            {
                return ApiResult.Fail<List<T>>(page.Problem!);
            }

            items.AddRange(page.Value.Items);
            cursor = page.Value.Cursor;
        }
        while (cursor is not null);

        return ApiResult.Ok(items);
    }

    private static void AddQuery(List<string> query, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            query.Add($"{name}={Uri.EscapeDataString(value)}");
        }
    }
}
