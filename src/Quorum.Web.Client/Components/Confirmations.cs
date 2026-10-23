using MudBlazor;

namespace Quorum.Web.Client.Components;

/// <summary>The two questions every drawer and list asks before it throws something away (spec 010, D-8 and D-9).</summary>
public static class Confirmations
{
    private static readonly DialogOptions Options = new() { MaxWidth = MaxWidth.Small, FullWidth = true, CloseOnEscapeKey = true };

    /// <summary><see langword="true"/> when the admin chose to discard the changes.</summary>
    public static async Task<bool> ConfirmDiscardAsync(IDialogService dialogs)
    {
        ArgumentNullException.ThrowIfNull(dialogs);

        var answer = await dialogs.ShowMessageBoxAsync(
            "Discard changes?",
            "What you typed has not been saved.",
            yesText: "Discard",
            cancelText: "Keep editing",
            options: Options);
        return answer == true;
    }

    /// <summary>Names what goes away and what that means; <see langword="true"/> when the admin confirmed.</summary>
    public static async Task<bool> ConfirmDeleteAsync(IDialogService dialogs, string title, string consequence, string confirmLabel = "Delete")
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(consequence);

        var answer = await dialogs.ShowMessageBoxAsync(
            title,
            consequence,
            yesText: confirmLabel,
            cancelText: "Cancel",
            options: Options);
        return answer == true;
    }
}
