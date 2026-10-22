using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Web.Client;
using Quorum.Web.Client.Api;
using MudBlazor.Services;

namespace Quorum.Integration.Tests.Web;

/// <summary>The sources under an answer in the chat: while it streams and when the conversation is reopened (spec 003, AC-40, AC-50).</summary>
public sealed class SourcesChatPageTests : BunitContext
{
    private static readonly Guid Conversation = Guid.Parse("01a14117-8443-7208-890d-b78925ebb291");
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly FakeApiHandler _api = new();
    private readonly Pipe _stream = new();
    private object[] _stored = [];

    public SourcesChatPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(TimeProvider.System);
        var client = new QuorumApi(_api.CreateClient());
        Services.AddSingleton(client);
        Services.AddSingleton(new Session(client));

        _api.Respond = (method, path) =>
        {
            if (method == HttpMethod.Post && path == "/api/conversations")
            {
                return FakeApiHandler.Json(new { id = Conversation }, HttpStatusCode.Created);
            }

            if (method == HttpMethod.Post && path.EndsWith("/messages", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(_stream.Reader.AsStream()) { Headers = { ContentType = new("text/event-stream") } } };
            }

            if (method == HttpMethod.Get && path == $"/api/conversations/{Conversation}")
            {
                return FakeApiHandler.Json(new { id = Conversation, title = "Hi", messages = _stored, attachments = Array.Empty<object>() });
            }

            if (method == HttpMethod.Get && (path == "/api/conversations" || path.EndsWith("/attachments", StringComparison.Ordinal)))
            {
                return FakeApiHandler.Json(Array.Empty<object>());
            }

            if (path.EndsWith("/context", StringComparison.Ordinal))
            {
                return FakeApiHandler.Json(new ConversationContext(4096, 3276, 48, 0, 20, 3200));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
    }

    private static object Source(int n, string title, string? url = null) =>
        new { n, knowledgeBase = "handbook", documentId = $"doc-{n}", title, position = (string?)null, url };

    private static object Assistant(string content, params object[] sources) =>
        new { id = Guid.NewGuid(), role = "Assistant", content, status = "Completed", createdAt = DateTimeOffset.UnixEpoch, sources };

    private async Task PushAsync(string eventName, object data)
    {
        var text = $"event: {eventName}\ndata: {JsonSerializer.Serialize(data, Web)}\n\n";
        await _stream.Writer.WriteAsync(Encoding.UTF8.GetBytes(text), Xunit.TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SourcesAppearWhileTheAnswerStreamsAndStayAfterItIsStored()
    {
        var page = Render<Quorum.Web.Client.Pages.Chat>();
        page.Find("textarea").Input("What is the leave policy?");
        var sending = page.InvokeAsync(() => page.Instance.SendFromKeyboard());
        await Task.Yield();

        await PushAsync("sources", new { sources = new[] { Source(1, "Leave policy", "https://example.com/leave"), Source(2, "Holidays") } });
        await PushAsync("delta", new { text = "You get 25 days [1]." });

        page.WaitForAssertion(() =>
        {
            var entries = page.FindAll(".sources .sources-list li");
            Assert.Equal(2, entries.Count);
            Assert.Contains("Leave policy", entries[0].TextContent, StringComparison.Ordinal);
            Assert.Single(page.FindAll(".sources-others"));
        });

        _stored =
        [
            new { id = Guid.NewGuid(), role = "User", content = "What is the leave policy?", status = "Completed", createdAt = DateTimeOffset.UnixEpoch, sources = Array.Empty<object>() },
            Assistant("You get 25 days [1].", Source(1, "Leave policy", "https://example.com/leave"), Source(2, "Holidays")),
        ];
        await PushAsync("done", new { messageId = Guid.NewGuid(), status = "Completed", error = (string?)null });
        _stream.Writer.Complete();
        await sending;

        page.WaitForAssertion(() =>
        {
            var cited = page.Find(".sources > .sources-list li");
            Assert.Equal("[1]", cited.QuerySelector(".source-n")!.TextContent);
            Assert.Equal("Leave policy", cited.QuerySelector("a")!.TextContent);
            Assert.Equal("handbook", cited.QuerySelector(".source-detail")!.TextContent);
            Assert.Equal("[2]", page.Find(".sources-others li .source-n").TextContent);
        });
    }

    [Fact]
    public void ReopeningAConversationShowsTheStoredSourcesOfEachAnswer()
    {
        _stored =
        [
            Assistant("First [1].", Source(1, "One")),
            Assistant("Second, no citations.", Source(1, "Other")),
            Assistant("No sources at all."),
        ];

        var page = Render<Quorum.Web.Client.Pages.Chat>(parameters => parameters.Add(chat => chat.Id, Conversation));

        page.WaitForAssertion(() =>
        {
            var lists = page.FindAll(".sources");
            Assert.Equal(2, lists.Count);
            Assert.Equal("One", Normalize(lists[0].QuerySelector(".sources-list li .source-label")!.TextContent));
            Assert.Null(lists[1].QuerySelector(":scope > .sources-list"));
            Assert.Contains("Sources (1)", lists[1].QuerySelector("summary")!.TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void SourcesOfLocalFilesAreGroupedByDocumentNameTheFileAndShowTheSamePlaceOnce()
    {
        // The duplicate [4] is an answer stored before the server gave one number to a source.
        _stored =
        [
            Assistant(
                "Core hours are ten to four [4], and the desk is paid for [3].",
                LocalSource(1, "Remote work guidelines", "remote-work.html", "Core hours"),
                LocalSource(2, "Security handbook", "security-handbook.html", "Passwords"),
                LocalSource(3, "Remote work guidelines", "remote-work.html", "Equipment"),
                LocalSource(4, "Remote work guidelines", "remote-work.html", "Core hours")),
        ];

        var page = Render<Quorum.Web.Client.Pages.Chat>(parameters => parameters.Add(chat => chat.Id, Conversation));

        page.WaitForAssertion(() =>
        {
            Assert.Empty(page.FindAll(".sources a"));

            var document = page.Find(".sources > .sources-list > li");
            Assert.Equal("Remote work guidelines", document.QuerySelector(".source-doc .source-label")!.TextContent);
            Assert.Equal("handbook · File: remote-work.html", document.QuerySelector(".source-doc .source-detail")!.TextContent);
            var sections = document.QuerySelectorAll(".source-sections li").Select(li => (li.QuerySelector(".source-n")!.TextContent, li.QuerySelector(".source-position")!.TextContent)).ToList();
            Assert.Equal([("[1] [4]", "Section: Core hours"), ("[3]", "Section: Equipment")], sections);

            var others = page.Find(".sources-others");
            Assert.Contains("(1)", others.QuerySelector("summary")!.TextContent, StringComparison.Ordinal);
            Assert.Equal("Security handbook", others.QuerySelector(".source-doc .source-label")!.TextContent);
            Assert.Equal("[2]", others.QuerySelector(".source-sections .source-n")!.TextContent);
            Assert.Equal("Section: Passwords", others.QuerySelector(".source-sections .source-position")!.TextContent);
            Assert.Single(page.FindAll(".sources .source-label"), label => label.TextContent == "Remote work guidelines");
        });
    }

    private static object LocalSource(int n, string title, string file, string position) =>
        new { n, knowledgeBase = "handbook", documentId = file, title, position, url = $"file:///srv/documents/{file}" };

    [Fact]
    public void OnlyWebAddressesAreLinksAndTheTextIsNeverMarkup()
    {
        _stored =
        [
            Assistant(
                "See [1] [2].",
                Source(1, "<img src=x onerror=alert(1)>", "javascript:alert(1)"),
                Source(2, "Safe", "https://example.com/safe")),
        ];

        var page = Render<Quorum.Web.Client.Pages.Chat>(parameters => parameters.Add(chat => chat.Id, Conversation));

        page.WaitForAssertion(() =>
        {
            Assert.Empty(page.FindAll(".sources img"));
            var links = page.FindAll(".sources a");
            var link = Assert.Single(links);
            Assert.Equal("https://example.com/safe", link.GetAttribute("href"));
            Assert.Equal("noopener noreferrer", link.GetAttribute("rel"));
            Assert.Contains("<img src=x onerror=alert(1)>", page.Find(".sources .source-label").TextContent, StringComparison.Ordinal);
        });
    }

    private static string Normalize(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
