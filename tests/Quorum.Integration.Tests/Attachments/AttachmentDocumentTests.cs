using System.Net;
using System.Net.Http.Json;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;
using static Quorum.Integration.Tests.Attachments.DocumentFixtures;

namespace Quorum.Integration.Tests.Attachments;

/// <summary>PDF and DOCX attachments through the public API.</summary>
public sealed class AttachmentDocumentTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-1
    public async Task APdfIsAttachedAndItsTextReachesTheModel()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync(contextTokenBudget: 4096);
        var conversation = await ChatApi.CreateConversationAsync(client);

        using var upload = await AttachmentApi.UploadAsync(client, conversation, "offer.pdf", Pdf("Payment is due within thirty days"));
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var created = await upload.Content.ReadFromJsonAsync<AttachmentApi.AttachmentBody>(Ct);
        Assert.Equal("application/pdf", created!.MediaType);

        await ChatApi.SendAsync(client, conversation, "When is payment due?");

        var system = app.ModelServer.ChatRequests[^1].GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.Contains("offer.pdf", system, StringComparison.Ordinal);
        Assert.Contains("Payment is due within thirty days", system, StringComparison.Ordinal);
    }

    [Fact] // AC-10
    public async Task ADocxIsAttachedAsMarkdown()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync(contextTokenBudget: 4096);
        var conversation = await ChatApi.CreateConversationAsync(client);
        var docx = Docx(Paragraph("Quarterly report", style: "Heading1") + Paragraph("Revenue grew.") + Table(["Quarter", "Revenue"], ["Q1", "10"]));

        using var upload = await AttachmentApi.UploadAsync(client, conversation, "report.docx", docx);
        var created = await upload.Content.ReadFromJsonAsync<AttachmentApi.AttachmentBody>(Ct);
        var text = await client.GetStringAsync(AttachmentApi.Text(conversation, created!.Id), Ct);

        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        Assert.Equal("# Quarterly report\n\nRevenue grew.\n\n| Quarter | Revenue |\n| --- | --- |\n| Q1 | 10 |", text);
    }

    [Fact] // AC-6
    public async Task AScannedPdfIsRejectedWithAClearCode()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var conversation = await ChatApi.CreateConversationAsync(client);

        using var response = await AttachmentApi.UploadAsync(client, conversation, "scan.pdf", Pdf(string.Empty));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("attachment.no_text", await AttachmentApi.CodeAsync(response));
        Assert.Empty(await AttachmentApi.ListAsync(client, conversation));
    }

    [Fact] // AC-5
    public async Task AMacroEnabledDocumentIsRejected()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var conversation = await ChatApi.CreateConversationAsync(client);
        var withMacros = Docx(Paragraph("Invoice"), new Dictionary<string, byte[]> { ["word/vbaProject.bin"] = [1, 2, 3] });

        using var response = await AttachmentApi.UploadAsync(client, conversation, "invoice.docx", withMacros);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("attachment.unreadable", await AttachmentApi.CodeAsync(response));
        Assert.Empty(await AttachmentApi.ListAsync(client, conversation));
    }

    [Fact] // AC-14
    public async Task ALongPdfIsRejectedByTheBudgetLikeAnyOtherFile()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync(contextTokenBudget: 256);
        var conversation = await ChatApi.CreateConversationAsync(client);
        var pages = Enumerable.Range(1, 8).Select(i => $"Page {i} " + string.Join(' ', Enumerable.Repeat("lorem", 40))).ToArray();

        using var response = await AttachmentApi.UploadAsync(client, conversation, "long.pdf", Pdf(pages));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("attachment.exceeds_budget", await AttachmentApi.CodeAsync(response));
    }
}
