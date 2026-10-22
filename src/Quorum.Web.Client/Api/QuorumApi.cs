using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace Quorum.Web.Client.Api;

/// <summary>Typed client of the Quorum public API, as used by the web UI (same origin, cookie authentication).</summary>
public sealed partial class QuorumApi(HttpClient http)
{
    private const string SetupTokenHeader = "X-Setup-Token";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<ApiResult<SetupStatus>> GetSetupStatusAsync(CancellationToken cancellationToken = default) =>
        GetAsync<SetupStatus>("api/setup/status", cancellationToken);

    public Task<ApiResult> VerifySetupTokenAsync(string token, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/setup/verify-token", new { token }, setupToken: null, cancellationToken);

    /// <param name="modelName">When given, the server also reports whether that model supports tools.</param>
    public Task<ApiResult<ModelConnection>> TestSetupModelAsync(string setupToken, string baseUrl, string? apiKey, string? modelName = null, CancellationToken cancellationToken = default) =>
        SendAsync<ModelConnection>(HttpMethod.Post, "api/setup/model/test", new { baseUrl, apiKey, modelName }, setupToken, cancellationToken);

    public Task<ApiResult> CompleteSetupAsync(string setupToken, CompleteSetupRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/setup/complete", request, setupToken, cancellationToken);

    public Task<ApiResult> LoginAsync(string email, string password, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/auth/login", new { email, password }, setupToken: null, cancellationToken);

    public Task<ApiResult> LogoutAsync(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/auth/logout", body: null, setupToken: null, cancellationToken);

    public Task<ApiResult<CurrentUser>> GetCurrentUserAsync(CancellationToken cancellationToken = default) =>
        GetAsync<CurrentUser>("api/auth/me", cancellationToken);

    public Task<ApiResult<AssistantSettings>> GetAssistantSettingsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<AssistantSettings>("api/settings/assistant", cancellationToken);

    public Task<ApiResult> UpdateAssistantSettingsAsync(UpdateAssistantSettingsRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, "api/settings/assistant", request, setupToken: null, cancellationToken);

    /// <param name="modelName">When given, the server also reports whether that model supports tools.</param>
    public Task<ApiResult<ModelConnection>> TestAssistantModelAsync(string baseUrl, string? apiKey, string? modelName = null, CancellationToken cancellationToken = default) =>
        SendAsync<ModelConnection>(HttpMethod.Post, "api/settings/assistant/test", new { baseUrl, apiKey, modelName }, setupToken: null, cancellationToken);

    public Task<ApiResult<List<AssistantSummary>>> ListAssistantsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<List<AssistantSummary>>("api/assistants", cancellationToken);

    public Task<ApiResult<AssistantTools>> GetAssistantToolsAsync(Guid assistantId, CancellationToken cancellationToken = default) =>
        GetAsync<AssistantTools>($"api/assistants/{assistantId}/tools", cancellationToken);

    /// <summary>Activates exactly these tools on the assistant.</summary>
    public Task<ApiResult> SetAssistantToolsAsync(Guid assistantId, IReadOnlyList<Guid> toolIds, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"api/assistants/{assistantId}/tools", new { toolIds }, setupToken: null, cancellationToken);

    public Task<ApiResult<List<ToolInfo>>> ListToolsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<List<ToolInfo>>("api/tools", cancellationToken);

    /// <summary>Enables or disables a tool and sets its policy. Enabling a tool pending review is how the admin approves it.</summary>
    public Task<ApiResult> UpdateToolAsync(Guid toolId, string state, string policy, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"api/tools/{toolId}", new { state, policy }, setupToken: null, cancellationToken);

    public Task<ApiResult<List<ToolServer>>> ListToolServersAsync(CancellationToken cancellationToken = default) =>
        GetAsync<List<ToolServer>>("api/tools/servers", cancellationToken);

    public Task<ApiResult<ToolServer>> GetToolServerAsync(Guid id, CancellationToken cancellationToken = default) =>
        GetAsync<ToolServer>($"api/tools/servers/{id}", cancellationToken);

    /// <summary>Connects to a server without registering it and lists what it offers.</summary>
    public Task<ApiResult<ToolServerTest>> TestToolServerAsync(string url, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken = default) =>
        SendAsync<ToolServerTest>(HttpMethod.Post, "api/tools/servers/test", new { url, headers }, setupToken: null, cancellationToken);

    public Task<ApiResult<CreatedToolServer>> CreateToolServerAsync(ToolServerRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<CreatedToolServer>(HttpMethod.Post, "api/tools/servers", request, setupToken: null, cancellationToken);

    public Task<ApiResult> UpdateToolServerAsync(Guid id, ToolServerRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"api/tools/servers/{id}", request, setupToken: null, cancellationToken);

    public Task<ApiResult> DeleteToolServerAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/tools/servers/{id}", body: null, setupToken: null, cancellationToken);

    /// <summary>Reads the server's tools again: new and changed ones go to review, vanished ones are removed.</summary>
    public Task<ApiResult<ToolRefresh>> RefreshToolServerAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync<ToolRefresh>(HttpMethod.Post, $"api/tools/servers/{id}/refresh", body: null, setupToken: null, cancellationToken);

    public Task<ApiResult<List<ConversationSummary>>> ListConversationsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<List<ConversationSummary>>("api/conversations", cancellationToken);

    public async Task<ApiResult<Guid>> CreateConversationAsync(CancellationToken cancellationToken = default)
    {
        var created = await SendAsync<CreatedConversation>(HttpMethod.Post, "api/conversations", body: null, setupToken: null, cancellationToken);
        return created.IsSuccess ? ApiResult.Ok(created.Value.Id) : ApiResult.Fail<Guid>(created.Problem!);
    }

    public Task<ApiResult<ConversationDetail>> GetConversationAsync(Guid id, CancellationToken cancellationToken = default) =>
        GetAsync<ConversationDetail>($"api/conversations/{id}", cancellationToken);

    public Task<ApiResult> RenameConversationAsync(Guid id, string title, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Patch, $"api/conversations/{id}", new { title }, setupToken: null, cancellationToken);

    public Task<ApiResult> DeleteConversationAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/conversations/{id}", body: null, setupToken: null, cancellationToken);

    public async Task<ApiResult<AttachmentInfo>> UploadAttachmentAsync(
        Guid conversationId,
        Stream content,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StreamContent(content), "file", fileName);
            using var response = await http.PostAsync(new Uri($"api/conversations/{conversationId}/attachments", UriKind.Relative), form, cancellationToken);
            return await ReadAsync<AttachmentInfo>(response, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return ApiResult.Fail<AttachmentInfo>(ApiProblem.Unreachable);
        }
    }

    public Task<ApiResult<List<AttachmentInfo>>> ListAttachmentsAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        GetAsync<List<AttachmentInfo>>($"api/conversations/{conversationId}/attachments", cancellationToken);

    /// <summary>The text extracted from an attachment, as the model receives it.</summary>
    public async Task<ApiResult<string>> GetAttachmentTextAsync(Guid conversationId, Guid attachmentId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http.GetAsync(
                new Uri($"api/conversations/{conversationId}/attachments/{attachmentId}/text", UriKind.Relative),
                cancellationToken);
            return response.IsSuccessStatusCode
                ? ApiResult.Ok(await response.Content.ReadAsStringAsync(cancellationToken))
                : ApiResult.Fail<string>(await ApiProblem.ReadAsync(response, cancellationToken));
        }
        catch (HttpRequestException)
        {
            return ApiResult.Fail<string>(ApiProblem.Unreachable);
        }
    }

    public Task<ApiResult> RemoveAttachmentAsync(Guid conversationId, Guid attachmentId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/conversations/{conversationId}/attachments/{attachmentId}", body: null, setupToken: null, cancellationToken);

    public Task<ApiResult<ConversationContext>> GetContextAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        GetAsync<ConversationContext>($"api/conversations/{conversationId}/context", cancellationToken);

    /// <summary>Streams the answer to a new message. Cancelling the token stops the answer: the server keeps it as interrupted.</summary>
    public IAsyncEnumerable<AnswerEvent> SendMessageAsync(Guid id, string content, CancellationToken cancellationToken = default) =>
        StreamAnswerAsync($"api/conversations/{id}/messages", new { content }, cancellationToken);

    /// <summary>Approves or denies a tool call the answer waits to run. Fails with 409 when it was decided already or its time passed.</summary>
    public Task<ApiResult> DecideToolCallAsync(Guid conversationId, Guid callId, bool approved, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"api/conversations/{conversationId}/tool-calls/{callId}/decision", new { approved }, setupToken: null, cancellationToken);

    /// <summary>Answers a question the model asked the user.</summary>
    public Task<ApiResult> AnswerQuestionAsync(Guid conversationId, Guid callId, string answer, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"api/conversations/{conversationId}/tool-calls/{callId}/answer", new { answer }, setupToken: null, cancellationToken);

    /// <summary>Streams a new answer that replaces the conversation's last one.</summary>
    public IAsyncEnumerable<AnswerEvent> RegenerateAsync(Guid id, CancellationToken cancellationToken = default) =>
        StreamAnswerAsync($"api/conversations/{id}/regenerate", body: null, cancellationToken);

    private async IAsyncEnumerable<AnswerEvent> StreamAnswerAsync(
        string uri,
        object? body,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, uri, body, setupToken: null);
        // Without this the browser buffers the whole response and the answer would appear only at the end.
        request.SetBrowserResponseStreamingEnabled(true);

        var response = await TrySendStreamingAsync(request, cancellationToken);
        if (response is null)
        {
            yield return new AnswerRejected(ApiProblem.Unreachable);
            yield break;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                yield return new AnswerRejected(await ApiProblem.ReadAsync(response, cancellationToken));
                yield break;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await foreach (var item in SseParser.Create(stream).EnumerateAsync(cancellationToken))
            {
                AnswerEvent? answerEvent = item.EventType switch
                {
                    "delta" => JsonSerializer.Deserialize<AnswerDelta>(item.Data, Json),
                    "reasoning" => JsonSerializer.Deserialize<AnswerReasoning>(item.Data, Json),
                    "done" => JsonSerializer.Deserialize<AnswerDone>(item.Data, Json),
                    "tool_call" => JsonSerializer.Deserialize<ToolCallEvent>(item.Data, Json),
                    "tool_result" => JsonSerializer.Deserialize<ToolResultEvent>(item.Data, Json),
                    "ask_user" => JsonSerializer.Deserialize<AskUserEvent>(item.Data, Json),
                    "sources" => JsonSerializer.Deserialize<SourcesEvent>(item.Data, Json),

                    // Includes "ping": the server keeps the connection busy while the answer waits.
                    _ => null,
                };

                if (answerEvent is not null)
                {
                    yield return answerEvent;
                }
            }
        }
    }

    private async Task<HttpResponseMessage?> TrySendStreamingAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private async Task<ApiResult<T>> GetAsync<T>(string uri, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(new Uri(uri, UriKind.Relative), cancellationToken);
            return await ReadAsync<T>(response, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return ApiResult.Fail<T>(ApiProblem.Unreachable);
        }
    }

    private async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string uri, object? body, string? setupToken, CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateRequest(method, uri, body, setupToken);
            using var response = await http.SendAsync(request, cancellationToken);
            return await ReadAsync<T>(response, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return ApiResult.Fail<T>(ApiProblem.Unreachable);
        }
    }

    private async Task<ApiResult> SendAsync(HttpMethod method, string uri, object? body, string? setupToken, CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateRequest(method, uri, body, setupToken);
            using var response = await http.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode
                ? ApiResult.Success
                : ApiResult.Failure(await ApiProblem.ReadAsync(response, cancellationToken));
        }
        catch (HttpRequestException)
        {
            return ApiResult.Failure(ApiProblem.Unreachable);
        }
    }

    private static async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            return ApiResult.Fail<T>(await ApiProblem.ReadAsync(response, cancellationToken));
        }

        var value = await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
        return value is null
            ? ApiResult.Fail<T>(new ApiProblem((int)response.StatusCode, "client.empty_response", "The server sent an empty response.", new Dictionary<string, string[]>()))
            : ApiResult.Ok(value);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string uri, object? body, string? setupToken)
    {
        var request = new HttpRequestMessage(method, new Uri(uri, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        if (setupToken is not null)
        {
            request.Headers.Add(SetupTokenHeader, setupToken);
        }

        return request;
    }

    private sealed record CreatedConversation(Guid Id);
}
