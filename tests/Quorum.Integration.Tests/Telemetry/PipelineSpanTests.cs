using System.Diagnostics;
using System.Net;
using System.Text;
using Quorum.Integration.Tests.Attachments;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.Agents.Telemetry;
using Quorum.Modules.Conversations.Telemetry;
using Quorum.Modules.KnowledgeBases.Telemetry;

namespace Quorum.Integration.Tests.Telemetry;

/// <summary>The spans of the pipeline stages around an answer: attachment extraction (spec 016 D-8, AC-16).</summary>
[Collection("Telemetry")]
public sealed class PipelineSpanTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-16
    public async Task AnAttachmentExtractionSpanHoldsItsFormatAndSizeButNotItsContent()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, telemetry: true);
        using var client = await app.SetUpAsync(toolsActive: false);
        app.Spans.Clear();
        var conversation = await ChatApi.CreateConversationAsync(client);
        const string text = "Confidential clause about payment terms";

        using var upload = await AttachmentApi.UploadAsync(client, conversation, "notes.txt", text);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);

        var span = await WaitForSpanAsync(app, s => s.OperationName == ConversationsTelemetry.AttachmentExtractionActivityName);
        Assert.Equal(".txt", span.GetTagItem(ConversationsTelemetry.AttachmentFormatTag));
        Assert.Equal<object?>(Encoding.UTF8.GetByteCount(text), span.GetTagItem(ConversationsTelemetry.AttachmentSizeTag));
        Assert.DoesNotContain(span.TagObjects, tag => (tag.Value?.ToString() ?? string.Empty).Contains("Confidential", StringComparison.Ordinal));
        Assert.DoesNotContain(span.TagObjects, tag => (tag.Value?.ToString() ?? string.Empty).Contains("notes.txt", StringComparison.Ordinal));
    }

    [Fact] // spec 016 D-7: background work is a trace of its own
    public async Task TheLookupWarmupAtStartupIsATraceOfItsOwn()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, telemetry: true);
        _ = app.Server;

        var warmup = await WaitForSpanAsync(app, s => s.OperationName == KnowledgeBasesTelemetry.LookupWarmupActivityName);
        Assert.Null(warmup.Parent);
        Assert.Equal<object?>(0, warmup.GetTagItem(KnowledgeBasesTelemetry.LookupListsTag));
    }

    [Fact] // spec 016 D-8, D-9: the SQL text and the connection details stay in the installation
    public async Task NoDatabaseSpanCarriesSqlTextOrConnectionDetails()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, telemetry: true);
        using var client = await app.SetUpAsync(toolsActive: false);
        app.Spans.Clear();
        var conversation = await ChatApi.CreateConversationAsync(client);

        await ChatApi.SendAsync(client, conversation, "Hello");

        await WaitForSpanAsync(app, s => s.OperationName == AgentsTelemetry.TurnActivityName);
        var database = app.Spans.Snapshot().Where(s => s.Source.Name == "Npgsql").ToList();
        Assert.NotEmpty(database);
        Assert.All(database, span =>
        {
            Assert.Null(span.GetTagItem("db.query.text"));
            Assert.Null(span.GetTagItem("db.npgsql.data_source"));
        });
    }

    private static async Task<Activity> WaitForSpanAsync(QuorumApp app, Func<Activity, bool> match)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (true)
        {
            var found = app.Spans.Snapshot().LastOrDefault(match);
            if (found is not null)
            {
                return found;
            }

            Assert.True(DateTimeOffset.UtcNow < deadline, "No matching span within 30 seconds.");
            await Task.Delay(20, Ct);
        }
    }
}
