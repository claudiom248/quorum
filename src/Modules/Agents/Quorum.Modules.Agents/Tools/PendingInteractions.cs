using System.Collections.Concurrent;
using Quorum.Modules.Agents.Contracts;
using Quorum.Shared.Results;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.Agents.Tools;

internal enum InteractionKind
{
    /// <summary>A tool call that waits for a yes or a no.</summary>
    Confirmation,

    /// <summary>A question of the model that waits for an answer.</summary>
    Question,
}

/// <summary>What the user replied: a decision for a confirmation, or the text of an answer for a question.</summary>
internal readonly record struct InteractionReply(bool Approved, string? Answer);

/// <summary>One call of an answer that is waiting for the user.</summary>
internal sealed class PendingInteraction(Guid callId, Guid tenantId, Guid userId, Guid conversationId, InteractionKind kind)
{
    private readonly TaskCompletionSource<InteractionReply> _reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _settledAt;

    public Guid CallId { get; } = callId;

    public Guid TenantId { get; } = tenantId;

    public Guid UserId { get; } = userId;

    public Guid ConversationId { get; } = conversationId;

    public InteractionKind Kind { get; } = kind;

    public Task<InteractionReply> Reply => _reply.Task;

    /// <summary>When the wait ended, as a <see cref="TimeProvider"/> timestamp; zero while it is still going.</summary>
    internal long SettledAt => Interlocked.Read(ref _settledAt);

    /// <returns><see langword="false"/> when it was replied to, or given up on, already.</returns>
    internal bool TryReply(InteractionReply reply) => _reply.TrySetResult(reply);

    /// <summary>The wait is over without a reply (timeout, the answer was stopped): a reply that comes later is too late.</summary>
    /// <returns><see langword="false"/> when a reply came first: the wait is then over with that reply, not without one.</returns>
    internal bool GiveUp(long timestamp)
    {
        var cancelled = _reply.TrySetCanceled();
        Settle(timestamp);
        return cancelled;
    }

    internal void Settle(long timestamp) => Interlocked.CompareExchange(ref _settledAt, timestamp, 0);
}

/// <summary>
/// The confirmations and questions that answers are waiting for. In memory, like the conversation lock: a restart loses them all,
/// which is a denial (D-9). An entry stays for a while after the wait ends, so that a second reply, or one that comes after the
/// timeout, is told it came too late (409) instead of being taken for an unknown call (404).
/// </summary>
internal sealed class PendingInteractions(TimeProvider clock)
{
    private static readonly TimeSpan SettledRetention = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<Guid, PendingInteraction> _pending = new();

    public PendingInteraction Register(Guid callId, Guid tenantId, Guid userId, Guid conversationId, InteractionKind kind)
    {
        Purge();
        var interaction = new PendingInteraction(callId, tenantId, userId, conversationId, kind);
        if (!_pending.TryAdd(callId, interaction))
        {
            throw new InvalidOperationException("A call id is registered once.");
        }

        return interaction;
    }

    /// <summary>The wait for this reply ended with no reply (the confirmation timeout, or the answer was stopped).</summary>
    /// <returns><see langword="false"/> when the user's reply arrived first and is the one to use.</returns>
    public bool GiveUp(PendingInteraction interaction)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        return interaction.GiveUp(clock.GetTimestamp());
    }

    /// <summary>The answer of this conversation has ended, so nothing it was waiting for can be replied to any more.</summary>
    public void CloseConversation(Guid conversationId)
    {
        foreach (var interaction in _pending.Values)
        {
            if (interaction.ConversationId == conversationId && interaction.SettledAt == 0)
            {
                interaction.GiveUp(clock.GetTimestamp());
            }
        }

        Purge();
    }

    /// <summary>Replies on behalf of a user. Another user's, another conversation's or an unknown call looks the same: not found.</summary>
    public Result Reply(Guid tenantId, Guid userId, Guid conversationId, Guid callId, InteractionKind kind, InteractionReply reply)
    {
        if (!_pending.TryGetValue(callId, out var interaction)
            || interaction.TenantId != tenantId
            || interaction.UserId != userId
            || interaction.ConversationId != conversationId
            || interaction.Kind != kind)
        {
            return InteractionErrors.NotFound;
        }

        if (!interaction.TryReply(reply))
        {
            return InteractionErrors.AlreadyReplied;
        }

        interaction.Settle(clock.GetTimestamp());
        return Result.Success();
    }

    private void Purge()
    {
        foreach (var (callId, interaction) in _pending)
        {
            if (interaction.SettledAt != 0 && clock.GetElapsedTime(interaction.SettledAt) > SettledRetention)
            {
                _pending.TryRemove(callId, out _);
            }
        }
    }
}

/// <summary>The caller of <see cref="IToolInteractions"/>: the signed-in user, for whom the reply is checked.</summary>
internal sealed class ToolInteractions(PendingInteractions pending, ITenantContext tenant, ICurrentUser user) : IToolInteractions
{
    public Result Decide(Guid conversationId, Guid callId, bool approved) =>
        Reply(conversationId, callId, InteractionKind.Confirmation, new InteractionReply(approved, null));

    public Result Answer(Guid conversationId, Guid callId, string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return Reply(conversationId, callId, InteractionKind.Question, new InteractionReply(true, answer));
    }

    private Result Reply(Guid conversationId, Guid callId, InteractionKind kind, InteractionReply reply) =>
        tenant.TenantId is { } tenantId && user.UserId is { } userId
            ? pending.Reply(tenantId, userId, conversationId, callId, kind, reply)
            : InteractionErrors.NotFound;
}

internal static class InteractionErrors
{
    public static Error NotFound { get; } = Error.NotFound("tool.interaction_not_found", "No tool call is waiting for a reply from you.");

    public static Error AlreadyReplied { get; } =
        Error.Conflict("tool.already_decided", "This tool call was already decided, or the time to decide has passed.");
}
