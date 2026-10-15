using System.Text;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Web.Client;
using Quorum.Web.Client.Api;

namespace Quorum.Integration.Tests.Web;

/// <summary>The web client's side of attachments: the typed API client against the real server, and the view logic.</summary>
public sealed class AttachmentClientTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MemoryStream Utf8(string text) => new(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task TheClientUploadsListsPreviewsAndRemovesAnAttachment()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var http = await app.SetUpAsync();
        var api = new QuorumApi(http);
        var conversation = (await api.CreateConversationAsync(Ct)).Value;

        var uploaded = await api.UploadAttachmentAsync(conversation, Utf8("Hello attachment"), "notes.txt", Ct);
        var listed = await api.ListAttachmentsAsync(conversation, Ct);
        var text = await api.GetAttachmentTextAsync(conversation, uploaded.Value.Id, Ct);
        var removed = await api.RemoveAttachmentAsync(conversation, uploaded.Value.Id, Ct);
        var after = await api.ListAttachmentsAsync(conversation, Ct);

        Assert.True(uploaded.IsSuccess);
        Assert.Equal("notes.txt", uploaded.Value.FileName);
        Assert.Equal(16, uploaded.Value.CharacterCount);
        Assert.Equal([uploaded.Value.Id], listed.Value.Select(a => a.Id));
        Assert.Equal("Hello attachment", text.Value);
        Assert.True(removed.IsSuccess);
        Assert.Empty(after.Value);
    }

    [Fact]
    public async Task ARejectedUploadCarriesTheServersCodeAndMessage()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var http = await app.SetUpAsync();
        var api = new QuorumApi(http);
        var conversation = (await api.CreateConversationAsync(Ct)).Value;

        var result = await api.UploadAttachmentAsync(conversation, new MemoryStream([0x89, 0x50, 0x4E, 0x47]), "image.png", Ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(415, result.Problem!.Status);
        Assert.Equal("attachment.unsupported_type", result.Problem.Code);
        Assert.Contains("PDF", result.Problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheContextShowsTheOverallBudgetUsage()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var http = await app.SetUpAsync(contextTokenBudget: 4096);
        var api = new QuorumApi(http);
        var conversation = (await api.CreateConversationAsync(Ct)).Value;
        await api.UploadAttachmentAsync(conversation, Utf8(new string('d', 4000)), "a.txt", Ct);

        var context = await api.GetContextAsync(conversation, Ct);

        Assert.True(context.IsSuccess);
        Assert.Equal(4096, context.Value.ContextTokenBudget);
        Assert.True(context.Value.AttachmentsTokens >= 1000);
        Assert.True(context.Value.RemainingTokens > 0);
    }

    [Fact]
    public async Task TheConversationDetailListsItsAttachments()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var http = await app.SetUpAsync();
        var api = new QuorumApi(http);
        var conversation = (await api.CreateConversationAsync(Ct)).Value;
        await api.UploadAttachmentAsync(conversation, Utf8("content"), "a.md", Ct);

        var detail = await api.GetConversationAsync(conversation, Ct);

        Assert.Equal(["a.md"], detail.Value.Attachments!.Select(a => a.FileName));
    }

    [Theory]
    [InlineData(0, 8192, 0.0, MeterLevel.Normal)]
    [InlineData(2000, 8192, 24.4, MeterLevel.Normal)]
    [InlineData(6200, 8192, 75.7, MeterLevel.Warning)]
    [InlineData(7600, 8192, 92.8, MeterLevel.Critical)]
    [InlineData(9000, 8192, 100.0, MeterLevel.Over)]
    public void TheMeterShowsHowMuchOfTheContextIsUsed(int used, int usable, double percent, MeterLevel level)
    {
        var context = new ConversationContext(
            ContextTokenBudget: 10240,
            UsableTokens: usable,
            SystemPromptTokens: 0,
            AttachmentsTokens: used,
            MessagesTokens: 0,
            RemainingTokens: usable - used);

        var meter = ContextMeter.From(context);

        Assert.Equal(percent, meter.Percent, precision: 1);
        Assert.Equal(level, meter.Level);
    }

    [Fact]
    public void TheMeterLabelNamesWhatIsUsedAndWhatIsLeft()
    {
        var context = new ConversationContext(16384, 13107, SystemPromptTokens: 50, AttachmentsTokens: 3000, MessagesTokens: 200, RemainingTokens: 9857);

        var meter = ContextMeter.From(context);

        Assert.Equal("3.3k of 13.1k tokens used", meter.Label);
        Assert.Contains("attachments 3.0k", meter.Details, StringComparison.Ordinal);
        Assert.Contains("messages 200", meter.Details, StringComparison.Ordinal);
        Assert.Contains("9.9k left", meter.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOverfullContextSaysSo()
    {
        var context = new ConversationContext(256, 204, SystemPromptTokens: 10, AttachmentsTokens: 300, MessagesTokens: 0, RemainingTokens: -106);

        var meter = ContextMeter.From(context);

        Assert.Equal(MeterLevel.Over, meter.Level);
        Assert.Contains("over by", meter.Details, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("report.pdf", 1000, null)]
    [InlineData("Report.DOCX", 1000, null)]
    [InlineData("notes.txt", 1000, null)]
    [InlineData("readme.md", 1000, null)]
    [InlineData("data.csv", 1000, null)]
    [InlineData("sheet.xlsx", 1000, "type")]
    [InlineData("photo.png", 1000, "type")]
    [InlineData("noextension", 1000, "type")]
    [InlineData("big.pdf", 6 * 1024 * 1024, "large")]
    public void TheUiRefusesWhatTheServerWouldRefuseBeforeUploading(string fileName, long size, string? problem)
    {
        var message = AttachmentRules.Check(fileName, size);

        if (problem is null)
        {
            Assert.Null(message);
        }
        else
        {
            Assert.NotNull(message);
            Assert.Contains(problem == "type" ? "PDF" : "5 MB", message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(900, "900 B")]
    [InlineData(2048, "2.0 KB")]
    [InlineData(5 * 1024 * 1024, "5.0 MB")]
    public void FileSizesAreHumanReadable(long bytes, string expected)
    {
        Assert.Equal(expected, AttachmentRules.FormatSize(bytes));
    }
}
