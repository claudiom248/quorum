using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.Chat;

public sealed class ChatStreamingTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-13
    public async Task TheAnswerIsStreamedAndBothMessagesAreStoredAsCompleted()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.ModelServer.Chat = FakeModelServer.ChatScript.Reply("Hello", " there", "!");
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Hi");

        Assert.Equal(["delta", "delta", "delta", "done"], events.Select(e => e.Type));
        Assert.Equal("Hello there!", SseReader.Text(events));
        Assert.Equal("Completed", events[^1].Data.GetProperty("status").GetString());

        var conversation = await ChatApi.GetAsync(client, id);
        Assert.Equal(
            [("User", "Hi", "Completed"), ("Assistant", "Hello there!", "Completed")],
            conversation.Messages.Select(m => (m.Role, m.Content, m.Status)));
    }

    [Theory]
    [InlineData("reasoning")] // Ollama
    [InlineData("reasoning_content")] // LiteLLM and others
    public async Task TheModelsReasoningIsStreamedButNotStored(string field)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.ModelServer.Chat = FakeModelServer.ChatScript.Reply("The answer", " is 42.") with
        {
            Reasoning = ["Let me ", "think."],
            ReasoningField = field,
        };
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "What is the answer?");

        Assert.Equal(["reasoning", "reasoning", "delta", "delta", "done"], events.Select(e => e.Type));
        Assert.Equal("Let me think.", string.Concat(events.Where(e => e.Type == "reasoning").Select(e => e.Data.GetProperty("text").GetString())));
        Assert.Equal("The answer is 42.", SseReader.Text(events));

        var conversation = await ChatApi.GetAsync(client, id);
        Assert.Equal("The answer is 42.", conversation.Messages[^1].Content);
    }

    [Fact]
    public async Task TheModelReceivesTheSystemPromptAndTheConversation()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync(systemPrompt: "You are Acme's assistant.");
        var id = await ChatApi.CreateConversationAsync(client);
        await ChatApi.SendAsync(client, id, "First question");

        await ChatApi.SendAsync(client, id, "Second question");

        var messages = app.ModelServer.ChatRequests[^1].GetProperty("messages").EnumerateArray()
            .Select(m => (m.GetProperty("role").GetString(), m.GetProperty("content").GetString()))
            .ToList();
        Assert.Equal(
            [("system", "You are Acme's assistant."), ("user", "First question"), ("assistant", "Hello from the model."), ("user", "Second question")],
            messages);
        Assert.Equal("llama3.2", app.ModelServer.ChatRequests[^1].GetProperty("model").GetString());
    }

    [Fact] // AC-15
    public async Task OnlyTheMostRecentMessagesThatFitTheContextBudgetAreSent()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        // 256 tokens with a 20% margin ≈ 819 characters of context.
        using var client = await app.SetUpAsync(systemPrompt: "Short.", contextTokenBudget: 256);
        app.ModelServer.Chat = FakeModelServer.ChatScript.Reply(new string('a', 150));
        var id = await ChatApi.CreateConversationAsync(client);

        for (var i = 1; i <= 5; i++)
        {
            await ChatApi.SendAsync(client, id, $"Question {i} " + new string('q', 140));
        }

        var sent = app.ModelServer.ChatRequests[^1].GetProperty("messages").EnumerateArray().ToList();
        var totalCharacters = sent.Sum(m => m.GetProperty("content").GetString()!.Length);
        Assert.Equal("system", sent[0].GetProperty("role").GetString());
        Assert.StartsWith("Question 5", sent[^1].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.True(sent.Count < 10, $"Expected older messages to be dropped, but {sent.Count} were sent.");
        Assert.True(totalCharacters <= 256 * 4 * 0.8, $"Sent {totalCharacters} characters, over the budget.");
        Assert.Equal(10, (await ChatApi.GetAsync(client, id)).Messages.Count);
    }

    [Fact] // AC-15b
    public async Task AMessageLongerThanTheBudgetIsRejectedAndNothingIsStored()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync(contextTokenBudget: 256);
        var id = await ChatApi.CreateConversationAsync(client);

        using var response = await client.PostAsJsonAsync(ChatApi.Messages(id), new { content = new string('x', 5000) }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct);
        Assert.Contains("characters", problem!.Detail, StringComparison.Ordinal);
        Assert.Empty((await ChatApi.GetAsync(client, id)).Messages);
        Assert.Empty(app.ModelServer.ChatRequests);
    }

    [Fact] // AC-16
    public async Task StoppingTheStreamStoresThePartialAnswerAsInterrupted()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.ModelServer.Chat = FakeModelServer.ChatScript.Slow(chunkCount: 100, delayPerChunk: TimeSpan.FromMilliseconds(50));
        var id = await ChatApi.CreateConversationAsync(client);

        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ChatApi.Messages(id)) { Content = JsonContent.Create(new { content = "Write a long story" }) };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token);
            var received = 0;
            await foreach (var sseEvent in SseReader.ReadEventsAsync(response))
            {
                if (sseEvent.Type == "delta" && ++received == 3)
                {
                    break;
                }
            }

            await stop.CancelAsync();
        }

        var conversation = await ChatApi.WaitForLastMessageAsync(client, id, "Interrupted");
        Assert.StartsWith("word1 word2 word3", conversation.Messages[^1].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("word100", conversation.Messages[^1].Content, StringComparison.Ordinal);

        // Stopping an answer is a normal user action, not a server error.
        Assert.Empty(app.Logs.ErrorsAndAbove);
    }

    [Fact] // AC-17
    public async Task AModelFailureMidStreamStoresThePartialAnswerAsFailed()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.ModelServer.Chat = FakeModelServer.ChatScript.FailAfter(2, "Partial", " answer", " never sent");
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Hi");

        var done = events[^1];
        Assert.Equal("done", done.Type);
        Assert.Equal("Failed", done.Data.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(done.Data.GetProperty("error").GetString()));
        var answer = (await ChatApi.GetAsync(client, id)).Messages[^1];
        Assert.Equal(("Partial answer", "Failed"), (answer.Content, answer.Status));
    }

    [Fact]
    public async Task AModelThatEndsWithoutAnAnswerIsAFailureNotAnEmptySuccess()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        // E.g. a reasoning model that used its whole budget thinking.
        app.ModelServer.Chat = FakeModelServer.ChatScript.Reply() with { Reasoning = ["Thinking, thinking…"] };
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Hi");

        var done = events[^1];
        Assert.Equal("Failed", done.Data.GetProperty("status").GetString());
        Assert.Contains("without an answer", done.Data.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal("Failed", (await ChatApi.GetAsync(client, id)).Messages[^1].Status);
    }

    [Fact]
    public async Task AnErrorStatusFromTheModelIsReportedWithItsDetails()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.ModelServer.Chat = FakeModelServer.ChatScript.Reply() with { HttpErrorStatus = 503 };
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Hi");

        var done = events[^1];
        Assert.Equal("Failed", done.Data.GetProperty("status").GetString());
        var error = done.Data.GetProperty("error").GetString();
        Assert.Contains("HTTP 503", error, StringComparison.Ordinal);
        Assert.Contains("model overloaded", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreachableModelStoresTheQuestionAndAFailedAnswer()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        using var _ = await client.PutAsJsonAsync(
            new Uri("/api/settings/assistant", UriKind.Relative),
            new { baseUrl = "http://turned-off.test/v1", modelName = "llama3.2", contextTokenBudget = 4096, systemPrompt = "x" },
            Ct);
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Anyone there?");

        Assert.Equal("Failed", events[^1].Data.GetProperty("status").GetString());
        Assert.StartsWith("Cannot reach the model", events[^1].Data.GetProperty("error").GetString(), StringComparison.Ordinal);
        var messages = (await ChatApi.GetAsync(client, id)).Messages;
        Assert.Equal([("User", "Completed"), ("Assistant", "Failed")], messages.Select(m => (m.Role, m.Status)));
    }

    [Fact] // AC-18
    public async Task RegenerationReplacesTheLastAnswer()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.ModelServer.Chat = FakeModelServer.ChatScript.FailAfter(1, "Broken", " answer");
        var id = await ChatApi.CreateConversationAsync(client);
        await ChatApi.SendAsync(client, id, "Hi");

        app.ModelServer.Chat = FakeModelServer.ChatScript.Reply("A better", " answer");
        using var response = await client.PostAsync(ChatApi.Regenerate(id), content: null, Ct);
        var events = await SseReader.ReadAllAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("A better answer", SseReader.Text(events));
        var messages = (await ChatApi.GetAsync(client, id)).Messages;
        Assert.Equal([("User", "Hi"), ("Assistant", "A better answer")], messages.Select(m => (m.Role, m.Content)));
        var sentToModel = app.ModelServer.ChatRequests[^1].GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal("user", sentToModel[^1].GetProperty("role").GetString());
    }

    [Fact]
    public async Task RegeneratingAnEmptyConversationIsAConflict()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var id = await ChatApi.CreateConversationAsync(client);

        using var response = await client.PostAsync(ChatApi.Regenerate(id), content: null, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact] // AC-19
    public async Task ASecondMessageWhileAnAnswerIsStreamingIsAConflict()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.ModelServer.Chat = FakeModelServer.ChatScript.Slow(chunkCount: 20, delayPerChunk: TimeSpan.FromMilliseconds(50));
        var id = await ChatApi.CreateConversationAsync(client);

        using var first = new HttpRequestMessage(HttpMethod.Post, ChatApi.Messages(id)) { Content = JsonContent.Create(new { content = "Long answer please" }) };
        using var streaming = await client.SendAsync(first, HttpCompletionOption.ResponseHeadersRead, Ct);
        using var second = await client.PostAsJsonAsync(ChatApi.Messages(id), new { content = "Are you done?" }, Ct);
        using var regenerate = await client.PostAsync(ChatApi.Regenerate(id), content: null, Ct);
        var firstEvents = await SseReader.ReadAllAsync(streaming);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, regenerate.StatusCode);
        Assert.Equal("Completed", firstEvents[^1].Data.GetProperty("status").GetString());
        Assert.Equal(2, (await ChatApi.GetAsync(client, id)).Messages.Count);
    }

    [Fact]
    public async Task DeletingAConversationWhileItStreamsCancelsTheAnswer()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.ModelServer.Chat = FakeModelServer.ChatScript.Slow(chunkCount: 100, delayPerChunk: TimeSpan.FromMilliseconds(50));
        var id = await ChatApi.CreateConversationAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Post, ChatApi.Messages(id)) { Content = JsonContent.Create(new { content = "Long answer please" }) };
        using var streaming = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        using var deleted = await client.DeleteAsync(ChatApi.Conversation(id), Ct);
        var events = await SseReader.ReadAllAsync(streaming);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.True(events.Count < 100, "The answer kept streaming after the conversation was deleted.");
        using var afterwards = await client.GetAsync(ChatApi.Conversation(id), Ct);
        Assert.Equal(HttpStatusCode.NotFound, afterwards.StatusCode);
    }
}
