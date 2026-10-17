using System.Net;
using System.Net.Http.Json;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.Tools;

public sealed class AssistantToolsTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ListsTheDefaultAssistant()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();

        var assistants = await client.GetFromJsonAsync<List<ToolsApi.AssistantBody>>(ToolsApi.Assistants, Ct);

        var assistant = Assert.Single(assistants!);
        Assert.NotEqual(Guid.Empty, assistant.Id);
        Assert.Equal("Assistant", assistant.Name);
    }

    [Fact]
    public async Task TheActiveSetIsReplacedByWhatTheAdminSends()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var assistantId = await ToolsApi.DefaultAssistantIdAsync(client);
        var calculator = (await ToolsApi.GetAssistantToolsAsync(client, assistantId)).Pool.Single(t => t.Name == "builtin__calculator");

        using var only = await client.PutAsJsonAsync(ToolsApi.AssistantTools(assistantId), new { toolIds = new[] { calculator.ToolId } }, Ct);
        var afterOnly = (await ToolsApi.GetAssistantToolsAsync(client, assistantId)).Pool;
        using var none = await client.PutAsJsonAsync(ToolsApi.AssistantTools(assistantId), new { toolIds = Array.Empty<Guid>() }, Ct);
        var afterNone = (await ToolsApi.GetAssistantToolsAsync(client, assistantId)).Pool;

        Assert.Equal(HttpStatusCode.NoContent, only.StatusCode);
        Assert.Equal(["builtin__calculator"], afterOnly.Where(t => t.Active).Select(t => t.Name).ToArray());
        Assert.Equal(HttpStatusCode.NoContent, none.StatusCode);
        Assert.All(afterNone, tool => Assert.False(tool.Active));
    }

    [Fact]
    public async Task SendingTheSameToolTwiceActivatesItOnce()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var assistantId = await ToolsApi.DefaultAssistantIdAsync(client);
        var calculator = (await ToolsApi.GetAssistantToolsAsync(client, assistantId)).Pool.Single(t => t.Name == "builtin__calculator");

        using var response = await client.PutAsJsonAsync(ToolsApi.AssistantTools(assistantId), new { toolIds = new[] { calculator.ToolId, calculator.ToolId } }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Single((await ToolsApi.GetAssistantToolsAsync(client, assistantId)).Pool, t => t.Active);
    }

    [Fact] // AC-14
    public async Task ActivatingAToolOutsideThePoolIsRejected()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var assistantId = await ToolsApi.DefaultAssistantIdAsync(client);

        using var unknown = await client.PutAsJsonAsync(ToolsApi.AssistantTools(assistantId), new { toolIds = new[] { Guid.NewGuid() } }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("tool.not_in_pool", await ToolsApi.ProblemCodeAsync(unknown));
    }

    [Fact] // AC-14
    public async Task ADisabledToolLeavesThePoolAndCannotBeActivated()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var assistantId = await ToolsApi.DefaultAssistantIdAsync(client);
        var calculator = (await ToolsApi.ListToolsAsync(client)).Single(t => t.Name == "calculator");
        using var disable = await client.PutAsJsonAsync(ToolsApi.Tool(calculator.Id), new { state = "Disabled", policy = "Auto" }, Ct);

        var pool = (await ToolsApi.GetAssistantToolsAsync(client, assistantId)).Pool;
        using var activate = await client.PutAsJsonAsync(ToolsApi.AssistantTools(assistantId), new { toolIds = new[] { calculator.Id } }, Ct);

        Assert.DoesNotContain(pool, t => t.ToolId == calculator.Id);
        Assert.Equal(HttpStatusCode.BadRequest, activate.StatusCode);
        Assert.Equal("tool.not_in_pool", await ToolsApi.ProblemCodeAsync(activate));
    }

    [Fact]
    public async Task AnUnknownAssistantReturns404()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var unknown = Guid.NewGuid();

        using var read = await client.GetAsync(ToolsApi.AssistantTools(unknown), Ct);
        using var write = await client.PutAsJsonAsync(ToolsApi.AssistantTools(unknown), new { toolIds = Array.Empty<Guid>() }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, write.StatusCode);
    }

    [Fact] // AC-17
    public async Task DefinitionsThatTakeMostOfTheContextRaiseAWarningButBlockNothing()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync(contextTokenBudget: 256);
        var assistantId = await ToolsApi.DefaultAssistantIdAsync(client);

        var tools = await ToolsApi.GetAssistantToolsAsync(client, assistantId);

        var warning = Assert.Single(tools.Warnings);
        Assert.Equal("tools.definitions_large", warning.Code);
        Assert.False(string.IsNullOrWhiteSpace(warning.Message));
        Assert.All(tools.Pool, tool => Assert.True(tool.Active));
    }

    [Fact] // AC-17
    public async Task AGenerousContextRaisesNoWarning()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync(contextTokenBudget: 16384);
        var assistantId = await ToolsApi.DefaultAssistantIdAsync(client);

        var tools = await ToolsApi.GetAssistantToolsAsync(client, assistantId);

        Assert.Empty(tools.Warnings);
    }

    [Fact] // AC-17
    public async Task InactiveToolsDoNotCountForTheWarning()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync(contextTokenBudget: 256);
        var assistantId = await ToolsApi.DefaultAssistantIdAsync(client);
        using var none = await client.PutAsJsonAsync(ToolsApi.AssistantTools(assistantId), new { toolIds = Array.Empty<Guid>() }, Ct);

        var tools = await ToolsApi.GetAssistantToolsAsync(client, assistantId);

        Assert.Empty(tools.Warnings);
    }
}
