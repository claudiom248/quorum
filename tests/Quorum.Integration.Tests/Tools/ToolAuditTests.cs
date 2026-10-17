using System.Text.Json;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.Agents.Tools;
using static Quorum.Integration.Tests.Fixtures.FakeModelServer;

namespace Quorum.Integration.Tests.Tools;

/// <summary>The audit log of tool calls (spec 004, AC-41 to AC-44).</summary>
public sealed class ToolAuditTests(PostgresFixture postgres)
{
    private static FakeToolCall Probe(string arguments, string id = "call_1") => new("native__probe", arguments, id);

    [Fact] // AC-41, AC-42
    public async Task EveryCallLeavesAnEntryWithWhoWhenWhatAndTheOutcomeButNotTheResult()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.ModelServer.EnqueueRounds(
            ChatScript.CallTools(new FakeToolCall("native__calculator", """{"expression":"2+2*3"}""")),
            ChatScript.Reply("Eight."));
        var conversationId = await ChatApi.CreateConversationAsync(client);
        var assistantId = await ToolsApi.DefaultAssistantIdAsync(client);

        var events = await ChatApi.SendAsync(client, conversationId, "2+2*3?");

        var entry = Assert.Single(await ToolFixtures.AuditRowsAsync(app));
        Assert.Equal("calculator", entry.ToolName);
        Assert.Null(entry.ServerName);
        Assert.Equal("Auto", entry.Decision);
        Assert.Equal("Succeeded", entry.Outcome);
        Assert.Equal("""{"expression":"2+2*3"}""", entry.Arguments);
        Assert.False(entry.ArgumentsTruncated);
        Assert.Equal(1, entry.ResultCharacters);
        Assert.Null(entry.ErrorCode);
        Assert.Equal(assistantId, entry.AssistantId);
        Assert.Equal(conversationId, entry.ConversationId);
        Assert.Equal(await ToolFixtures.ScalarAsync<Guid>(app, "SELECT \"Id\" FROM tenancy.users LIMIT 1"), entry.UserId);
        Assert.Equal(Guid.Parse(events.Single(e => e.Type == "tool_call").Data.GetProperty("callId").GetString()!), entry.CallId);
        Assert.NotNull(entry.CompletedAt);
        Assert.NotNull(entry.DurationMs);
    }

    [Fact] // AC-42
    public async Task TheContentOfTheResultIsNeverStored()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await ToolFixtures.AddProbeAsync(app);
        app.Probe.Handler = (_, _) => Task.FromResult("the-secret-contents-of-the-result");
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Probe("""{"text":"x"}""")), ChatScript.Reply("Done."));
        var id = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, id, "Go");

        var entry = Assert.Single(await ToolFixtures.AuditRowsAsync(app));
        Assert.Equal("the-secret-contents-of-the-result".Length, entry.ResultCharacters);
        var everything = await ToolFixtures.ScalarAsync<string>(app, "SELECT string_agg(t::text, ' ') FROM agents.tool_audit_entries t");
        Assert.DoesNotContain("the-secret-contents-of-the-result", everything, StringComparison.Ordinal);
    }

    [Fact] // AC-41
    public async Task TheEntryExistsAsStartedWhileTheCallIsStillRunning()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await ToolFixtures.AddProbeAsync(app);
        string? outcomeDuringTheCall = null;
        app.Probe.Handler = async (_, _) =>
        {
            outcomeDuringTheCall = await ToolFixtures.ScalarAsync<string>(app, "SELECT \"Outcome\" FROM agents.tool_audit_entries");
            return "ok";
        };
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Probe("""{"text":"x"}""")), ChatScript.Reply("Done."));
        var id = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, id, "Go");

        Assert.Equal("Started", outcomeDuringTheCall);
        Assert.Equal("Succeeded", Assert.Single(await ToolFixtures.AuditRowsAsync(app)).Outcome);
    }

    [Fact] // AC-41
    public async Task ARefusedCallIsAuditedToo()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.ModelServer.EnqueueRounds(
            ChatScript.CallTools(new FakeToolCall("native__calculator", """{"where":"home"}""")),
            ChatScript.Reply("No."));
        var id = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, id, "Go");

        var entry = Assert.Single(await ToolFixtures.AuditRowsAsync(app));
        Assert.Equal("calculator", entry.ToolName);
        Assert.Equal("Failed", entry.Outcome);
        Assert.Equal("tool.invalid_arguments", entry.ErrorCode);
        Assert.NotNull(entry.CompletedAt);
    }

    [Fact] // AC-41: a tool that was never offered cannot be called, so the call is only logged
    public async Task ACallToAToolThatWasNotOfferedLeavesNoAuditEntryButIsLogged()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.ModelServer.EnqueueRounds(
            ChatScript.CallTools(new FakeToolCall("native__teleport", """{"where":"home"}""")),
            ChatScript.Reply("No."));
        var id = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, id, "Go");

        Assert.Empty(await ToolFixtures.AuditRowsAsync(app));
        Assert.Contains(app.Logs.Messages, message => message.Contains("native__teleport", StringComparison.Ordinal));
    }

    [Fact] // AC-41
    public async Task AFailedToolIsAuditedWithItsErrorCode()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.ModelServer.EnqueueRounds(
            ChatScript.CallTools(new FakeToolCall("native__calculator", """{"expression":"1/0"}""")),
            ChatScript.Reply("No."));
        var id = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, id, "Go");

        var entry = Assert.Single(await ToolFixtures.AuditRowsAsync(app));
        Assert.Equal("Failed", entry.Outcome);
        Assert.Equal("tool.failed", entry.ErrorCode);
    }

    [Fact] // AC-43
    public async Task ValuesOfSensitiveKeysAreRedactedInTheStoredArguments()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await ToolFixtures.AddProbeAsync(app);
        app.ModelServer.EnqueueRounds(
            ChatScript.CallTools(Probe("""{"text":"hello","password":"hunter2","nested":{"apiKey":"k-123","fine":1},"list":[{"Authorization":"Bearer abc"}]}""")),
            ChatScript.Reply("Done."));
        var id = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, id, "Go");

        var entry = Assert.Single(await ToolFixtures.AuditRowsAsync(app));
        foreach (var secret in new[] { "hunter2", "k-123", "Bearer abc" })
        {
            Assert.DoesNotContain(secret, entry.Arguments, StringComparison.Ordinal);
        }

        using var arguments = JsonDocument.Parse(entry.Arguments);
        Assert.Equal("hello", arguments.RootElement.GetProperty("text").GetString());
        Assert.Equal(1, arguments.RootElement.GetProperty("nested").GetProperty("fine").GetInt32());
        Assert.Equal("[redacted]", arguments.RootElement.GetProperty("password").GetString());
        // The tool itself still received the real values.
        Assert.Contains("hunter2", Assert.Single(app.Probe.Calls), StringComparison.Ordinal);
    }

    [Fact] // AC-44
    public async Task WhenTheEntryCannotBeWrittenTheCallDoesNotRunAndTheModelGetsAnError()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await ToolFixtures.AddProbeAsync(app);
        await ToolFixtures.ExecuteAsync(app, "DROP TABLE agents.tool_audit_entries");
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Probe("""{"text":"x"}""")), ChatScript.Reply("I could not run it."));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "Go");

        Assert.Empty(app.Probe.Calls);
        var result = Assert.Single(events, e => e.Type == "tool_result").Data;
        Assert.Equal("failed", result.GetProperty("status").GetString());
        Assert.Contains("audit", app.ModelServer.ChatRequests[1].GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Completed", events[^1].Data.GetProperty("status").GetString());
    }

    [Fact]
    public void RedactionReplacesTheValueOfAnyKeyThatLooksSensitiveAtAnyDepth()
    {
        const string json = """
            {"Password":"a","user_password":"b","PASSWD":"c","clientSecret":"d","token":"e","accessToken":"f","apikey":"g","api_key":"h",
             "Authorization":"i","credentials":"j","private_key":"k","deep":{"x":[{"mySecretThing":{"inner":1}}]},"keep":"visible","count":3}
            """;

        var (redacted, truncated) = AuditRedactor.Redact(json);

        Assert.False(truncated);
        using var document = JsonDocument.Parse(redacted);
        var root = document.RootElement;
        foreach (var key in new[] { "Password", "user_password", "PASSWD", "clientSecret", "token", "accessToken", "apikey", "api_key", "Authorization", "credentials", "private_key" })
        {
            Assert.Equal("[redacted]", root.GetProperty(key).GetString());
        }

        Assert.Equal("[redacted]", root.GetProperty("deep").GetProperty("x")[0].GetProperty("mySecretThing").GetString());
        Assert.Equal("visible", root.GetProperty("keep").GetString());
        Assert.Equal(3, root.GetProperty("count").GetInt32());
    }

    [Fact]
    public void ArgumentsThatAreNotAnObjectAreKeptWithTheirNestedSecretsRedacted()
    {
        var (redacted, _) = AuditRedactor.Redact("""[{"token":"t"},"plain"]""");

        Assert.Equal("""[{"token":"[redacted]"},"plain"]""", redacted);
    }

    [Fact] // AC-41
    public void ArgumentsLongerThanTheLimitAreCutAndFlagged()
    {
        var json = JsonSerializer.Serialize(new { text = new string('a', AuditRedactor.MaxArgumentCharacters + 500) });

        var (redacted, truncated) = AuditRedactor.Redact(json);

        Assert.True(truncated);
        Assert.Equal(AuditRedactor.MaxArgumentCharacters, redacted.Length);
    }

    [Fact]
    public void ArgumentsAtTheLimitAreNotFlagged()
    {
        var json = JsonSerializer.Serialize(new { text = new string('a', 100) });

        var (redacted, truncated) = AuditRedactor.Redact(json);

        Assert.False(truncated);
        Assert.Equal(json, redacted);
    }

    [Fact]
    public void ArgumentsThatCannotBeParsedAreNeverStoredVerbatim()
    {
        var (redacted, _) = AuditRedactor.Redact("""{"password":"hunter2" oops""");

        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(redacted);
        Assert.True(document.RootElement.TryGetProperty("unparsed", out _));
    }

    [Fact]
    public async Task CallsAreAuditedInTheOrderTheyRan()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.ModelServer.EnqueueRounds(
            ChatScript.CallTools(
                new FakeToolCall("native__calculator", """{"expression":"1+1"}""", "call_1"),
                new FakeToolCall("native__calculator", """{"expression":"2+2"}""", "call_2")),
            ChatScript.Reply("Done."));
        var id = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, id, "Go");

        Assert.Equal(
            ["""{"expression":"1+1"}""", """{"expression":"2+2"}"""],
            (await ToolFixtures.AuditRowsAsync(app)).Select(r => r.Arguments));
    }
}
