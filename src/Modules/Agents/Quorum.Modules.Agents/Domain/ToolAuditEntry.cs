namespace Quorum.Modules.Agents.Domain;

/// <summary>How a tool call came to run (or not): the policy let it through, or the user decided.</summary>
internal enum ToolDecision
{
    Auto,
    Approved,
    Denied,
    TimedOut,
}

internal enum ToolOutcome
{
    /// <summary>Written before the call runs; a call that never completes stays in this state.</summary>
    Started,
    Succeeded,
    Failed,
    Denied,
    Cancelled,
    TimedOut,
}

/// <summary>
/// One tool call, recorded before it runs and completed afterwards. Names are snapshots, so the entry stays readable
/// after a server is renamed or deleted. The content of the result is never stored, only its size.
/// </summary>
internal sealed class ToolAuditEntry
{
    private ToolAuditEntry()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    public Guid AssistantId { get; private set; }

    public Guid ConversationId { get; private set; }

    /// <summary>The model's id for the call, also used by the confirmation endpoints.</summary>
    public Guid CallId { get; private set; }

    /// <summary>Null for built-in tools.</summary>
    public string? ServerName { get; private set; }

    public string ToolName { get; private set; } = null!;

    /// <summary>Null until the call is decided (a confirmation may still be pending).</summary>
    public ToolDecision? Decision { get; private set; }

    public ToolOutcome Outcome { get; private set; }

    /// <summary>The arguments as JSON, with the values of sensitive keys redacted.</summary>
    public string Arguments { get; private set; } = null!;

    public bool ArgumentsTruncated { get; private set; }

    public int? ResultCharacters { get; private set; }

    public int? DurationMs { get; private set; }

    public string? ErrorCode { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>The entry of a call that is about to run (or to be refused): <see cref="ToolOutcome.Started"/> until it is completed.</summary>
    public static ToolAuditEntry Start(
        Guid tenantId,
        Guid userId,
        Guid assistantId,
        Guid conversationId,
        Guid callId,
        string? serverName,
        string toolName,
        ToolDecision? decision,
        string arguments,
        bool argumentsTruncated,
        DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            UserId = userId,
            AssistantId = assistantId,
            ConversationId = conversationId,
            CallId = callId,
            ServerName = serverName,
            ToolName = toolName,
            Decision = decision,
            Outcome = ToolOutcome.Started,
            Arguments = arguments,
            ArgumentsTruncated = argumentsTruncated,
            StartedAt = now,
        };

    /// <summary>Records how the call ended. Only the size of the result is kept, never its content.</summary>
    public void Complete(ToolOutcome outcome, int? resultCharacters, string? errorCode, DateTimeOffset now, TimeSpan duration, ToolDecision? decision = null)
    {
        Outcome = outcome;
        ResultCharacters = resultCharacters;
        ErrorCode = errorCode;
        Decision = decision ?? Decision;
        CompletedAt = now;
        DurationMs = (int)Math.Min(int.MaxValue, duration.TotalMilliseconds);
    }
}
