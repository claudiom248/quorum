using System.Threading.Channels;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Agents.Tools;

namespace Quorum.Modules.Agents.Chat;

/// <summary>Why an answer was stopped by one of its own limits, as far as the code that enforces them can tell.</summary>
internal enum AnswerStop
{
    None,
    TotalTime,
    Rounds,
}

/// <summary>
/// What the parts of one answer share: who is answering, the time left, where tool events go, and the limit that stopped it.
/// The answer runs on a background task, so the invoker can publish a tool call to the client while it waits.
/// </summary>
internal sealed class AnswerRun(ToolRunContext context, AnswerDeadline deadline, ChannelWriter<AnswerChunk> events, int usableCharacters)
{
    private readonly HashSet<string> _handledCalls = new(StringComparer.Ordinal);

    public ToolRunContext Context { get; } = context;

    public AnswerDeadline Deadline { get; } = deadline;

    /// <summary>The model's whole context budget, in characters.</summary>
    public int UsableCharacters { get; } = usableCharacters;

    public AnswerStop Stop { get; private set; }

    /// <summary>An unexpected failure of the invoker. It ends the answer: MEAI would only turn the exception into an error for the model.</summary>
    public Exception? Failure { get; private set; }

    /// <summary>Waits when the reader is behind, so a slow client slows the model stream instead of piling the answer up in memory.</summary>
    public ValueTask PublishAsync(AnswerChunk chunk, CancellationToken cancellationToken) => events.WriteAsync(chunk, cancellationToken);

    public void Fail(Exception exception) => Failure ??= exception;

    /// <summary>Remembers the first limit reached; the answer ends with its message.</summary>
    public void StopFor(AnswerStop reason)
    {
        if (Stop == AnswerStop.None)
        {
            Stop = reason;
        }
    }

    /// <summary>The invoker took this call of the model, so it publishes the call's events itself.</summary>
    public void MarkHandled(string modelCallId) => _handledCalls.Add(modelCallId);

    /// <summary>Whether the invoker took this call, forgetting it: servers may reuse an id in a later round.</summary>
    public bool ConsumeHandled(string modelCallId) => _handledCalls.Remove(modelCallId);
}
