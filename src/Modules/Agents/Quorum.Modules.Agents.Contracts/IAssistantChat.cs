using Quorum.Shared.Results;

namespace Quorum.Modules.Agents.Contracts;

/// <summary>Generates an assistant's answer to a conversation, streamed as text chunks.</summary>
public interface IAssistantChat
{
    /// <summary>
    /// Checks the request against the assistant's context budget and starts the answer.
    /// Fails with a validation error, before calling the model, when the last turn alone does not fit.
    /// The returned stream throws <see cref="ModelStreamException"/> when the model fails mid-answer.
    /// </summary>
    /// <param name="history">The conversation, oldest first; the last turn is the user's message to answer.</param>
    Task<Result<IAsyncEnumerable<string>>> StartAnswerAsync(Guid assistantId, IReadOnlyList<ChatTurn> history, CancellationToken cancellationToken);
}

/// <summary>Finds the assistants of the current tenant.</summary>
public interface IAssistantCatalog
{
    Task<Guid?> GetDefaultAssistantIdAsync(CancellationToken cancellationToken);
}

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
