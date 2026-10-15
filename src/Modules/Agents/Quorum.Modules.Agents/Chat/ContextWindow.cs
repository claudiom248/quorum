using Quorum.Modules.Agents.Contracts;
using Quorum.Shared.Results;

namespace Quorum.Modules.Agents.Chat;

/// <summary>
/// Chooses what fits in a model's context. Tokens are estimated as characters / 4 (no model-specific tokenizer),
/// with a 20% safety margin on the budget. The system prompt comes first, then all the attachments, then the most
/// recent messages that fit.
/// </summary>
internal static class ContextWindow
{
    private const double SafetyMargin = 0.8;

    public static int CharacterBudget(int contextTokenBudget) =>
        (int)(contextTokenBudget * SafetyMargin * ContextUsage.CharactersPerToken);

    /// <summary>
    /// The most recent turns that fit after the system prompt and the attachments; the last turn must fit on its own.
    /// Fails, instead of dropping anything, when the attachments do not fit or the last message does not fit after them.
    /// </summary>
    public static Result<IReadOnlyList<ChatTurn>> Select(
        string systemPrompt,
        IReadOnlyList<ChatTurn> history,
        IReadOnlyList<ChatAttachment> attachments,
        int contextTokenBudget)
    {
        if (history.Count == 0)
        {
            return Error.Validation("chat.no_message", "There is no message to answer.");
        }

        var afterSystemPrompt = CharacterBudget(contextTokenBudget) - systemPrompt.Length;
        var attachmentCharacters = AttachmentBlock.Length(attachments);

        // Removing attachments cannot help when the system prompt alone leaves no room for them.
        if (attachments.Count > 0 && afterSystemPrompt < AttachmentBlock.PreambleLength)
        {
            return Error.Validation(
                "chat.system_prompt_too_long",
                "The system prompt leaves no room for attachments in this model's context: shorten it or raise the context budget in the settings.");
        }

        if (attachments.Count > 0 && attachmentCharacters > afterSystemPrompt)
        {
            return Error.Validation(
                "chat.attachments_exceed_budget",
                $"The attachments take {attachmentCharacters} characters but only {Math.Max(0, afterSystemPrompt)} fit this model's context. " +
                $"Remove {string.Join(", ", AttachmentsToRemove(attachments, afterSystemPrompt))} or raise the model's context budget in the settings.");
        }

        var available = afterSystemPrompt - attachmentCharacters;
        var last = history[^1];

        if (last.Content.Length > available)
        {
            var hint = attachments.Count > 0
                ? $" The {attachments.Count} attachment(s) take {attachmentCharacters} characters: remove some to make room."
                : string.Empty;
            return Error.Validation(
                "chat.message_too_long",
                $"The message is too long for this model's context: keep it under {Math.Max(0, available)} characters.{hint}");
        }

        var selected = new List<ChatTurn>();
        for (var i = history.Count - 1; i >= 0 && history[i].Content.Length <= available; i--)
        {
            available -= history[i].Content.Length;
            selected.Add(history[i]);
        }

        selected.Reverse();
        return selected;
    }

    /// <summary>The usage of the context: the messages counted are the ones that <see cref="Select"/> would keep.</summary>
    public static ContextUsage Measure(
        string systemPrompt,
        IReadOnlyList<ChatTurn> history,
        IReadOnlyList<ChatAttachment> attachments,
        int contextTokenBudget)
    {
        var attachmentCharacters = AttachmentBlock.Length(attachments);
        var available = CharacterBudget(contextTokenBudget) - systemPrompt.Length - attachmentCharacters;

        var messageCharacters = 0;
        for (var i = history.Count - 1; i >= 0 && history[i].Content.Length <= available - messageCharacters; i--)
        {
            messageCharacters += history[i].Content.Length;
        }

        return new ContextUsage(
            contextTokenBudget,
            CharacterBudget(contextTokenBudget),
            systemPrompt.Length,
            attachmentCharacters,
            messageCharacters);
    }

    /// <summary>The newest attachments, enough of them that what is left fits.</summary>
    private static List<string> AttachmentsToRemove(IReadOnlyList<ChatAttachment> attachments, int available)
    {
        var total = AttachmentBlock.Length(attachments);
        var names = new List<string>();

        for (var i = attachments.Count - 1; i >= 0 && total > available; i--)
        {
            names.Add(attachments[i].FileName);
            total = i == 0 ? 0 : total - AttachmentBlock.Length(attachments[i]);
        }

        return names;
    }
}
