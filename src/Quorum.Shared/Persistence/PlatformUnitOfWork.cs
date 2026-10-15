using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Quorum.Shared.Persistence;

internal sealed class PlatformUnitOfWork(NpgsqlDataSource dataSource) : IPlatformUnitOfWork, IAsyncDisposable
{
    private NpgsqlConnection? _connection;
    private PlatformTransaction? _transaction;

    public DbConnection Connection => _connection ??= dataSource.CreateConnection();

    public async Task<IPlatformTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (_transaction is not null)
        {
            throw new InvalidOperationException("A platform transaction is already in progress.");
        }

        var connection = (NpgsqlConnection)Connection;
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        _transaction = new PlatformTransaction(await connection.BeginTransactionAsync(cancellationToken), () => _transaction = null);
        return _transaction;
    }

    public void Enlist(DbContext context) => _transaction?.Enlist(context);

    public async ValueTask DisposeAsync()
    {
        if (_transaction is not null)
        {
            await _transaction.DisposeAsync();
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }

    private sealed class PlatformTransaction(NpgsqlTransaction transaction, Action onEnded) : IPlatformTransaction
    {
        private readonly List<DbContext> _enlisted = [];

        public void Enlist(DbContext context)
        {
            if (context.Database.CurrentTransaction is null)
            {
                context.Database.UseTransaction(transaction);
                _enlisted.Add(context);
            }
        }

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            await transaction.CommitAsync(cancellationToken);
            End();
        }

        public async ValueTask DisposeAsync()
        {
            // Rolls back when not committed.
            await transaction.DisposeAsync();
            End();
        }

        // Contexts keep working on the shared connection after the transaction, outside of it.
        private void End()
        {
            foreach (var context in _enlisted)
            {
                context.Database.UseTransaction(null);
            }

            _enlisted.Clear();
            onEnded();
        }
    }
}
