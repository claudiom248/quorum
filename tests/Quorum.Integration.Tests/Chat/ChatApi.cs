using System.Net;
using System.Net.Http.Json;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.Chat;

internal static class ChatApi
{
    public static Uri Conversations { get; } = new("/api/conversations", UriKind.Relative);

    public static Uri Conversation(Guid id) => new($"/api/conversations/{id}", UriKind.Relative);

    public static Uri Messages(Guid id) => new($"/api/conversations/{id}/messages", UriKind.Relative);

    public static Uri Regenerate(Guid id) => new($"/api/conversations/{id}/regenerate", UriKind.Relative);

    public static Uri ToolDecision(Guid id, Guid callId) => new($"/api/conversations/{id}/tool-calls/{callId}/decision", UriKind.Relative);

    public static Uri ToolAnswer(Guid id, Guid callId) => new($"/api/conversations/{id}/tool-calls/{callId}/answer", UriKind.Relative);

    /// <summary>Approves or denies a tool call that waits for the user. Returns the response as is, for tests about the errors.</summary>
    public static Task<HttpResponseMessage> DecideAsync(HttpClient client, Guid conversationId, Guid callId, bool approved) =>
        client.PostAsJsonAsync(ToolDecision(conversationId, callId), new { approved }, TestContext.Current.CancellationToken);

    /// <summary>Answers a question of the model. Returns the response as is, for tests about the errors.</summary>
    public static Task<HttpResponseMessage> AnswerAsync(HttpClient client, Guid conversationId, Guid callId, string answer) =>
        client.PostAsJsonAsync(ToolAnswer(conversationId, callId), new { answer }, TestContext.Current.CancellationToken);

    public static async Task<Guid> CreateConversationAsync(HttpClient client)
    {
        using var response = await client.PostAsync(Conversations, content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CreatedBody>(TestContext.Current.CancellationToken);
        return body!.Id;
    }

    /// <summary>Sends a message and reads the whole stream.</summary>
    public static async Task<IReadOnlyList<SseEvent>> SendAsync(HttpClient client, Guid conversationId, string content)
    {
        using var response = await client.PostAsJsonAsync(Messages(conversationId), new { content }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        return await SseReader.ReadAllAsync(response);
    }

    public static async Task<ConversationBody> GetAsync(HttpClient client, Guid conversationId) =>
        (await client.GetFromJsonAsync<ConversationBody>(Conversation(conversationId), TestContext.Current.CancellationToken))!;

    /// <summary>Waits until the last message reaches a final status (responses are stored when the stream ends).</summary>
    public static async Task<ConversationBody> WaitForLastMessageAsync(HttpClient client, Guid conversationId, string status)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var conversation = await GetAsync(client, conversationId);
            if (conversation.Messages.Count > 0 && conversation.Messages[^1].Status == status)
            {
                return conversation;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"The last message never reached status {status}.");
    }

    public sealed record CreatedBody(Guid Id);

    public sealed record ConversationBody(Guid Id, string? Title, IReadOnlyList<MessageBody> Messages);

    public sealed record MessageBody(Guid Id, string Role, string Content, string Status, DateTimeOffset CreatedAt);

    public sealed record SummaryBody(Guid Id, string? Title, DateTimeOffset UpdatedAt);
}
