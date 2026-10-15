using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Quorum.Web.Client.Api;
using Quorum.Web.Client.Components;

namespace Quorum.Integration.Tests.Web;

/// <summary>The composer's behaviour in a component host: file selection, the context pill and the number of requests.</summary>
public sealed class ComposerCardTests : BunitContext
{
    private static readonly Guid Conversation = Guid.Parse("01a14117-8443-7208-890d-b78925ebb291");

    private readonly FakeApiHandler _api = new();
    private readonly List<object> _attachments = [];

    public ComposerCardTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(new QuorumApi(_api.CreateClient()));

        _api.Respond = (method, path) =>
        {
            if (path.EndsWith("/attachments", StringComparison.Ordinal))
            {
                if (method == HttpMethod.Post)
                {
                    var created = Attachment($"file{_attachments.Count + 1}.txt");
                    _attachments.Add(created);
                    return FakeApiHandler.Json(created, HttpStatusCode.Created);
                }

                return FakeApiHandler.Json(_attachments);
            }

            if (path.Contains("/attachments/", StringComparison.Ordinal) && method == HttpMethod.Delete)
            {
                _attachments.Clear();
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (path.EndsWith("/context", StringComparison.Ordinal))
            {
                return FakeApiHandler.Json(new ConversationContext(4096, 3276, 48, 2100, 0, 1128));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
    }

    private static AttachmentInfo Attachment(string name) =>
        new(Guid.NewGuid(), name, "text/plain", 1200, 1200, DateTimeOffset.UnixEpoch);

    private IRenderedComponent<ComposerCard> RenderCard(
        Guid? conversation = null,
        IReadOnlyList<AttachmentInfo>? initial = null) =>
        Render<ComposerCard>(parameters => parameters
            .Add(p => p.ConversationId, conversation)
            .Add(p => p.InitialAttachments, initial)
            .Add(p => p.Input, (RenderFragment)(builder => builder.AddContent(0, "message box")))
            .Add(p => p.Actions, (RenderFragment)(builder => builder.AddContent(0, "send"))));

    [Fact]
    public void MoreThanTenFilesAreRefusedWithAMessageAndNothingIsUploaded()
    {
        var card = RenderCard(Conversation);
        var files = Enumerable.Range(1, 11).Select(i => InputFileContent.CreateFromText("text", $"f{i}.txt")).ToArray();

        card.FindComponent<InputFile>().UploadFiles(files);

        card.WaitForAssertion(() => Assert.Contains("at most 10 files", card.Markup, StringComparison.Ordinal));
        Assert.Equal(0, _api.Count(HttpMethod.Post, "/attachments"));
    }

    [Fact]
    public void ThePickerIsRecreatedAfterEachSelectionSoTheSameFileCanBeChosenAgain()
    {
        var card = RenderCard(Conversation);
        var before = card.FindComponent<InputFile>().Instance;

        card.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("text", "same.txt"));
        card.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Post, "/attachments")));

        card.WaitForAssertion(() => Assert.NotSame(before, card.FindComponent<InputFile>().Instance));
    }

    [Fact]
    public void ARemovedFileCanBeAttachedAgain()
    {
        var card = RenderCard(Conversation);
        card.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("text", "again.txt"));
        card.WaitForAssertion(() => Assert.Contains("file1.txt", card.Markup, StringComparison.Ordinal));

        card.Find("button.file-remove[aria-label^='Remove']").Click();
        card.WaitForAssertion(() => Assert.DoesNotContain("file1.txt", card.Markup, StringComparison.Ordinal));
        card.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("text", "again.txt"));

        card.WaitForAssertion(() => Assert.Equal(2, _api.Count(HttpMethod.Post, "/attachments")));
    }

    [Fact]
    public void TheAttachControlCanBeReachedByKeyboard()
    {
        var card = RenderCard(Conversation);

        var input = card.Find("label.tool-button input[type=file]");

        // display:none would take the input out of the tab order.
        Assert.DoesNotContain("display:none", input.GetAttribute("style") ?? string.Empty, StringComparison.Ordinal);
        Assert.False(input.HasAttribute("disabled"));
    }

    [Fact]
    public void AttachmentsLoadedWithTheConversationAreNotRequestedAgain()
    {
        var card = RenderCard(Conversation, [Attachment("loaded.pdf")]);

        card.WaitForAssertion(() => Assert.Contains("loaded.pdf", card.Markup, StringComparison.Ordinal));
        Assert.Equal(0, _api.Count(HttpMethod.Get, "/attachments"));
        Assert.Equal(1, _api.Count(HttpMethod.Get, "/context"));
    }

    [Fact]
    public void ThePillSummarisesTheContextAndOpensTheDetail()
    {
        var card = RenderCard(Conversation, [Attachment("a.txt")]);
        card.WaitForAssertion(() => Assert.Contains("of 3.3k tokens", card.Markup, StringComparison.Ordinal));

        card.Find("button.context-pill").Click();

        Assert.Contains("Context window", card.Markup, StringComparison.Ordinal);
        Assert.Contains("System prompt", card.Markup, StringComparison.Ordinal);
        Assert.Contains("a.txt", card.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReloadingTheContextDoesNotRequestTheAttachmentsAgain()
    {
        var card = RenderCard(Conversation, [Attachment("a.txt")]);
        card.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Get, "/context")));

        await card.InvokeAsync(() => card.Instance.ReloadContextAsync());

        Assert.Equal(2, _api.Count(HttpMethod.Get, "/context"));
        Assert.Equal(0, _api.Count(HttpMethod.Get, "/attachments"));
    }
}
