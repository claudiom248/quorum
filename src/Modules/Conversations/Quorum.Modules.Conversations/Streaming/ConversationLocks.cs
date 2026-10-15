using System.Collections.Concurrent;

namespace Quorum.Modules.Conversations.Streaming;

/// <summary>
/// At most one answer is generated per conversation at a time. Kept in memory: Quorum runs as a single
/// instance (self-hosted); several replicas would need a lock in the database.
/// </summary>
internal sealed class ConversationLocks
{
    private readonly ConcurrentDictionary<Guid, Lease> _leases = new();

    /// <summary>Returns <see langword="null"/> when an answer is already being generated.</summary>
    public Lease? TryAcquire(Guid conversationId, CancellationToken requestAborted)
    {
        var lease = new Lease(this, conversationId, requestAborted);
        if (_leases.TryAdd(conversationId, lease))
        {
            return lease;
        }

        lease.Dispose();
        return null;
    }

    /// <summary>Stops the answer being generated, if any, and waits until it has been stored.</summary>
    public async Task StopAsync(Guid conversationId, TimeSpan timeout)
    {
        if (_leases.TryGetValue(conversationId, out var lease))
        {
            await lease.CancelAsync();
            try
            {
                await lease.Released.WaitAsync(timeout);
            }
            catch (TimeoutException)
            {
                // The answer is still being stored; deleting the conversation removes it anyway.
            }
        }
    }

    internal sealed class Lease : IDisposable
    {
        private readonly ConversationLocks _owner;
        private readonly Guid _conversationId;
        private readonly CancellationTokenSource _cancellation;
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        public Lease(ConversationLocks owner, Guid conversationId, CancellationToken requestAborted)
        {
            _owner = owner;
            _conversationId = conversationId;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        }

        /// <summary>Cancelled when the client goes away or someone stops the answer.</summary>
        public CancellationToken Token => _cancellation.Token;

        public Task Released => _released.Task;

        public Task CancelAsync() => _cancellation.CancelAsync();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _owner._leases.TryRemove(new KeyValuePair<Guid, Lease>(_conversationId, this));
            _cancellation.Dispose();
            _released.TrySetResult();
        }
    }
}
