namespace Quorum.Web.Client.Api;

// Skills, their assignment to agents, the display name style and the import and export (spec 017). Skills need skills.manage;
// assigning them to an agent needs agents.manage.
public sealed partial class QuorumApi
{
    public Task<ApiResult<List<SkillSummary>>> ListSkillsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<List<SkillSummary>>("api/skills", cancellationToken);

    public Task<ApiResult<SkillDocument>> GetSkillAsync(Guid id, CancellationToken cancellationToken = default) =>
        GetAsync<SkillDocument>($"api/skills/{id}", cancellationToken);

    public Task<ApiResult<SkillDocument>> CreateSkillAsync(SkillRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<SkillDocument>(HttpMethod.Post, "api/skills", request, setupToken: null, cancellationToken);

    public Task<ApiResult> UpdateSkillAsync(Guid id, SkillRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"api/skills/{id}", request, setupToken: null, cancellationToken);

    public Task<ApiResult> DeleteSkillAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/skills/{id}", body: null, setupToken: null, cancellationToken);

    public Task<ApiResult<List<SkillSummary>>> GetAgentSkillsAsync(Guid agentId, CancellationToken cancellationToken = default) =>
        GetAsync<List<SkillSummary>>($"api/agents/{agentId}/skills", cancellationToken);

    /// <summary>Replaces the skills of the agent in one call; the list sent is the whole set.</summary>
    public Task<ApiResult> SetAgentSkillsAsync(Guid agentId, IReadOnlyList<Guid> skillIds, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"api/agents/{agentId}/skills", new { skillIds }, setupToken: null, cancellationToken);

    public Task<ApiResult<SkillSettingsInfo>> GetSkillSettingsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<SkillSettingsInfo>("api/skills/settings", cancellationToken);

    public Task<ApiResult> SetSkillSettingsAsync(string displayNameStyle, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, "api/skills/settings", new { displayNameStyle }, setupToken: null, cancellationToken);

    /// <summary>The first step of an import: the files, as the browser read them, are checked and listed; nothing is written yet.</summary>
    public async Task<ApiResult<SkillImportPreviewInfo>> PreviewSkillImportAsync(IReadOnlyList<SkillFileUpload> files, CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        foreach (var file in files)
        {
            form.Add(new ByteArrayContent(Convert.FromBase64String(file.Base64)), "files", file.Path);
        }

        try
        {
            using var response = await http.PostAsync(new Uri("api/skills/import/preview", UriKind.Relative), form, cancellationToken);
            return await ReadAsync<SkillImportPreviewInfo>(response, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return ApiResult.Fail<SkillImportPreviewInfo>(ApiProblem.Unreachable);
        }
    }

    public Task<ApiResult<SkillImportResultInfo>> ConfirmSkillImportAsync(Guid previewId, IReadOnlyList<SkillImportDecisionInfo> decisions, CancellationToken cancellationToken = default) =>
        SendAsync<SkillImportResultInfo>(HttpMethod.Post, "api/skills/import", new { previewId, decisions }, setupToken: null, cancellationToken);
}
