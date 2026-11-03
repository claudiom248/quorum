namespace Quorum.Modules.Agents.Persistence;

/// <summary>
/// Lets one <see cref="AgentsDbContext"/> be used by the parts of an answer that run at the same time (sub-agents in parallel, spec 013
/// D-14): a context is not thread safe, so every use during an answer takes this gate for the duration of one operation.
/// </summary>
internal sealed class AgentsDatabaseGate
{
    public SemaphoreSlim Semaphore { get; } = new(1, 1);

    public async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            return await operation();
        }
        finally
        {
            Semaphore.Release();
        }
    }
}
