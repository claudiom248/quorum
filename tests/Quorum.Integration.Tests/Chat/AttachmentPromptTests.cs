using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Tenancy.Contracts;

namespace Quorum.Integration.Tests.Chat;

/// <summary>
/// What the model receives when attachments are part of the conversation, through the public <see cref="IAssistantChat"/>
/// contract (the upload API arrives in a later PR of plan 002).
/// </summary>
public sealed class AttachmentPromptTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-20
    public async Task TheModelReceivesTheAttachmentsInTheSystemPromptNotInTheMessages()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync(systemPrompt: "You are Acme's assistant.", contextTokenBudget: 4096);
        await using var scope = TenantScope(app, await TenantIdAsync(app));
        var chat = scope.ServiceProvider.GetRequiredService<IAssistantChat>();
        var assistantId = await AssistantIdAsync(scope);

        var started = await chat.StartAnswerAsync(
            assistantId,
            [new ChatTurn(ChatTurnRole.User, "Summarize the contract")],
            [new ChatAttachment("contract.pdf", "Clause 1: payment within 30 days.")],
            Ct);
        Assert.True(started.IsSuccess);
        await foreach (var chunk in started.Value.WithCancellation(Ct))
        {
        }

        var sent = app.ModelServer.ChatRequests[^1].GetProperty("messages").EnumerateArray().ToList();
        var system = sent[0].GetProperty("content").GetString()!;
        Assert.Equal("system", sent[0].GetProperty("role").GetString());
        Assert.StartsWith("You are Acme's assistant.", system, StringComparison.Ordinal);
        Assert.Contains("contract.pdf", system, StringComparison.Ordinal);
        Assert.Contains("Clause 1: payment within 30 days.", system, StringComparison.Ordinal);
        Assert.Equal(2, sent.Count);
        Assert.Equal("Summarize the contract", sent[1].GetProperty("content").GetString());
    }

    [Fact] // AC-16
    public async Task TheModelReceivesAllAttachmentsAndOnlyTheMessagesThatFitAfterThem()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync(systemPrompt: "s", contextTokenBudget: 256);
        await using var scope = TenantScope(app, await TenantIdAsync(app));
        var chat = scope.ServiceProvider.GetRequiredService<IAssistantChat>();
        var assistantId = await AssistantIdAsync(scope);
        var history = Enumerable.Range(1, 6)
            .Select(i => new ChatTurn(i % 2 == 1 ? ChatTurnRole.User : ChatTurnRole.Assistant, $"Turn {i} {new string('x', 90)}"))
            .ToList();

        var started = await chat.StartAnswerAsync(assistantId, history, [new ChatAttachment("a.txt", new string('d', 300))], Ct);
        Assert.True(started.IsSuccess);
        await foreach (var chunk in started.Value.WithCancellation(Ct))
        {
        }

        var sent = app.ModelServer.ChatRequests[^1].GetProperty("messages").EnumerateArray().ToList();
        Assert.Contains(new string('d', 300), sent[0].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("Turn 6", sent[^1].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.True(sent.Count < 7, $"Expected older messages to be dropped, but {sent.Count} were sent.");
    }

    [Fact] // AC-15
    public async Task TheContextUsageIsMeasuredAgainstTheAssistantsBudget()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync(systemPrompt: "You are Acme's assistant.", contextTokenBudget: 4096);
        await using var scope = TenantScope(app, await TenantIdAsync(app));
        var chat = scope.ServiceProvider.GetRequiredService<IAssistantChat>();
        var assistantId = await AssistantIdAsync(scope);

        var usage = await chat.MeasureContextAsync(
            assistantId,
            [new ChatTurn(ChatTurnRole.User, "Hello")],
            [new ChatAttachment("a.txt", new string('d', 1000))],
            Ct);

        Assert.True(usage.IsSuccess);
        Assert.Equal(4096, usage.Value.ContextTokenBudget);
        Assert.Equal("You are Acme's assistant.".Length, usage.Value.SystemPromptCharacters);
        Assert.True(usage.Value.AttachmentsCharacters > 1000);
        Assert.Equal("Hello".Length, usage.Value.MessagesCharacters);
        Assert.True(usage.Value.RemainingTokens > 0);
    }

    [Fact]
    public async Task AnUnknownAssistantCannotBeMeasured()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await using var scope = TenantScope(app, await TenantIdAsync(app));
        var chat = scope.ServiceProvider.GetRequiredService<IAssistantChat>();

        var usage = await chat.MeasureContextAsync(Guid.NewGuid(), [], [], Ct);

        Assert.False(usage.IsSuccess);
        Assert.Equal("assistant.not_found", usage.Error.Code);
    }

    [Fact] // AC-19
    public void TheDefaultContextBudgetIs16384Tokens()
    {
        Assert.Equal(16384, AssistantDefaults.ContextTokenBudget);
    }

    private static async Task<Guid> TenantIdAsync(QuorumApp app)
    {
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT \"TenantId\" FROM agents.assistants LIMIT 1", connection);
        return (Guid)(await command.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>
    /// A service scope that looks like a request of the tenant (the data is filtered by tenant). Not async on purpose:
    /// the accessor's ambient context must be set in the caller's own flow to be seen by the services.
    /// </summary>
    private static AsyncServiceScope TenantScope(QuorumApp app, Guid tenantId)
    {
        var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(QuorumClaimTypes.TenantId, tenantId.ToString())], "test")),
        };
        return scope;
    }

    private static async Task<Guid> AssistantIdAsync(AsyncServiceScope scope)
    {
        var catalog = scope.ServiceProvider.GetRequiredService<IAssistantCatalog>();
        return await catalog.GetDefaultAssistantIdAsync(Ct) ?? throw new InvalidOperationException("No assistant.");
    }
}
