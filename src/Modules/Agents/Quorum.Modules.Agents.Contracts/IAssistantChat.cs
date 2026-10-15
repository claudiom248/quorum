using Quorum.Shared.Results;

namespace Quorum.Modules.Agents.Contracts;

/// <summary>Generates an assistant's answer to a conversation, streamed as text and reasoning chunks.</summary>
public interface IAssistantChat
{
    /// <summary>
    /// Checks the request against the assistant's context budget and starts the answer.
    /// Fails with a validation error, before calling the model, when the last turn alone does not fit.
    /// The returned stream throws <see cref="ModelStreamException"/> when the model fails mid-answer.
    /// </summary>
    /// <param name="history">The conversation, oldest first; the last turn is the user's message to answer.</param>
    /// <param name="attachments">
    /// The documents attached to the conversation, oldest first. They take priority over the history in the budget:
    /// when they do not fit, the call fails instead of dropping any.
    /// </param>
    Task<Result<IAsyncEnumerable<AnswerChunk>>> StartAnswerAsync(
        Guid assistantId,
        IReadOnlyList<ChatTurn> history,
        IReadOnlyList<ChatAttachment> attachments,
        CancellationToken cancellationToken);

    /// <summary>How much of the assistant's context the conversation uses, without calling the model.</summary>
    Task<Result<ContextUsage>> MeasureContextAsync(
        Guid assistantId,
        IReadOnlyList<ChatTurn> history,
        IReadOnlyList<ChatAttachment> attachments,
        CancellationToken cancellationToken);
}

/// <summary>A document attached to a conversation: its display name and the text extracted from it.</summary>
public sealed record ChatAttachment(string FileName, string Text);

/// <summary>
/// The context budget of a conversation, in characters (what the budget rules work with) and estimated tokens.
/// <see cref="RemainingCharacters"/> is negative when the system prompt and the attachments alone do not fit.
/// </summary>
public sealed record ContextUsage(
    int ContextTokenBudget,
    int UsableCharacters,
    int SystemPromptCharacters,
    int AttachmentsCharacters,
    int MessagesCharacters)
{
    public const int CharactersPerToken = 4;

    public int UsableTokens => UsableCharacters / CharactersPerToken;

    public int SystemPromptTokens => SystemPromptCharacters / CharactersPerToken;

    public int AttachmentsTokens => AttachmentsCharacters / CharactersPerToken;

    public int MessagesTokens => MessagesCharacters / CharactersPerToken;

    public int RemainingCharacters => UsableCharacters - SystemPromptCharacters - AttachmentsCharacters - MessagesCharacters;

    // Rounded down, so a few characters over the budget are a negative margin rather than "0 tokens left".
    public int RemainingTokens => (int)Math.Floor(RemainingCharacters / (double)CharactersPerToken);
}

/// <summary>Finds the assistants of the current tenant.</summary>
public interface IAssistantCatalog
{
    Task<Guid?> GetDefaultAssistantIdAsync(CancellationToken cancellationToken);
}

public enum AnswerChunkKind
{
    Text,
    Reasoning,
}

/// <summary>A piece of the answer: its text, or the reasoning the model shows while it thinks (never part of the answer).</summary>
public sealed record AnswerChunk(AnswerChunkKind Kind, string Text);

public enum ChatTurnRole
{
    User,
    Assistant,
}

public sealed record ChatTurn(ChatTurnRole Role, string Content);

/// <summary>The model endpoint failed while answering (unreachable, connection reset, error response).</summary>
public sealed class ModelStreamException : Exception
{
    public ModelStreamException()
    {
    }

    public ModelStreamException(string message)
        : base(message)
    {
    }

    public ModelStreamException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
