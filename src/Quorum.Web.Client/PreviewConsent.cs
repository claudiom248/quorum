using Microsoft.JSInterop;

namespace Quorum.Web.Client;

/// <summary>The two consent switches of an HTML preview (spec 014, D-17 and D-18).</summary>
public enum PreviewConsentSwitch
{
    RunScripts,
    RemoteImages,
}

/// <summary>
/// The "don't ask me again" choices, kept in <c>localStorage</c> per signed-in user and per browser (D-17). When
/// <c>localStorage</c> is unavailable the stored value reads as absent and the dialog is shown every time (AC-31).
/// </summary>
public sealed class PreviewConsent(IJSRuntime js)
{
    public static string StorageKey(string email, PreviewConsentSwitch which) =>
        $"quorum.preview.{email}.{Segment(which)}.dontAsk";

    /// <summary>The key the panel width is kept under (D-20).</summary>
    public static string WidthKey(string email) => $"quorum.preview.{email}.width";

    public async Task<bool> DontAskAsync(string email, PreviewConsentSwitch which) =>
        await js.InvokeAsync<string?>("quorumPreview.storageGet", StorageKey(email, which)) == "1";

    public Task SaveDontAskAsync(string email, PreviewConsentSwitch which) =>
        js.InvokeVoidAsync("quorumPreview.storageSet", StorageKey(email, which), "1").AsTask();

    /// <summary>The panel menu's "Ask before running scripts" / "…loading remote images" takes the choice back.</summary>
    public Task AskAgainAsync(string email, PreviewConsentSwitch which) =>
        js.InvokeVoidAsync("quorumPreview.storageRemove", StorageKey(email, which)).AsTask();

    private static string Segment(PreviewConsentSwitch which) => which switch
    {
        PreviewConsentSwitch.RunScripts => "runScripts",
        _ => "remoteImages",
    };
}
