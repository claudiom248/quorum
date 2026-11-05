using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Quorum.Modules.KnowledgeBases.Contracts;

namespace Quorum.Modules.KnowledgeBases.Persistence;

/// <summary>
/// Tells when the data of the module changes, and keeps what was read from it until then. It looks at the statements the module's
/// DbContext sends, not at what <c>SaveChanges</c> saw, because the module also writes with <c>ExecuteUpdate</c>, <c>ExecuteDelete</c>
/// and raw SQL, which a save interceptor never sees. A write counts when it is sent, and again when its transaction commits, since a
/// reader in between could read the old rows and keep them; one that rolls back only costs a read. A time limit stays as a safety net.
/// </summary>
internal sealed class KnowledgeBaseChanges : IKnowledgeBaseChangeStamp, IDisposable
{
    private static readonly TimeSpan SafetyNet = TimeSpan.FromMinutes(1);

    private readonly MemoryCache _entries = new(new MemoryCacheOptions());
    private readonly ConditionalWeakTable<DbContext, object> _wrote = [];
    private long _version;

    public long Current => Interlocked.Read(ref _version);

    public DbCommandInterceptor CommandInterceptor() => new CommandWatcher(this);

    public DbTransactionInterceptor TransactionInterceptor() => new TransactionWatcher(this);

    /// <summary>Makes everything read before this moment stale.</summary>
    public void Invalidate() => Interlocked.Increment(ref _version);

    /// <param name="load">Reads the value; a <see langword="null"/> result is not kept.</param>
    public async Task<T?> GetOrLoadAsync<T>(string key, Func<Task<T?>> load)
        where T : class
    {
        // The version is read before the data is: a change that lands while it is read leaves the entry stale, not fresh.
        var version = Current;
        if (_entries.TryGetValue(key, out Entry<T>? kept) && kept is not null && kept.Version == version)
        {
            return kept.Value;
        }

        var value = await load();
        if (value is not null)
        {
            _entries.Set(key, new Entry<T>(version, value), SafetyNet);
        }

        return value;
    }

    public void Dispose() => _entries.Dispose();

    // What reads (a SELECT, which includes the advisory locks) leaves the data as it was; anything else may not.
    private static bool Writes(DbCommand command) =>
        !command.CommandText.AsSpan().TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase);

    private void Wrote(DbCommand command, DbContext? context)
    {
        if (!Writes(command))
        {
            return;
        }

        Invalidate();
        if (context is not null && command.Transaction is not null)
        {
            _wrote.AddOrUpdate(context, this);
        }
    }

    private sealed record Entry<T>(long Version, T Value);

    private sealed class CommandWatcher(KnowledgeBaseChanges owner) : DbCommandInterceptor
    {
        public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        {
            owner.Wrote(command, eventData.Context);
            return result;
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            owner.Wrote(command, eventData.Context);
            return ValueTask.FromResult(result);
        }

        public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
        {
            owner.Wrote(command, eventData.Context);
            return result;
        }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            owner.Wrote(command, eventData.Context);
            return ValueTask.FromResult(result);
        }

        public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
        {
            owner.Wrote(command, eventData.Context);
            return result;
        }

        public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
        {
            owner.Wrote(command, eventData.Context);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class TransactionWatcher(KnowledgeBaseChanges owner) : DbTransactionInterceptor
    {
        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => Committed(eventData.Context);

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Committed(eventData.Context);
            return Task.CompletedTask;
        }

        public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => Forget(eventData.Context);

        public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Forget(eventData.Context);
            return Task.CompletedTask;
        }

        private void Committed(DbContext? context)
        {
            if (context is not null && owner._wrote.Remove(context))
            {
                owner.Invalidate();
            }
        }

        private void Forget(DbContext? context)
        {
            if (context is not null)
            {
                owner._wrote.Remove(context);
            }
        }
    }
}
