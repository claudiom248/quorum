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
/// A sub-agent runs on a run of its own (<see cref="ForSubAgent"/>) that shares the events, the deadline and the gates of the answer
/// but has its own agent, rounds and stop reason (spec 013).
/// </summary>
internal sealed class AnswerRun
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _handledCalls = new(StringComparer.Ordinal);
    private readonly ChannelWriter<AnswerChunk> _events;
    private readonly RunGates _gates;
    private int _rounds;
    private int _toolCalls;

    /// <param name="maxParallelSubAgents">How many delegations of the answer may run at once (spec 013 D-14).</param>
    public AnswerRun(
        ToolRunContext context,
        AnswerDeadline deadline,
        ChannelWriter<AnswerChunk> events,
        int usableCharacters,
        int maxParallelSubAgents = 1,
        SemaphoreSlim? databaseGate = null)
        : this(context, deadline, events, usableCharacters, new RunGates(maxParallelSubAgents, databaseGate ?? new SemaphoreSlim(1, 1)), null, null)
    {
    }

    private AnswerRun(
        ToolRunContext context,
        AnswerDeadline deadline,
        ChannelWriter<AnswerChunk> events,
        int usableCharacters,
        RunGates gates,
        Guid? parentCallId,
        string? agentSlug)
    {
        Context = context;
        Deadline = deadline;
        UsableCharacters = usableCharacters;
        _events = events;
        _gates = gates;
        ParentCallId = parentCallId;
        AgentSlug = agentSlug;
    }

    public ToolRunContext Context { get; }

    public AnswerDeadline Deadline { get; }

    /// <summary>The model's whole context budget, in characters.</summary>
    public int UsableCharacters { get; }

    /// <summary>The delegation call this run is the sub-agent of; <see langword="null"/> for the root agent's run.</summary>
    public Guid? ParentCallId { get; }

    /// <summary>The slug of the sub-agent this run belongs to; <see langword="null"/> for the root agent's run.</summary>
    public string? AgentSlug { get; }

    public AnswerStop Stop { get; private set; }

    /// <summary>The requests this run made to its model, one per round (spec 016 D-13).</summary>
    public int Rounds => Volatile.Read(ref _rounds);

    /// <summary>The tool calls this run took, its own and not those of its sub-agents (spec 016 D-13).</summary>
    public int ToolCalls => Volatile.Read(ref _toolCalls);

    /// <summary>An unexpected failure of the invoker. It ends the answer: MEAI would only turn the exception into an error for the model.</summary>
    public Exception? Failure { get; private set; }

    /// <summary>The one writer to the database context of the answer: it is not thread safe and delegations run in parallel.</summary>
    public SemaphoreSlim DatabaseGate => _gates.Database;

    /// <summary>Held while a confirmation or a question is open, so the user sees one dialog at a time (spec 013 D-37).</summary>
    public SemaphoreSlim ConfirmationGate => _gates.Confirmation;

    /// <summary>Admits the delegations of the answer up to the assistant's limit (spec 013 D-14, D-38).</summary>
    public SemaphoreSlim DelegationGate => _gates.Delegation;

    /// <summary>
    /// Ordinary tool calls run one at a time (spec 004 D-22, spec 013 D-38), also across sub-agents: the tools share the scoped services
    /// of the request (knowledge base searches, the audit context), which are not safe to use from two calls at once.
    /// </summary>
    public SemaphoreSlim OrdinaryToolGate => _gates.OrdinaryTool;

    /// <summary>The run of a sub-agent called by <paramref name="parentCallId"/>, on the budget of the model it uses.</summary>
    public AnswerRun ForSubAgent(Guid agentId, string slug, Guid parentCallId, int usableCharacters) =>
        new(Context with { AgentId = agentId }, Deadline, _events, usableCharacters, _gates, parentCallId, slug);

    /// <summary>Waits when the reader is behind, so a slow client slows the model stream instead of piling the answer up in memory.</summary>
    public ValueTask PublishAsync(AnswerChunk chunk, CancellationToken cancellationToken) => _events.WriteAsync(chunk, cancellationToken);

    public void Fail(Exception exception)
    {
        lock (_gate)
        {
            Failure ??= exception;
        }
    }

    public void CountRound() => Interlocked.Increment(ref _rounds);

    public void CountToolCall() => Interlocked.Increment(ref _toolCalls);

    /// <summary>Remembers the first limit reached; the answer ends with its message.</summary>
    public void StopFor(AnswerStop reason)
    {
        lock (_gate)
        {
            if (Stop == AnswerStop.None)
            {
                Stop = reason;
            }
        }
    }

    /// <summary>The invoker took this call of the model, so it publishes the call's events itself.</summary>
    public void MarkHandled(string modelCallId)
    {
        lock (_gate)
        {
            _handledCalls.Add(modelCallId);
        }
    }

    /// <summary>Whether the invoker took this call, forgetting it: servers may reuse an id in a later round.</summary>
    public bool ConsumeHandled(string modelCallId)
    {
        lock (_gate)
        {
            return _handledCalls.Remove(modelCallId);
        }
    }

    private sealed class RunGates(int maxParallelSubAgents, SemaphoreSlim database)
    {
        public SemaphoreSlim Database { get; } = database;

        public SemaphoreSlim OrdinaryTool { get; } = new(1, 1);

        public SemaphoreSlim Confirmation { get; } = new(1, 1);

        public SemaphoreSlim Delegation { get; } = new(Math.Max(1, maxParallelSubAgents));
    }
}
