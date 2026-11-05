using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.KnowledgeBases.Telemetry;
using static Quorum.Integration.Tests.Telemetry.SpanTestHelpers;

namespace Quorum.Integration.Tests.Telemetry;

/// <summary>Work that runs outside any request is a trace of its own (spec 016 D-7). The warm-up happens when the app starts, so the test starts its own.</summary>
[Collection("Telemetry")]
public sealed class BackgroundSpanTests(PostgresFixture postgres)
{
    [Fact] // spec 016 D-7
    public async Task TheLookupWarmupAtStartupIsATraceOfItsOwn()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, telemetry: true);
        _ = app.Server;

        var warmup = await WaitForSpanAsync(app, s => s.OperationName == KnowledgeBasesTelemetry.LookupWarmupActivityName);
        Assert.Null(warmup.Parent);
        Assert.Equal<object?>(0, warmup.GetTagItem(KnowledgeBasesTelemetry.LookupListsTag));
    }
}
