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
    /// <param name="conversationId">The conversation being answered; tool calls are recorded against it in the audit log.</param>
    /// <param name="history">The conversation, oldest first; the last turn is the user's message to answer.</param>
    /// <param name="attachments">
    /// The documents attached to the conversation, oldest first. They take priority over the history in the budget:
    /// when they do not fit, the call fails instead of dropping any.
    /// </param>
    Task<Result<IAsyncEnumerable<AnswerChunk>>> StartAnswerAsync(
        Guid assistantId,
        Guid conversationId,
        IReadOnlyList<ChatTurn> history,
        IReadOnlyList<ChatAttachment> attachments,
        CancellationToken cancellationToken);

    /// <summary>How much of the assistant's context the conversation uses, without calling the model.</summary>
    Task<Result<ContextUsage>> MeasureContextAsync(
        Guid assistantId,
        IReadOnlyList<ChatTurn> history,
        IReadOnlyList<ChatAttachment> attachments,
        CancellationToken cancellationToken);

    /// <summary>
    /// The same measure from the sizes of the attachments, for callers that must not load their text only to count it.
    /// </summary>
    Task<Result<ContextUsage>> MeasureContextBySizesAsync(
        Guid assistantId,
        IReadOnlyList<ChatTurn> history,
        IReadOnlyList<ChatAttachmentSize> attachments,
        CancellationToken cancellationToken);
}

/// <summary>A document attached to a conversation: its display name and the text extracted from it.</summary>
public sealed record ChatAttachment(string FileName, string Text);

/// <summary>The size of an attachment: the length of its file name and of its extracted text, in characters.</summary>
public sealed record ChatAttachmentSize(int FileNameLength, int TextLength);

/// <summary>
/// The context budget of a conversation, in characters (what the budget rules work with) and estimated tokens.
/// <see cref="RemainingCharacters"/> is negative when the system prompt, the attachments and the tool definitions alone do not fit.
/// </summary>
public sealed record ContextUsage(
    int ContextTokenBudget,
    int UsableCharacters,
    int SystemPromptCharacters,
    int AttachmentsCharacters,
    int MessagesCharacters,
    int ToolsCharacters = 0)
{
    public const int CharactersPerToken = 4;

    public int UsableTokens => UsableCharacters / CharactersPerToken;

    public int SystemPromptTokens => SystemPromptCharacters / CharactersPerToken;

    public int AttachmentsTokens => AttachmentsCharacters / CharactersPerToken;

    public int MessagesTokens => MessagesCharacters / CharactersPerToken;

    /// <summary>What the definitions of the assistant's active tools take in the request.</summary>
    public int ToolsTokens => ToolsCharacters / CharactersPerToken;

    public int RemainingCharacters =>
        UsableCharacters - SystemPromptCharacters - AttachmentsCharacters - ToolsCharacters - MessagesCharacters;

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

    /// <summary>The model asked for a tool: <see cref="AnswerChunk.Tool"/> is a <see cref="ToolCallEvent"/>.</summary>
    ToolCall,

    /// <summary>A tool call ended: <see cref="AnswerChunk.Tool"/> is a <see cref="ToolResultEvent"/>.</summary>
    ToolResult,

    /// <summary>The model asks the user a question and the answer waits for it: <see cref="AnswerChunk.Tool"/> is a <see cref="UserQuestionEvent"/>.</summary>
    UserQuestion,
}

/// <summary>
/// A piece of the answer: its text, or the reasoning the model shows while it thinks (never part of the answer), or an event of
/// a tool call (shown live, never part of the answer either).
/// </summary>
public sealed record AnswerChunk(AnswerChunkKind Kind, string Text, ToolEvent? Tool = null);

/// <summary>What happened to a tool call, for the user to watch.</summary>
public abstract record ToolEvent(Guid CallId);

/// <param name="Tool">The name the model called the tool by, e.g. <c>native__calculator</c>.</param>
/// <param name="Arguments">The arguments as the model sent them: JSON text when they are valid JSON.</param>
public sealed record ToolCallEvent(Guid CallId, string Tool, string Arguments, bool RequiresConfirmation, DateTimeOffset? ExpiresAt)
    : ToolEvent(CallId);

/// <param name="Question">What the model wants to know, as it wrote it.</param>
/// <param name="ExpiresAt">When the question counts as unanswered.</param>
public sealed record UserQuestionEvent(Guid CallId, string Question, DateTimeOffset ExpiresAt) : ToolEvent(CallId);

/// <param name="Content">The result, or the error that the model received in its place.</param>
public sealed record ToolResultEvent(Guid CallId, ToolResultStatus Status, string Content, int SizeCharacters)
    : ToolEvent(CallId);

public enum ToolResultStatus
{
    Succeeded,
    Failed,
    Denied,
    Cancelled,
    TimedOut,
}

public enum ChatTurnRole
{
    User,
    Assistant,
}

public sealed record ChatTurn(ChatTurnRole Role, string Content);

/// <summary>
/// The answer failed while streaming: the model endpoint is unreachable, the connection reset, it answered with an error,
/// or a limit of the tool-calling loop was reached. The message is meant for the user.
/// </summary>
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
