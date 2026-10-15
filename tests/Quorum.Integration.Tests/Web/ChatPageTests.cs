using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Quorum.Web.Client;
using Quorum.Web.Client.Api;

namespace Quorum.Integration.Tests.Web;

/// <summary>The chat page's behaviour while an answer arrives and after it ended.</summary>
public sealed class ChatPageTests : BunitContext
{
    private static readonly Guid Conversation = Guid.Parse("01a14117-8443-7208-890d-b78925ebb291");

    private readonly FakeApiHandler _api = new();

    public ChatPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(TimeProvider.System);
        var client = new QuorumApi(_api.CreateClient());
        Services.AddSingleton(client);
        Services.AddSingleton(new Session(client));

        var answered = false;
        _api.Respond = (method, path) =>
        {
            if (method == HttpMethod.Post && path == "/api/conversations")
            {
                return FakeApiHandler.Json(new { id = Conversation }, HttpStatusCode.Created);
            }

            if (method == HttpMethod.Post && path.EndsWith("/messages", StringComparison.Ordinal))
            {
                answered = true;
                return FakeApiHandler.Answer("Hello from the model");
            }

            if (method == HttpMethod.Get && path == $"/api/conversations/{Conversation}")
            {
                return FakeApiHandler.Json(new
                {
                    id = Conversation,
                    title = "Hi",
                    messages = answered
                        ? new[]
                        {
                            new { id = Guid.NewGuid(), role = "User", content = "Hi", status = "Completed", createdAt = DateTimeOffset.UnixEpoch },
                            new { id = Guid.NewGuid(), role = "Assistant", content = "Stored: Hello from the model", status = "Completed", createdAt = DateTimeOffset.UnixEpoch },
                        }
                        : [],
                    attachments = Array.Empty<object>(),
                });
            }

            if (method == HttpMethod.Get && path == "/api/conversations")
            {
                return FakeApiHandler.Json(Array.Empty<object>());
            }

            if (path.EndsWith("/attachments", StringComparison.Ordinal))
            {
                return FakeApiHandler.Json(Array.Empty<object>());
            }

            if (path.EndsWith("/context", StringComparison.Ordinal))
            {
                return FakeApiHandler.Json(new ConversationContext(4096, 3276, 48, 0, 20, 3208));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
    }

    [Fact]
    public async Task AfterAnAnswerSentWithTheEnterKeyThePageShowsItsFinalState()
    {
        var page = Render<Quorum.Web.Client.Pages.Chat>();
        page.Find("textarea").Input("Hi");

        // The Enter key handler runs outside of any button handler: nothing re-renders the page for it afterwards.
        await page.InvokeAsync(() => page.Instance.SendFromKeyboard());

        page.WaitForAssertion(() =>
        {
            // The stored answer, loaded when the stream ended: the text that was streamed must have been replaced by it.
            Assert.Contains("Stored: Hello from the model", page.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("Stop", page.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("Thinking", page.Markup, StringComparison.Ordinal);
            Assert.False(page.Find("textarea").HasAttribute("disabled"));
        });
    }

    [Fact]
    public async Task APressedEnterTwiceSendsTheMessageOnce()
    {
        var page = Render<Quorum.Web.Client.Pages.Chat>();
        page.Find("textarea").Input("Hi");

        await page.InvokeAsync(async () =>
        {
            var first = page.Instance.SendFromKeyboard();
            var second = page.Instance.SendFromKeyboard();
            await Task.WhenAll(first, second);
        });

        page.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Post, "/messages")));
        Assert.Equal(1, _api.Requests.Count(r => r.Method == HttpMethod.Post && r.Path == "/api/conversations"));
    }
}
