using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Quorum.Web.Client;
using Quorum.Web.Client.Components;

namespace Quorum.Integration.Tests.Web;

/// <summary>
/// The panel's state machine in a component host (spec 014): title and landmark, the sandboxed frame, the limit and
/// failure notices, close and Esc, the consent dialogs of the two HTML switches and what a new preview resets.
/// </summary>
public sealed class PreviewPanelTests : BunitContext
{
    private const string User = "user@test";

    private readonly IRenderedComponent<MudPopoverProvider> _popovers;
    private readonly IRenderedComponent<MudDialogProvider> _dialogs;
    private int _closed;

    public PreviewPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddScoped<ThemeState>();
        JSInterop.Setup<Dictionary<string, string>>("quorumPreview.themeColors").SetResult(new Dictionary<string, string>());

        _popovers = Render<MudPopoverProvider>();
        _dialogs = Render<MudDialogProvider>();
    }

    private static PreviewContent Html(string text = "<p>x</p>") => new(PreviewKind.Html, text);

    private static PreviewContent Json(string text = """{"a":1}""") => new(PreviewKind.Json, text);

    private IRenderedComponent<PreviewPanel> Open(PreviewContent content) =>
        Render<PreviewPanel>(parameters => parameters
            .Add(p => p.Content, content)
            .Add(p => p.UserKey, User)
            .Add(p => p.OnClosed, EventCallback.Factory.Create(this, () => _closed++)));

    [Fact]
    public void ThePanelIsALabelledLandmarkAndTheBarNamesTheKindAndTheSize()
    {
        var panel = Open(Json());

        var section = panel.Find("section.chat-preview");
        Assert.Equal("complementary", section.GetAttribute("role"));
        Assert.Equal("Content preview", section.GetAttribute("aria-label"));
        Assert.Contains("JSON · 7 B", panel.Markup, StringComparison.Ordinal);
        Assert.Contains("Preview of model output", panel.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheViewerFrameIsSandboxedWithScriptsOnlyAndNamedForTheKind()
    {
        var panel = Open(Json());

        var frame = panel.Find("iframe.preview-frame");
        // The viewer document arrives as srcdoc, built by the host (spec 014, D-6): no URL of the app is loaded.
        Assert.Null(frame.GetAttribute("src"));
        Assert.Equal("allow-scripts", frame.GetAttribute("sandbox"));
        Assert.Contains("JSON", frame.GetAttribute("title"), StringComparison.Ordinal);
    }

    [Fact]
    public void ABlockAboveTheLimitShowsTheNoticeAndCreatesNoViewerAtAll()
    {
        var panel = Open(Json(new string('a', PreviewRules.MaxTextBytes + 1)));

        Assert.Contains("preview limit", panel.Markup, StringComparison.Ordinal);
        Assert.Empty(panel.FindAll("iframe"));
        // AC-35: no viewer means no attach and nothing vendored is ever requested.
        Assert.DoesNotContain(JSInterop.Invocations, call => call.Identifier == "quorumPreview.attachViewer");
        Assert.DoesNotContain(JSInterop.Invocations, call => call.Identifier == "quorumPreview.loadViewer");
        // AC-32: Copy and Download still work.
        Assert.NotEmpty(panel.FindAll("button[aria-label='Copy the raw text']"));
        Assert.NotEmpty(panel.FindAll("button[aria-label='Download the raw text']"));
    }

    [Fact]
    public void CopyAndDownloadHandTheRawTextToThePageWithTheRightFileName()
    {
        var panel = Open(new PreviewContent(PreviewKind.Mermaid, "graph TD;\nA-->B;"));

        panel.Find("button[aria-label='Copy the raw text']").Click();
        panel.Find("button[aria-label='Download the raw text']").Click();

        Assert.Contains(JSInterop.Invocations, call =>
            call.Identifier == "quorumPreview.copyText" && Equals(call.Arguments[0], "graph TD;\nA-->B;"));
        Assert.Contains(JSInterop.Invocations, call =>
            call.Identifier == "quorumPreview.download" && Equals(call.Arguments[0], "preview.mmd"));
    }

    [Fact]
    public async Task XAndEscapeBothAskThePageToCloseThePanel()
    {
        var panel = Open(Json());

        panel.Find("button[aria-label='Close the preview']").Click();
        Assert.Equal(1, _closed);

        panel.Find("section.chat-preview").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        Assert.Equal(2, _closed);

        // Esc pressed inside the viewer arrives over the protocol (D-3).
        await panel.InvokeAsync(() => panel.Instance.OnPreviewEscape());
        Assert.Equal(3, _closed);
    }

    [Fact]
    public void OnlyHtmlPreviewsCarryTheTwoConsentSwitches()
    {
        Assert.Equal(2, Open(Html()).FindComponents<MudSwitch<bool>>().Count);
        Assert.Empty(Open(Json()).FindComponents<MudSwitch<bool>>());
    }

    [Fact]
    public void TurningRunScriptsOnAsksAndCancelLeavesEverythingAsItWas()
    {
        var panel = Open(Html());
        var rendersBefore = RenderCalls.Count;

        TurnOn(panel, "Run scripts");
        WaitConsentDialog("Run this block's scripts?");

        ClickButton(_dialogs, "Cancel");

        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindComponents<PreviewConsentDialog>()));
        // Cancel re-renders nothing: the viewer keeps the frame it already has (AC-28).
        Assert.Equal(rendersBefore, RenderCalls.Count);
    }

    [Fact]
    public void ConfirmingRecreatesTheRenderWithScriptsAndOnlyForThisPreview()
    {
        var panel = Open(Html());

        TurnOn(panel, "Run scripts");
        WaitConsentDialog("Run this block's scripts?");
        ClickButton(_dialogs, "Turn on");

        panel.WaitForAssertion(() => Assert.Contains("\"runScripts\":true", LastRenderPayload(), StringComparison.Ordinal));

        // A new preview starts with both switches off again, whatever the previous one had (D-17, AC-28).
        panel.Render(p => p.Add(x => x.Content, Html("<b>other</b>")));
        panel.WaitForAssertion(() => Assert.Contains("\"runScripts\":false", LastRenderPayload(), StringComparison.Ordinal));
    }

    [Fact]
    public void TheTwoSwitchesAskSeparatelyAndStoreSeparately()
    {
        var panel = Open(Html());

        TurnOn(panel, "Load remote images");
        WaitConsentDialog("Load this block's remote images?");

        _dialogs.FindComponent<MudCheckBox<bool>>().Find("input").Change(true);
        ClickButton(_dialogs, "Turn on");

        var key = PreviewConsent.StorageKey(User, PreviewConsentSwitch.RemoteImages);
        Assert.Contains(JSInterop.Invocations, call =>
            call.Identifier == "quorumPreview.storageSet" &&
            Equals(call.Arguments[0], key) &&
            Equals(call.Arguments[1], "1"));
        panel.WaitForAssertion(() => Assert.Contains("\"loadRemoteImages\":true", LastRenderPayload(), StringComparison.Ordinal));
    }

    [Fact]
    public void TheStoredChoiceSkipsTheDialogAndTheMenuOffersToTakeItBack()
    {
        JSInterop.Setup<string>("quorumPreview.storageGet", PreviewConsent.StorageKey(User, PreviewConsentSwitch.RunScripts)).SetResult("1");
        var panel = Open(Html());

        TurnOn(panel, "Run scripts");

        Assert.Empty(_dialogs.FindComponents<PreviewConsentDialog>());
        panel.WaitForAssertion(() => Assert.Contains("\"runScripts\":true", LastRenderPayload(), StringComparison.Ordinal));
        Assert.NotEmpty(panel.FindComponents<MudMenu>());
    }

    [Fact]
    public void AskAgainRemovesTheStoredChoice()
    {
        JSInterop.Setup<string>("quorumPreview.storageGet", PreviewConsent.StorageKey(User, PreviewConsentSwitch.RunScripts)).SetResult("1");
        var panel = Open(Html());

        panel.FindComponents<MudMenu>()[0].Find("button").Click();
        _popovers.WaitForAssertion(() => Assert.Contains("Ask before running scripts", _popovers.Markup, StringComparison.Ordinal));
        ClickText(_popovers, "Ask before running scripts");

        var key = PreviewConsent.StorageKey(User, PreviewConsentSwitch.RunScripts);
        Assert.Contains(JSInterop.Invocations, call =>
            call.Identifier == "quorumPreview.storageRemove" && Equals(call.Arguments[0], key));
        panel.WaitForAssertion(() => Assert.Empty(panel.FindComponents<MudMenu>()));
    }

    [Fact]
    public void ReplacingTheContentReplacesThePreviewAndThereIsStillOnePanel()
    {
        var panel = Open(Json());

        panel.Render(p => p.Add(x => x.Content, new PreviewContent(PreviewKind.Csv, "a;b\n1;2")));

        panel.WaitForAssertion(() =>
        {
            Assert.Contains("CSV · 7 B", panel.Markup, StringComparison.Ordinal);
            Assert.Single(panel.FindAll("iframe.preview-frame"));
            Assert.Contains("\"kind\":\"csv\"", LastRenderPayload(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void AViewerThatNeverAnswersFailsWithRetryAndRetryRestoresTheFrame()
    {
        var panel = Open(Json());

        panel.InvokeAsync(() => panel.Instance.OnViewerTimeout());

        panel.WaitForAssertion(() => Assert.Contains("The preview could not start.", panel.Markup, StringComparison.Ordinal));
        Assert.Contains("hidden", panel.Find("iframe.preview-frame").GetAttribute("class"), StringComparison.Ordinal);
        // AC-33: Copy and Download are still there.
        Assert.NotEmpty(panel.FindAll("button[aria-label='Copy the raw text']"));

        ClickButton(panel, "Retry");

        panel.WaitForAssertion(() => Assert.DoesNotContain("could not start", panel.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("hidden", panel.Find("iframe.preview-frame").GetAttribute("class"), StringComparison.Ordinal);
    }

    [Fact]
    public void ALibraryFailureFailsTheSameWayAndARendererErrorShowsItsMessage()
    {
        var panel = Open(Json("{bad"));

        panel.InvokeAsync(() => panel.Instance.OnPreviewError("library", "mermaid"));
        panel.WaitForAssertion(() => Assert.Contains("The preview could not start.", panel.Markup, StringComparison.Ordinal));

        panel.FindAll("button").First(b => b.TextContent.Contains("Retry", StringComparison.Ordinal)).Click();
        panel.InvokeAsync(() => panel.Instance.OnPreviewError("syntax", "Unexpected token at position 1"));

        // AC-15: the message is in the panel, Copy and Download still work.
        panel.WaitForAssertion(() => Assert.Contains("Unexpected token at position 1", panel.Markup, StringComparison.Ordinal));
        Assert.NotEmpty(panel.FindAll("button[aria-label='Copy the raw text']"));
        Assert.NotEmpty(panel.FindAll("button[aria-label='Download the raw text']"));
    }

    [Fact]
    public void AnEmptyErrorMessageFallsBackToAGenericOne()
    {
        var panel = Open(Json());

        panel.InvokeAsync(() => panel.Instance.OnPreviewError("syntax", " "));

        panel.WaitForAssertion(() => Assert.Contains("The preview could not render this content.", panel.Markup, StringComparison.Ordinal));
    }

    private IReadOnlyList<JSRuntimeInvocation> RenderCalls =>
        [.. JSInterop.Invocations.Where(call => call.Identifier == "quorumPreview.postToViewer")];

    private string LastRenderPayload() =>
        JsonSerializer.Serialize(RenderCalls[^1].Arguments[1]);

    private static void TurnOn(IRenderedComponent<PreviewPanel> panel, string label) =>
        panel.FindComponents<MudSwitch<bool>>()
            .Single(switchComponent => switchComponent.Instance.Label == label)
            .Find("input")
            .Change(true);

    /// <summary>
    /// Waits until the consent dialog is up. The title and the actions are rendered by the provider around the
    /// fragments the dialog hands over, so they are looked for in the provider's markup, not the component's own.
    /// </summary>
    private IRenderedComponent<PreviewConsentDialog> WaitConsentDialog(string title)
    {
        _dialogs.WaitForAssertion(() => Assert.Contains(title, _dialogs.Markup, StringComparison.Ordinal));
        return _dialogs.FindComponent<PreviewConsentDialog>();
    }

    private static void ClickButton<TComponent>(IRenderedComponent<TComponent> rendered, string text)
        where TComponent : IComponent =>
        rendered.FindAll("button").First(button => button.TextContent.Contains(text, StringComparison.Ordinal)).Click();

    /// <summary>Clicks the innermost element carrying the text: a menu item is not a button (bUnit bubbles the click).</summary>
    private static void ClickText<TComponent>(IRenderedComponent<TComponent> rendered, string text)
        where TComponent : IComponent
    {
        var matches = rendered.FindAll("*").Where(element => element.TextContent.Contains(text, StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(matches);
        matches[^1].Click();
    }
}
