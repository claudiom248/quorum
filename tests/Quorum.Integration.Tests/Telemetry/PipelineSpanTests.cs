using System.Diagnostics;
using System.Net;
using System.Text;
using Quorum.Integration.Tests.Attachments;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.Conversations.Telemetry;

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
