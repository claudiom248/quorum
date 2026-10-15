using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace Quorum.Web.Client.Api;

/// <summary>Typed client of the Quorum public API, as used by the web UI (same origin, cookie authentication).</summary>
public sealed class QuorumApi(HttpClient http)
{
    private const string SetupTokenHeader = "X-Setup-Token";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<ApiResult<SetupStatus>> GetSetupStatusAsync(CancellationToken cancellationToken = default) =>
        GetAsync<SetupStatus>("api/setup/status", cancellationToken);

    public Task<ApiResult> VerifySetupTokenAsync(string token, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/setup/verify-token", new { token }, setupToken: null, cancellationToken);

    public Task<ApiResult<ModelConnection>> TestSetupModelAsync(string setupToken, string baseUrl, string? apiKey, CancellationToken cancellationToken = default) =>
        SendAsync<ModelConnection>(HttpMethod.Post, "api/setup/model/test", new { baseUrl, apiKey }, setupToken, cancellationToken);

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

    public Task<ApiResult<ModelConnection>> TestAssistantModelAsync(string baseUrl, string? apiKey, CancellationToken cancellationToken = default) =>
        SendAsync<ModelConnection>(HttpMethod.Post, "api/settings/assistant/test", new { baseUrl, apiKey }, setupToken: null, cancellationToken);

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

    /// <summary>Streams the answer to a new message. Cancelling the token stops the answer: the server keeps it as interrupted.</summary>
    public IAsyncEnumerable<AnswerEvent> SendMessageAsync(Guid id, string content, CancellationToken cancellationToken = default) =>
        StreamAnswerAsync($"api/conversations/{id}/messages", new { content }, cancellationToken);

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
