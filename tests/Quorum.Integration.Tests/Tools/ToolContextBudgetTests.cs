using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.Agents.Chat;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Agents.Tools;
using static Quorum.Integration.Tests.Fixtures.FakeModelServer;

namespace Quorum.Integration.Tests.Tools;

/// <summary>The tool definitions and results in the context budget (spec 004, D-27, AC-20, AC-21).</summary>
public sealed class ToolContextBudgetTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // 256 tokens give 256 * 4 = 1024 usable characters.
    private const int SmallBudget = 256;

    private static readonly ChatAttachment[] NoAttachments = [];

    private static ChatTurn User(string content) => new(ChatTurnRole.User, content);

    private static ChatTurn Assistant(string content) => new(ChatTurnRole.Assistant, content);

    [Fact] // AC-21
    public void DefinitionsThatDoNotFitAfterTheSystemPromptFailAndSayHowManyCharactersAreMissing()
    {
        var selection = ContextWindow.Select("system", [User("hi")], [], SmallBudget, toolsCharacters: 2000);

        Assert.False(selection.IsSuccess);
        Assert.Equal("chat.tools_exceed_budget", selection.Error.Code);
        Assert.Contains("982", selection.Error.Description, StringComparison.Ordinal); // 2000 - (1024 - 6)
        Assert.Contains("deactivate", selection.Error.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Quorum.Shared.Results.ErrorType.Validation, selection.Error.Type);
    }

    [Fact] // AC-21
    public void AttachmentsKeepTheirPriorityOverTheDefinitions()
    {
        var attachments = new[] { new ChatAttachment("a.txt", new string('d', 300)) };
        var attachmentCharacters = AttachmentBlock.Length(attachments);

        var selection = ContextWindow.Select("system", [User("hi")], attachments, SmallBudget, toolsCharacters: 800);

        Assert.False(selection.IsSuccess);
        Assert.Equal("chat.tools_exceed_budget", selection.Error.Code);
        Assert.Contains((800 - (1024 - 6 - attachmentCharacters)).ToString(CultureInfo.InvariantCulture), selection.Error.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void AttachmentsThatDoNotFitStillReportTheAttachmentError()
    {
        var attachments = new[] { new ChatAttachment("big.txt", new string('d', 2000)) };

        var selection = ContextWindow.Select("system", [User("hi")], attachments, SmallBudget, toolsCharacters: 100);

        Assert.Equal("chat.attachments_exceed_budget", selection.Error.Code);
    }

    [Fact]
    public void DefinitionsAreReservedBeforeTheHistoryWhichLosesTheOldestMessagesFirst()
    {
        var history = new[] { User(new string('1', 200)), Assistant(new string('2', 200)), User(new string('3', 200)), Assistant(new string('4', 200)), User("last") };

        var without = ContextWindow.Select("system", history, [], SmallBudget);
        var with = ContextWindow.Select("system", history, [], SmallBudget, toolsCharacters: 500);

        Assert.True(without.IsSuccess);
        Assert.True(with.IsSuccess);
        Assert.True(with.Value.Count < without.Value.Count, "The definitions should have pushed older messages out.");
        Assert.Equal("last", with.Value[^1].Content);
        Assert.True(with.Value.Sum(t => t.Content.Length) + 6 + 500 <= 1024);
    }

    [Fact]
    public void AMessageThatDoesNotFitAfterTheDefinitionsIsTooLongAndTheHintMentionsTheTools()
    {
        var selection = ContextWindow.Select("system", [User(new string('x', 600))], [], SmallBudget, toolsCharacters: 500);

        Assert.Equal("chat.message_too_long", selection.Error.Code);
        Assert.Contains("tool", selection.Error.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // AC-20
    public void TheUsageHasASeparateToolsSegmentThatCountsAgainstWhatIsLeft()
    {
        var usage = ContextWindow.Measure("system", [User("hi")], NoAttachments, SmallBudget, toolsCharacters: 400);

        Assert.Equal(400, usage.ToolsCharacters);
        Assert.Equal(100, usage.ToolsTokens);
        Assert.Equal(1024 - 6 - 400 - 2, usage.RemainingCharacters);
        Assert.Equal(2, usage.MessagesCharacters);
    }

    [Fact]
    public void TheUsageMeasuresOnlyTheMessagesThatFitAfterTheDefinitions()
    {
        var history = new[] { User(new string('1', 300)), Assistant(new string('2', 300)), User("last") };

        var usage = ContextWindow.Measure("system", history, NoAttachments, SmallBudget, toolsCharacters: 500);

        Assert.Equal(304, usage.MessagesCharacters); // 1024 - 6 - 500 = 518 are left: "last" and the message before it fit, the first does not
    }

    [Fact]
    public void WithoutToolsTheUsageIsAsBefore()
    {
        var usage = ContextWindow.Measure("system", [User("hi")], NoAttachments, SmallBudget);

        Assert.Equal(0, usage.ToolsCharacters);
        Assert.Equal(1024 - 6 - 2, usage.RemainingCharacters);
    }

    [Fact] // AC-21
    public async Task SendingIsRejectedWhenTheActiveToolsDoNotFitAndNothingIsStored()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync(systemPrompt: new string('s', 400), contextTokenBudget: SmallBudget);
        var id = await ChatApi.CreateConversationAsync(client);

        using var response = await client.PostAsJsonAsync(ChatApi.Messages(id), new { content = "Hello" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct);
        Assert.Contains("deactivate", problem!.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("chat.tools_exceed_budget", await ToolsApi.ProblemCodeAsync(response));
        Assert.Empty((await ChatApi.GetAsync(client, id)).Messages);
        Assert.Empty(app.ModelServer.ChatRequests);
    }

    [Fact] // AC-21
    public async Task DeactivatingToolsMakesTheSameConversationFit()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync(systemPrompt: new string('s', 400), contextTokenBudget: SmallBudget);
        await ToolFixtures.ActivateOnlyAsync(client);
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Hello");

        Assert.Equal("Completed", events[^1].Data.GetProperty("status").GetString());
    }

    [Fact] // AC-20
    public async Task TheContextEndpointCountsTheDefinitionsInTheirOwnSegment()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync(contextTokenBudget: 4096);
        var id = await ChatApi.CreateConversationAsync(client);

        var withTools = await client.GetFromJsonAsync<ContextBody>(new Uri($"/api/conversations/{id}/context", UriKind.Relative), Ct);
        await ToolFixtures.ActivateOnlyAsync(client);
        var without = await client.GetFromJsonAsync<ContextBody>(new Uri($"/api/conversations/{id}/context", UriKind.Relative), Ct);

        Assert.True(withTools!.ToolsTokens > 100, $"Expected the native tools to take some context, got {withTools.ToolsTokens}.");
        Assert.Equal(0, without!.ToolsTokens);
        Assert.True(withTools.RemainingTokens < without.RemainingTokens);
    }

    [Fact] // loop budget
    public async Task WhenToolResultsFillTheContextTheAnswerEndsAsFailedKeepingTheText()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync(systemPrompt: "s", contextTokenBudget: 1024); // 4096 characters
        await ToolFixtures.ActivateOnlyAsync(client);
        await ToolFixtures.AddProbeAsync(app);
        app.Probe.Handler = (_, _) => Task.FromResult(new string('r', 4000));
        app.ModelServer.EnqueueRounds(
            ChatScript.Reply("Looking it up. ") with { ToolCalls = [new FakeToolCall("native__probe", """{"text":"x"}""")] },
            ChatScript.Reply("Should not be asked."));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Go");

        var done = events[^1].Data;
        Assert.Equal("Failed", done.GetProperty("status").GetString());
        Assert.Contains("tool results filled the context", done.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Single(app.ModelServer.ChatRequests);
        Assert.Equal("Looking it up. ", (await ChatApi.GetAsync(client, id)).Messages[^1].Content);
    }

    [Fact] // loop budget
    public async Task ResultsThatStillFitLetTheLoopContinue()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync(systemPrompt: "s", contextTokenBudget: 1024);
        await ToolFixtures.ActivateOnlyAsync(client);
        await ToolFixtures.AddProbeAsync(app);
        app.Probe.Handler = (_, _) => Task.FromResult(new string('r', 2000));
        app.ModelServer.EnqueueRounds(
            ChatScript.CallTools(new FakeToolCall("native__probe", """{"text":"x"}""")),
            ChatScript.Reply("Done."));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Go");

        Assert.Equal("Completed", events[^1].Data.GetProperty("status").GetString());
    }

    private sealed record ContextBody(
        int ContextTokenBudget,
        int UsableTokens,
        int SystemPromptTokens,
        int AttachmentsTokens,
        int MessagesTokens,
        int RemainingTokens,
        int ToolsTokens);
}
