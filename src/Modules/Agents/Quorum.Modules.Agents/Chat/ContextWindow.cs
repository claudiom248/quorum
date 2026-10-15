using Quorum.Modules.Agents.Contracts;
using Quorum.Shared.Results;

namespace Quorum.Modules.Agents.Chat;

/// <summary>
/// Chooses what fits in a model's context. Tokens are estimated as characters / 4 (no model-specific tokenizer),
/// with a 20% safety margin on the budget.
/// </summary>
internal static class ContextWindow
{
    private const int CharactersPerToken = 4;
    private const double SafetyMargin = 0.8;

    public static int CharacterBudget(int contextTokenBudget) => (int)(contextTokenBudget * SafetyMargin * CharactersPerToken);

    /// <summary>The most recent turns that fit after the system prompt; the last turn must fit on its own.</summary>
    public static Result<IReadOnlyList<ChatTurn>> Select(string systemPrompt, IReadOnlyList<ChatTurn> history, int contextTokenBudget)
    {
        var available = CharacterBudget(contextTokenBudget) - systemPrompt.Length;
        var last = history[^1];

        if (last.Content.Length > available)
        {
            return Error.Validation(
                "chat.message_too_long",
                $"The message is too long for this model's context: keep it under {Math.Max(0, available)} characters.");
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
}
