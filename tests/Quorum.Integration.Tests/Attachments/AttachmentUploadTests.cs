using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.Attachments;

public sealed class AttachmentUploadTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-1, AC-13
    public async Task ASupportedFileIsStoredListedAndItsTextCanBeRead()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var conversation = await ChatApi.CreateConversationAsync(client);

        using var response = await AttachmentApi.UploadAsync(client, conversation, "notes.txt", "First line\nSecond line");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<AttachmentApi.AttachmentBody>(Ct);
        Assert.Equal("notes.txt", created!.FileName);
        Assert.Equal(22, created.CharacterCount);

        var listed = await AttachmentApi.ListAsync(client, conversation);
        Assert.Equal([created.Id], listed.Select(a => a.Id));

        using var text = await client.GetAsync(AttachmentApi.Text(conversation, created.Id), Ct);
        Assert.Equal("text/markdown", text.Content.Headers.ContentType?.MediaType);
        Assert.Equal("First line\nSecond line", await text.Content.ReadAsStringAsync(Ct));

        var detail = await client.GetFromJsonAsync<DetailBody>(ChatApi.Conversation(conversation), Ct);
        Assert.Equal([created.Id], detail!.Attachments.Select(a => a.Id));
    }

    [Fact] // AC-3
    public async Task AFileOverFiveMegabytesIsRejectedAndNothingIsStored()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync(contextTokenBudget: 1_000_000);
        var conversation = await ChatApi.CreateConversationAsync(client);

        using var response = await AttachmentApi.UploadAsync(client, conversation, "big.txt", new byte[(5 * 1024 * 1024) + 1].Select(_ => (byte)'a').ToArray());

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("attachment.too_large", await AttachmentApi.CodeAsync(response));
        Assert.Empty(await AttachmentApi.ListAsync(client, conversation));
    }

    [Theory] // AC-4
    [InlineData("image.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D })]
    [InlineData("archive.txt", new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00 })]
    [InlineData("scan.md", new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37, 0x0A })]
    [InlineData("program.exe", new byte[] { 0x4D, 0x5A, 0x90, 0x00 })]
    public async Task AFileThatIsNotASupportedFormatIsRejectedWhateverItsName(string fileName, byte[] content)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var conversation = await ChatApi.CreateConversationAsync(client);

        using var response = await AttachmentApi.UploadAsync(client, conversation, fileName, content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("attachment.unsupported_type", await AttachmentApi.CodeAsync(response));
        Assert.Empty(await AttachmentApi.ListAsync(client, conversation));
    }

    [Fact] // AC-12
    public async Task TextThatIsNotUtf8IsRejected()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var conversation = await ChatApi.CreateConversationAsync(client);
        var windows1252 = new byte[] { 0x63, 0x61, 0x66, 0xE9, 0x0A }; // "café" in Windows-1252

        using var response = await AttachmentApi.UploadAsync(client, conversation, "menu.csv", windows1252);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("attachment.unsupported_type", await AttachmentApi.CodeAsync(response));
    }

    [Fact]
    public async Task AFileWithoutTextIsRejected()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var conversation = await ChatApi.CreateConversationAsync(client);

        using var response = await AttachmentApi.UploadAsync(client, conversation, "empty.txt", "  \n\n ");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("attachment.no_text", await AttachmentApi.CodeAsync(response));
    }

    [Fact] // AC-8
    public async Task AnEleventhAttachmentIsRejected()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync(contextTokenBudget: 100_000);
        var conversation = await ChatApi.CreateConversationAsync(client);
        for (var i = 1; i <= 10; i++)
        {
            await AttachmentApi.AttachAsync(client, conversation, $"file{i}.txt", $"content {i}");
        }

        using var response = await AttachmentApi.UploadAsync(client, conversation, "one-too-many.txt", "content 11");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("attachment.limit_reached", await AttachmentApi.CodeAsync(response));
        Assert.Equal(10, (await AttachmentApi.ListAsync(client, conversation)).Count);
    }

    [Fact] // AC-9
    public async Task IdenticalContentIsRejectedButTheSameNameWithOtherContentIsAccepted()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var conversation = await ChatApi.CreateConversationAsync(client);
        await AttachmentApi.AttachAsync(client, conversation, "a.txt", "same content");

        using var duplicate = await AttachmentApi.UploadAsync(client, conversation, "copy.txt", "same content");
        using var otherVersion = await AttachmentApi.UploadAsync(client, conversation, "a.txt", "different content");
        var anotherConversation = await ChatApi.CreateConversationAsync(client);
        using var elsewhere = await AttachmentApi.UploadAsync(client, anotherConversation, "a.txt", "same content");

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("attachment.duplicate", await AttachmentApi.CodeAsync(duplicate));
        Assert.Equal(HttpStatusCode.Created, otherVersion.StatusCode);
        Assert.Equal(HttpStatusCode.Created, elsewhere.StatusCode);
    }

    [Fact] // AC-11
    public async Task ACsvBecomesAMarkdownTable()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var conversation = await ChatApi.CreateConversationAsync(client);

        var id = await AttachmentApi.AttachAsync(client, conversation, "people.csv", "name,city\r\n\"Rossi, Mario\",Roma\r\nAnna,\"Cagli|ari\"\r\n");
        var table = await client.GetStringAsync(AttachmentApi.Text(conversation, id), Ct);

        Assert.Equal("| name | city |\n| --- | --- |\n| Rossi, Mario | Roma |\n| Anna | Cagli\\|ari |", table);
    }

    [Fact] // AC-11
    public async Task ASemicolonSeparatedCsvFromExcelIsRecognized()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var conversation = await ChatApi.CreateConversationAsync(client);

        var id = await AttachmentApi.AttachAsync(client, conversation, "prezzi.csv", "prodotto;prezzo\nPane;1,50\n");
        var table = await client.GetStringAsync(AttachmentApi.Text(conversation, id), Ct);

        Assert.Equal("| prodotto | prezzo |\n| --- | --- |\n| Pane | 1,50 |", table);
    }

    [Fact]
    public async Task AUtf8ByteOrderMarkIsStripped()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var conversation = await ChatApi.CreateConversationAsync(client);
        var withBom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("# Title")).ToArray();

        using var response = await AttachmentApi.UploadAsync(client, conversation, "doc.md", withBom);
        var created = await response.Content.ReadFromJsonAsync<AttachmentApi.AttachmentBody>(Ct);
        var text = await client.GetStringAsync(AttachmentApi.Text(conversation, created!.Id), Ct);

        Assert.Equal("# Title", text);
    }

    [Fact]
    public async Task AnAttachmentCanBeRemoved()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var conversation = await ChatApi.CreateConversationAsync(client);
        var id = await AttachmentApi.AttachAsync(client, conversation, "a.txt", "content");

        using var removed = await client.DeleteAsync(AttachmentApi.Attachment(conversation, id), Ct);
        using var again = await client.DeleteAsync(AttachmentApi.Attachment(conversation, id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Empty(await AttachmentApi.ListAsync(client, conversation));
    }

    [Fact] // AC-14
    public async Task AnAttachmentThatDoesNotFitTheBudgetIsRejectedWithTheMissingCharacters()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync(contextTokenBudget: 256);
        var conversation = await ChatApi.CreateConversationAsync(client);

        using var response = await AttachmentApi.UploadAsync(client, conversation, "long.txt", new string('x', 2000));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var (code, detail) = await AttachmentApi.ProblemAsync(response);
        Assert.Equal("attachment.exceeds_budget", code);
        Assert.Matches(@"\d+ characters", detail);
        Assert.Empty(await AttachmentApi.ListAsync(client, conversation));
    }

    [Fact] // AC-14
    public async Task TheBudgetCountsTheAttachmentsAlreadyThere()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync(contextTokenBudget: 256);
        var conversation = await ChatApi.CreateConversationAsync(client);
        await AttachmentApi.AttachAsync(client, conversation, "first.txt", new string('a', 300));

        using var second = await AttachmentApi.UploadAsync(client, conversation, "second.txt", new string('b', 300));

        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Equal("attachment.exceeds_budget", await AttachmentApi.CodeAsync(second));
    }

    [Fact] // AC-15
    public async Task TheContextReportsTheOverallBudgetUsage()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync(systemPrompt: "You are Acme's assistant.", contextTokenBudget: 4096);
        var conversation = await ChatApi.CreateConversationAsync(client);
        var before = await client.GetFromJsonAsync<AttachmentApi.ContextBody>(AttachmentApi.Context(conversation), Ct);
        await AttachmentApi.AttachAsync(client, conversation, "a.txt", new string('d', 4000));

        var after = await client.GetFromJsonAsync<AttachmentApi.ContextBody>(AttachmentApi.Context(conversation), Ct);

        Assert.Equal(4096, before!.ContextTokenBudget);
        Assert.Equal(0, before.AttachmentsTokens);
        Assert.True(after!.AttachmentsTokens >= 1000);
        Assert.Equal(before.RemainingTokens - after.AttachmentsTokens, after.RemainingTokens);
        Assert.True(after.RemainingTokens > 0);
    }

    [Fact] // AC-22
    public async Task AttachingOrRemovingWhileAnAnswerIsStreamingIsAConflict()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var conversation = await ChatApi.CreateConversationAsync(client);
        var existing = await AttachmentApi.AttachAsync(client, conversation, "a.txt", "content");
        app.ModelServer.Chat = FakeModelServer.ChatScript.Slow(chunkCount: 20, delayPerChunk: TimeSpan.FromMilliseconds(50));

        using var first = new HttpRequestMessage(HttpMethod.Post, ChatApi.Messages(conversation)) { Content = JsonContent.Create(new { content = "Long answer please" }) };
        using var streaming = await client.SendAsync(first, HttpCompletionOption.ResponseHeadersRead, Ct);
        using var upload = await AttachmentApi.UploadAsync(client, conversation, "b.txt", "other content");
        using var remove = await client.DeleteAsync(AttachmentApi.Attachment(conversation, existing), Ct);
        await SseReader.ReadAllAsync(streaming);

        Assert.Equal(HttpStatusCode.Conflict, upload.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, remove.StatusCode);
        Assert.Equal([existing], (await AttachmentApi.ListAsync(client, conversation)).Select(a => a.Id));
    }

    [Fact] // AC-24
    public async Task AnotherUsersOrTenantsAttachmentsAreNotFound()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var foreign = Guid.NewGuid();
        var foreignAttachment = Guid.NewGuid();
        await using (var connection = new NpgsqlConnection(app.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO conversations.conversations ("Id", "TenantId", "UserId", "AssistantId", "Title", "CreatedAt", "UpdatedAt")
                VALUES (@conversation, @tenant, @user, @assistant, 'Secret plans', now(), now());
                INSERT INTO conversations.attachments ("Id", "TenantId", "ConversationId", "FileName", "MediaType", "SizeBytes", "ContentHash", "Text", "CharacterCount", "CreatedAt")
                VALUES (@attachment, @tenant, @conversation, 'secret.txt', 'text/plain', 6, 'abc', 'secret', 6, now());
                """,
                connection);
            command.Parameters.AddWithValue("conversation", foreign);
            command.Parameters.AddWithValue("attachment", foreignAttachment);
            command.Parameters.AddWithValue("tenant", Guid.NewGuid());
            command.Parameters.AddWithValue("user", Guid.NewGuid());
            command.Parameters.AddWithValue("assistant", Guid.NewGuid());
            await command.ExecuteNonQueryAsync(Ct);
        }

        using var list = await client.GetAsync(AttachmentApi.Attachments(foreign), Ct);
        using var upload = await AttachmentApi.UploadAsync(client, foreign, "mine.txt", "content");
        using var text = await client.GetAsync(AttachmentApi.Text(foreign, foreignAttachment), Ct);
        using var remove = await client.DeleteAsync(AttachmentApi.Attachment(foreign, foreignAttachment), Ct);
        using var context = await client.GetAsync(AttachmentApi.Context(foreign), Ct);

        Assert.All([list, upload, text, remove, context], response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
    }

    [Fact] // AC-25
    public async Task DeletingAConversationDeletesItsAttachments()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var conversation = await ChatApi.CreateConversationAsync(client);
        await AttachmentApi.AttachAsync(client, conversation, "a.txt", "content");

        using var deleted = await client.DeleteAsync(ChatApi.Conversation(conversation), Ct);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM conversations.attachments", connection);
        Assert.Equal(0L, await count.ExecuteScalarAsync(Ct));
    }

    [Fact] // AC-26
    public async Task EveryAttachmentRouteRequiresAuthentication()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var anonymous = app.CreateUiClient();
        var conversation = Guid.NewGuid();
        var attachment = Guid.NewGuid();

        var responses = new[]
        {
            await AttachmentApi.UploadAsync(anonymous, conversation, "a.txt", "content"),
            await anonymous.GetAsync(AttachmentApi.Attachments(conversation), Ct),
            await anonymous.GetAsync(AttachmentApi.Text(conversation, attachment), Ct),
            await anonymous.DeleteAsync(AttachmentApi.Attachment(conversation, attachment), Ct),
            await anonymous.GetAsync(AttachmentApi.Context(conversation), Ct),
        };

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
    }

    [Fact] // AC-27
    public async Task NeitherTheTextNorTheFileNameIsLogged()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var conversation = await ChatApi.CreateConversationAsync(client);

        await AttachmentApi.AttachAsync(client, conversation, "confidential-salaries.txt", "The CEO earns 12345 euros");
        using var rejected = await AttachmentApi.UploadAsync(client, conversation, "confidential-salaries-2.png", [0x89, 0x50, 0x4E, 0x47, 0x00]);

        Assert.DoesNotContain(app.Logs.Messages, message => message.Contains("confidential-salaries", StringComparison.Ordinal));
        Assert.DoesNotContain(app.Logs.Messages, message => message.Contains("12345", StringComparison.Ordinal));
    }

    [Fact] // AC-3
    public async Task AFarTooLargeBodyIsRejectedAndTheEndpointCarriesTheRequestLimit()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync(contextTokenBudget: 1_000_000);
        var conversation = await ChatApi.CreateConversationAsync(client);

        using var response = await AttachmentApi.UploadAsync(client, conversation, "huge.txt", Enumerable.Repeat((byte)'a', 7 * 1024 * 1024).ToArray());

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(await AttachmentApi.ListAsync(client, conversation));

        // Kestrel enforces this metadata; asserting it is attached proves a body is cut at the limit before it is buffered.
        var upload = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Single(endpoint => endpoint.Metadata.GetMetadata<EndpointNameMetadata>()?.EndpointName == "UploadAttachment");
        var limit = upload.Metadata.GetMetadata<IRequestSizeLimitMetadata>();
        Assert.NotNull(limit);
        Assert.Equal(6L * 1024 * 1024, limit.MaxRequestBodySize);
    }

    [Fact]
    public async Task ThePreviewIsNeverSniffedAsAnotherType()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var conversation = await ChatApi.CreateConversationAsync(client);
        var id = await AttachmentApi.AttachAsync(client, conversation, "page.md", "<script>alert(1)</script>");

        using var response = await client.GetAsync(AttachmentApi.Text(conversation, id), Ct);

        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }

    [Fact] // AC-22
    public async Task DeletingTheConversationWhileAFileIsBeingUploadedNeverFailsWithAServerError()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync(contextTokenBudget: 1_000_000);
        var conversation = await ChatApi.CreateConversationAsync(client);
        var rows = string.Join('\n', Enumerable.Range(0, 120_000).Select(i => $"{i},name {i},city {i}"));

        var upload = AttachmentApi.UploadAsync(client, conversation, "big.csv", rows);
        var delete = client.DeleteAsync(ChatApi.Conversation(conversation), Ct);
        using var uploaded = await upload;
        using var deleted = await delete;

        Assert.Contains(uploaded.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.NotFound, HttpStatusCode.Conflict });
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty(app.Logs.ErrorsAndAbove);
    }

    private sealed record DetailBody(Guid Id, IReadOnlyList<AttachmentApi.AttachmentBody> Attachments);
}
