using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace Quorum.Integration.Tests.Attachments;

internal static class AttachmentApi
{
    public static Uri Attachments(Guid conversationId) => new($"/api/conversations/{conversationId}/attachments", UriKind.Relative);

    public static Uri Attachment(Guid conversationId, Guid attachmentId) => new($"/api/conversations/{conversationId}/attachments/{attachmentId}", UriKind.Relative);

    public static Uri Text(Guid conversationId, Guid attachmentId) => new($"/api/conversations/{conversationId}/attachments/{attachmentId}/text", UriKind.Relative);

    public static Uri Context(Guid conversationId) => new($"/api/conversations/{conversationId}/context", UriKind.Relative);

    public static Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid conversationId, string fileName, string content) =>
        UploadAsync(client, conversationId, fileName, Encoding.UTF8.GetBytes(content));

    public static async Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid conversationId, string fileName, byte[] content)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        return await client.PostAsync(Attachments(conversationId), form, TestContext.Current.CancellationToken);
    }

    /// <summary>Uploads a file that must be accepted and returns its id.</summary>
    public static async Task<Guid> AttachAsync(HttpClient client, Guid conversationId, string fileName, string content)
    {
        using var response = await UploadAsync(client, conversationId, fileName, content);
        Assert.Equal(global::System.Net.HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AttachmentBody>(TestContext.Current.CancellationToken))!.Id;
    }

    public static async Task<IReadOnlyList<AttachmentBody>> ListAsync(HttpClient client, Guid conversationId) =>
        (await client.GetFromJsonAsync<List<AttachmentBody>>(Attachments(conversationId), TestContext.Current.CancellationToken))!;

    public static async Task<string> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken))!.Extensions["code"]!.ToString()!;

    public static async Task<(string Code, string Detail)> ProblemAsync(HttpResponseMessage response)
    {
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken))!;
        return (problem.Extensions["code"]!.ToString()!, problem.Detail!);
    }

    public sealed record AttachmentBody(Guid Id, string FileName, string MediaType, long SizeBytes, int CharacterCount, DateTimeOffset CreatedAt);

    public sealed record ContextBody(int ContextTokenBudget, int UsableTokens, int SystemPromptTokens, int AttachmentsTokens, int MessagesTokens, int RemainingTokens);
}
