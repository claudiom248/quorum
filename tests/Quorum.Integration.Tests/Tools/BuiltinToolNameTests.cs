using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;
using static Quorum.Integration.Tests.Fixtures.FakeModelServer;

namespace Quorum.Integration.Tests.Tools;

/// <summary>A built-in tool is renamed like any other tool, and the model reads the new name (spec 015 D-10, AC-11 to AC-13).</summary>
public sealed class BuiltinToolNameTests(SharedQuorumApp host) : IClassFixture<SharedQuorumApp>, IAsyncLifetime
{
    public ValueTask InitializeAsync() => new(host.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<ToolsApi.ToolBody> BuiltinAsync(HttpClient client, string defaultName) =>
        (await ToolsApi.ListToolsAsync(client)).Single(t => t.Origin == "Builtin" && t.DefaultName == defaultName);

    private static Task<HttpResponseMessage> RenameAsync(HttpClient client, ToolsApi.ToolBody tool, string? name) =>
        client.PutAsJsonAsync(ToolsApi.Tool(tool.Id), new { state = tool.State, policy = tool.Policy, name }, Ct);

    [Fact]
    public async Task ABuiltinToolComesWithItsDefaultNameAndKeepsItWhenTheRequestOmitsTheName()
    {
        var client = host.Admin;
        var calculator = await BuiltinAsync(client, "calculator");

        using var response = await RenameAsync(client, calculator, name: null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var after = await BuiltinAsync(client, "calculator");
        Assert.Equal(("calculator", "calculator"), (after.Name, after.DefaultName));
    }

    [Fact] // AC-11
    public async Task RenamingABuiltinToolChangesTheNameEveryListShowsAndKeepsTheDefault()
    {
        var client = host.Admin;
        var calculator = await BuiltinAsync(client, "calculator");

        using var response = await RenameAsync(client, calculator, "Calc");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var renamed = await BuiltinAsync(client, "calculator");
        Assert.Equal(("Calc", "calculator"), (renamed.Name, renamed.DefaultName));
        var assistant = await ToolsApi.DefaultAssistantIdAsync(client);
        var pool = (await ToolsApi.GetAssistantToolsAsync(client, assistant)).Pool;
        Assert.Contains(pool, t => t.Name == "Calc" && t.Origin == "builtin");
        Assert.DoesNotContain(pool, t => t.Name == "calculator");
    }

    [Fact] // AC-11
    public async Task AnEmptyNameOrTheDefaultOneGivesTheDefaultBack()
    {
        var client = host.Admin;
        using var first = await RenameAsync(client, await BuiltinAsync(client, "calculator"), "Calc");
        using var empty = await RenameAsync(client, await BuiltinAsync(client, "calculator"), string.Empty);
        var afterEmpty = (await BuiltinAsync(client, "calculator")).Name;
        using var second = await RenameAsync(client, await BuiltinAsync(client, "calculator"), "Calc");
        using var sameAsDefault = await RenameAsync(client, await BuiltinAsync(client, "calculator"), "calculator");

        Assert.Equal("calculator", afterEmpty);
        Assert.All(new[] { first, empty, second, sameAsDefault }, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        Assert.Equal("calculator", (await BuiltinAsync(client, "calculator")).Name);
    }

    [Theory] // AC-5
    [InlineData("has space")]
    [InlineData("two__underscores")]
    [InlineData("-leading")]
    public async Task AnInvalidNameIsRefused(string name)
    {
        var client = host.Admin;
        var calculator = await BuiltinAsync(client, "calculator");

        using var response = await RenameAsync(client, calculator, name);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("calculator", (await BuiltinAsync(client, "calculator")).Name);
    }

    [Theory] // AC-6
    [InlineData("current_datetime")]
    [InlineData("Current_DateTime")]
    public async Task ANameThatAnotherToolAlreadyAnswersToIsRefusedWhateverTheCase(string name)
    {
        var client = host.Admin;
        var calculator = await BuiltinAsync(client, "calculator");

        using var response = await RenameAsync(client, calculator, name);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("calculator", (await BuiltinAsync(client, "calculator")).Name);
    }

    [Fact] // AC-11
    public async Task TheOldNameIsFreeOnceTheToolIsRenamed()
    {
        var client = host.Admin;
        using var renamed = await RenameAsync(client, await BuiltinAsync(client, "calculator"), "Calc");
        var clock = await BuiltinAsync(client, "current_datetime");

        using var response = await RenameAsync(client, clock, "calculator");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("calculator", (await BuiltinAsync(client, "current_datetime")).Name);
    }

    [Fact] // AC-12
    public async Task TheStateAndThePolicyAreUntouchedByARename()
    {
        var client = host.Admin;
        var calculator = await BuiltinAsync(client, "calculator");

        using var response = await RenameAsync(client, calculator, "Calc");

        var after = await BuiltinAsync(client, "calculator");
        Assert.Equal((calculator.State, calculator.Policy), (after.State, after.Policy));
    }

    [Fact] // AC-12
    public async Task OnlyABuiltinToolIsRenamedThroughThisRequest()
    {
        var client = host.Admin;
        // A web fetch tool is named in its own form, like every tool that is not a built-in one.
        using var created = await client.PostAsJsonAsync(
            new Uri("/api/tools/web-fetch", UriKind.Relative),
            new { name = "fetcher", description = "Reads pages.", scope = new { kind = "Tenant", tenantIds = Array.Empty<Guid>(), agentIds = Array.Empty<Guid>() } },
            Ct);
        created.EnsureSuccessStatusCode();
        var tool = (await ToolsApi.ListToolsAsync(client)).Single(t => t.Origin == "WebFetch");

        using var response = await RenameAsync(client, tool, "Other");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact] // AC-13
    public async Task TheModelIsOfferedTheNewNameAndACallToItRunsTheTool()
    {
        var client = host.Admin;
        var app = host.App;
        using var renamed = await RenameAsync(client, await BuiltinAsync(client, "calculator"), "Calc");
        await ToolFixtures.ActivateOnlyAsync(client, "Calc");
        app.ModelServer.EnqueueRounds(
            ChatScript.Reply("Computing. ") with { ToolCalls = [new FakeToolCall("Calc", JsonSerializer.Serialize(new { expression = "2+3" }), "call_1")] },
            ChatScript.Reply("It is 5."));
        var id = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, id, "What is 2+3?");

        var offered = app.ModelServer.ChatRequests[0].GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("function").GetProperty("name").GetString()).ToArray();
        Assert.Equal(["Calc"], offered);
        var call = Assert.Single(events, e => e.Type == "tool_call");
        Assert.Equal("Calc", call.Data.GetProperty("tool").GetString());
        var result = Assert.Single(events, e => e.Type == "tool_result");
        Assert.Equal("succeeded", result.Data.GetProperty("status").GetString());
        Assert.Equal("5", result.Data.GetProperty("content").GetString());
    }
}
