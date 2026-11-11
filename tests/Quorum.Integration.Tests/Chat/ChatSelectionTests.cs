using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.Tools;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Tenancy.Contracts;

namespace Quorum.Integration.Tests.Chat;

/// <summary>
/// The conversation's tool selection and endpoint override reach the model through <see cref="IAssistantChat"/> (spec 018, AC-11, AC-12, AC-17).
/// </summary>
public sealed class ChatSelectionTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-11
    public async Task ADebugTurnTurnsOffARequiredToolForTheNextAnswer()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        app.ModelServer.Describe(QuorumApp.ModelBaseUrl, "llama3.2", "tools");
        using var client = await app.SetUpAsync(systemPrompt: "You are Acme's assistant.", contextTokenBudget: 4096);
        var assistantId = await ToolsApi.DefaultAssistantIdAsync(client);
        await ToolFixtures.ActivateOnlyAsync(client, "calculator");
        await using var scope = TenantScope(app, await TenantIdAsync(app));
        var chat = scope.ServiceProvider.GetRequiredService<IAssistantChat>();

        // Required and on for a consumer; a debug turn turns it off anyway.
        await AnswerAsync(chat, assistantId, new ChatTurnOptions([]));
        Assert.Contains("calculator", ToolNames(app.ModelServer.ChatRequests[^1]));

        await AnswerAsync(chat, assistantId, new ChatTurnOptions(["calculator"], Debug: true));
        Assert.DoesNotContain("calculator", ToolNames(app.ModelServer.ChatRequests[^1]));
    }

    [Fact] // AC-17, AC-6 (runtime)
    public async Task AnOptionalToolTurnedOffIsOnAgainOnceItIsRequired()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        app.ModelServer.Describe(QuorumApp.ModelBaseUrl, "llama3.2", "tools");
        using var client = await app.SetUpAsync(systemPrompt: "You are Acme's assistant.", contextTokenBudget: 4096);
        var assistantId = await ToolsApi.DefaultAssistantIdAsync(client);
        await ToolFixtures.ActivateOnlyAsync(client, "calculator");
        var calculator = await CalculatorIdAsync(client, assistantId);
        await using var scope = TenantScope(app, await TenantIdAsync(app));
        var chat = scope.ServiceProvider.GetRequiredService<IAssistantChat>();
        var consumer = new ChatTurnOptions(["calculator"]);

        await SetModeAsync(client, assistantId, calculator, "optional");
        await AnswerAsync(chat, assistantId, consumer);
        Assert.DoesNotContain("calculator", ToolNames(app.ModelServer.ChatRequests[^1]));

        await SetModeAsync(client, assistantId, calculator, "required");
        await AnswerAsync(chat, assistantId, consumer);
        Assert.Contains("calculator", ToolNames(app.ModelServer.ChatRequests[^1]));
    }

    [Fact] // AC-12 (root agent)
    public async Task AnEndpointOverrideAnswersThroughTheChosenModel()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        app.ModelServer.Describe(QuorumApp.ModelBaseUrl, "llama3.2", "tools");
        using var client = await app.SetUpAsync(systemPrompt: "You are Acme's assistant.", contextTokenBudget: 4096);
        var assistantId = await ToolsApi.DefaultAssistantIdAsync(client);
        await ToolFixtures.ActivateOnlyAsync(client, "calculator");
        app.ModelServer.Serve(QuorumApp.ModelBaseUrl, "llama3.2", "qwen2.5");
        var other = await CreateEndpointAsync(client, "qwen2.5");
        await using var scope = TenantScope(app, await TenantIdAsync(app));
        var chat = scope.ServiceProvider.GetRequiredService<IAssistantChat>();

        await AnswerAsync(chat, assistantId, new ChatTurnOptions([], Debug: true, ModelEndpointId: other));

        Assert.Equal("qwen2.5", app.ModelServer.ChatRequests[^1].GetProperty("model").GetString());
    }

    [Fact] // AC-13 (runtime): an override that does not exist is refused before the answer starts
    public async Task AnOverrideNamingAnUnknownEndpointIsRefused()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        app.ModelServer.Describe(QuorumApp.ModelBaseUrl, "llama3.2", "tools");
        using var client = await app.SetUpAsync(systemPrompt: "You are Acme's assistant.", contextTokenBudget: 4096);
        var assistantId = await ToolsApi.DefaultAssistantIdAsync(client);
        await ToolFixtures.ActivateOnlyAsync(client, "calculator");
        await using var scope = TenantScope(app, await TenantIdAsync(app));
        var chat = scope.ServiceProvider.GetRequiredService<IAssistantChat>();

        var started = await chat.StartAnswerAsync(
            assistantId,
            Guid.NewGuid(),
            [new ChatTurn(ChatTurnRole.User, "Hi")],
            [],
            Ct,
            new ChatTurnOptions([], Debug: true, ModelEndpointId: Guid.NewGuid()));

        Assert.False(started.IsSuccess);
    }

    private static async Task AnswerAsync(IAssistantChat chat, Guid assistantId, ChatTurnOptions options)
    {
        var started = await chat.StartAnswerAsync(assistantId, Guid.NewGuid(), [new ChatTurn(ChatTurnRole.User, "Hi")], [], Ct, options);
        Assert.True(started.IsSuccess);
        await foreach (var _ in started.Value.WithCancellation(Ct))
        {
        }
    }

    private static async Task<Guid> CalculatorIdAsync(HttpClient client, Guid assistantId) =>
        (await ToolsApi.GetAssistantToolsAsync(client, assistantId)).Pool.Single(t => t.Name == "calculator").ToolId;

    private static async Task SetModeAsync(HttpClient client, Guid assistantId, Guid toolId, string mode)
    {
        using var response = await client.PutAsJsonAsync(ToolsApi.AssistantTools(assistantId), new { tools = new[] { new { toolId, mode } } }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<Guid> CreateEndpointAsync(HttpClient client, string modelName)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/model-endpoints", UriKind.Relative),
            new { name = modelName, baseUrl = QuorumApp.ModelBaseUrl, modelName, apiKey = (string?)null, contextTokenBudget = 4096, supportsTools = true },
            Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private static string[] ToolNames(JsonElement request) =>
        request.TryGetProperty("tools", out var tools)
            ? [.. tools.EnumerateArray().Select(tool => tool.GetProperty("function").GetProperty("name").GetString()!)]
            : [];

    private static async Task<Guid> TenantIdAsync(QuorumApp app)
    {
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT \"TenantId\" FROM agents.assistants LIMIT 1", connection);
        return (Guid)(await command.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>A service scope that looks like a request of the tenant, as the attachment prompt tests do.</summary>
    private static AsyncServiceScope TenantScope(QuorumApp app, Guid tenantId)
    {
        var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(QuorumClaimTypes.TenantId, tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role, "Admin"),
                ],
                "test")),
        };
        return scope;
    }
}
