using System.Net;
using System.Text;
using Quorum.Integration.Tests.Attachments;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.Conversations.Telemetry;
using static Quorum.Integration.Tests.Telemetry.SpanTestHelpers;

namespace Quorum.Integration.Tests.Telemetry;

/// <summary>
/// The spans of the pipeline stages around an answer: attachment extraction (spec 016 D-8, AC-16) and what the database spans leave out
/// (D-8, D-9). One app with the export on serves the tests.
/// </summary>
[Collection("Telemetry")]
public sealed class PipelineSpanTests(SharedTelemetryApp host) : IClassFixture<SharedTelemetryApp>, IAsyncLifetime
{
    public ValueTask InitializeAsync() => new(host.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact] // AC-16
    public async Task AnAttachmentExtractionSpanHoldsItsFormatAndSizeButNotItsContent()
    {
        var conversation = await ChatApi.CreateConversationAsync(host.Admin);
        const string text = "Confidential clause about payment terms";

        using var upload = await AttachmentApi.UploadAsync(host.Admin, conversation, "notes.txt", text);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);

        var span = await WaitForSpanAsync(host.App, s => s.OperationName == ConversationsTelemetry.AttachmentExtractionActivityName);
        Assert.Equal(".txt", span.GetTagItem(ConversationsTelemetry.AttachmentFormatTag));
        Assert.Equal<object?>(Encoding.UTF8.GetByteCount(text), span.GetTagItem(ConversationsTelemetry.AttachmentSizeTag));
        Assert.DoesNotContain(span.TagObjects, tag => (tag.Value?.ToString() ?? string.Empty).Contains("Confidential", StringComparison.Ordinal));
        Assert.DoesNotContain(span.TagObjects, tag => (tag.Value?.ToString() ?? string.Empty).Contains("notes.txt", StringComparison.Ordinal));
    }

    [Fact] // spec 016 D-9: the connection details stay in the installation
    public async Task NoDatabaseSpanCarriesConnectionDetails()
    {
        var conversation = await ChatApi.CreateConversationAsync(host.Admin);

        await ChatApi.SendAsync(host.Admin, conversation, "Hello");

        var turn = await WaitForTurnAsync(host.App, conversation);
        var database = TraceOf(host.App, turn).Where(s => s.Source.Name == "Npgsql").ToList();
        Assert.NotEmpty(database);
        Assert.All(database, span =>
        {
            Assert.Null(span.GetTagItem("db.npgsql.data_source"));
        });
    }
}
