using System.Text.Json;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.Tools;
using static Quorum.Integration.Tests.Fixtures.FakeModelServer;
using static Quorum.Integration.Tests.Telemetry.SpanTestHelpers;

namespace Quorum.Integration.Tests.Telemetry;

/// <summary>
/// How many queries an answer makes, and that the assistant graph kept between turns follows what an administrator changes (the query
/// volume refactor, docs/query-volume-refactor.md). The queries are counted as the database spans of the trace of the turn.
/// </summary>
[Collection("Telemetry")]
public sealed class QueryVolumeTests(SharedTelemetryApp host) : IClassFixture<SharedTelemetryApp>, IAsyncLifetime
{
    public ValueTask InitializeAsync() => new(host.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static bool OffersTools(JsonElement request) => request.TryGetProperty("tools", out var tools) && tools.GetArrayLength() > 0;

    private static async Task<int> DatabaseSpansOfAnswerAsync(SharedTelemetryApp host, string text)
    {
        var conversation = await ChatApi.CreateConversationAsync(host.Admin);
        await ChatApi.SendAsync(host.Admin, conversation, text);

        var turn = await WaitForTurnAsync(host.App, conversation);
        return TraceOf(host.App, turn).Count(span => span.Source.Name == "Npgsql");
    }

    [Fact]
    public async Task AnAnswerMakesFewerQueriesOnceTheGraphIsKept()
    {
        await ToolFixtures.ActivateOnlyAsync(host.Admin, "calculator", "current_datetime");

        var first = await DatabaseSpansOfAnswerAsync(host, "Hello");
        var second = await DatabaseSpansOfAnswerAsync(host, "Hello again");

        Assert.True(second < first, $"The first answer made {first} queries and the second {second}.");
        Assert.True(second <= 8, $"A plain answer made {second} queries once the graph was kept.");
    }

    [Fact]
    public async Task ATurnOffersTheToolsAnAdministratorActivatedSinceTheLastOne()
    {
        await ToolFixtures.ActivateOnlyAsync(host.Admin, "calculator");
        var conversation = await ChatApi.CreateConversationAsync(host.Admin);
        await ChatApi.SendAsync(host.Admin, conversation, "One");
        await WaitForTurnAsync(host.App, conversation);
        Assert.True(OffersTools(host.App.ModelServer.ChatRequests[^1]));

        await ToolFixtures.ActivateOnlyAsync(host.Admin);
        var next = await ChatApi.CreateConversationAsync(host.Admin);
        await ChatApi.SendAsync(host.Admin, next, "Two");
        await WaitForTurnAsync(host.App, next);

        Assert.False(OffersTools(host.App.ModelServer.ChatRequests[^1]));
    }
}
