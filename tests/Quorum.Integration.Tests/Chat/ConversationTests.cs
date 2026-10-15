using System.Net;
using System.Net.Http.Json;
using Npgsql;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.Chat;

public sealed class ConversationTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory] // AC-14
    [InlineData("How do I renew the office lease?", "How do I renew the office lease?")]
    [InlineData(
        "Please summarize the attached supplier contract and list every clause about penalties and termination",
        "Please summarize the attached supplier contract and list")]
    [InlineData("  Many    spaces\n\nand lines  ", "Many spaces and lines")]
    public async Task TheTitleIsTheFirstWordsOfTheFirstMessage(string firstMessage, string expectedTitle)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var id = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, id, firstMessage);
        await ChatApi.SendAsync(client, id, "A second message does not change it");

        var conversation = await ChatApi.GetAsync(client, id);
        Assert.Equal(expectedTitle, conversation.Title);
        Assert.True(conversation.Title!.Length <= 60);
    }

    [Fact] // AC-20
    public async Task ConversationsAreListedByLastUpdateAndCanBeContinued()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var older = await ChatApi.CreateConversationAsync(client);
        await ChatApi.SendAsync(client, older, "First conversation");
        var newer = await ChatApi.CreateConversationAsync(client);
        await ChatApi.SendAsync(client, newer, "Second conversation");

        await ChatApi.SendAsync(client, older, "Back to the first one");
        var list = await client.GetFromJsonAsync<ChatApi.SummaryBody[]>(ChatApi.Conversations, Ct);

        Assert.Equal([older, newer], list!.Select(c => c.Id));
        Assert.Equal(4, (await ChatApi.GetAsync(client, older)).Messages.Count);
    }

    [Fact] // AC-21
    public async Task RenameValidatesAndStoresTheTitle()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var id = await ChatApi.CreateConversationAsync(client);

        using var renamed = await client.PatchAsJsonAsync(ChatApi.Conversation(id), new { title = "Lease renewal" }, Ct);
        using var empty = await client.PatchAsJsonAsync(ChatApi.Conversation(id), new { title = "  " }, Ct);
        using var tooLong = await client.PatchAsJsonAsync(ChatApi.Conversation(id), new { title = new string('x', 61) }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("Lease renewal", (await ChatApi.GetAsync(client, id)).Title);
    }

    [Fact] // AC-22
    public async Task DeleteRemovesTheConversationAndItsMessages()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var id = await ChatApi.CreateConversationAsync(client);
        await ChatApi.SendAsync(client, id, "Something to forget");

        using var deleted = await client.DeleteAsync(ChatApi.Conversation(id), Ct);
        using var afterwards = await client.GetAsync(ChatApi.Conversation(id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, afterwards.StatusCode);

        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM conversations.messages", connection);
        Assert.Equal(0L, await count.ExecuteScalarAsync(Ct));
    }

    [Fact] // AC-23
    public async Task AnotherTenantsConversationIsNotFound()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var mine = await ChatApi.CreateConversationAsync(client);

        // A conversation that belongs to someone else, written directly as another tenant would have it.
        var foreign = Guid.NewGuid();
        await using (var connection = new NpgsqlConnection(app.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO conversations.conversations ("Id", "TenantId", "UserId", "AssistantId", "Title", "CreatedAt", "UpdatedAt")
                VALUES (@id, @tenant, @user, @assistant, 'Secret plans', now(), now())
                """,
                connection);
            insert.Parameters.AddWithValue("id", foreign);
            insert.Parameters.AddWithValue("tenant", Guid.NewGuid());
            insert.Parameters.AddWithValue("user", Guid.NewGuid());
            insert.Parameters.AddWithValue("assistant", Guid.NewGuid());
            await insert.ExecuteNonQueryAsync(Ct);
        }

        using var read = await client.GetAsync(ChatApi.Conversation(foreign), Ct);
        using var rename = await client.PatchAsJsonAsync(ChatApi.Conversation(foreign), new { title = "Mine now" }, Ct);
        using var delete = await client.DeleteAsync(ChatApi.Conversation(foreign), Ct);
        using var send = await client.PostAsJsonAsync(ChatApi.Messages(foreign), new { content = "Hi" }, Ct);
        var list = await client.GetFromJsonAsync<ChatApi.SummaryBody[]>(ChatApi.Conversations, Ct);

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, rename.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, send.StatusCode);
        Assert.Equal([mine], list!.Select(c => c.Id));
    }
}
