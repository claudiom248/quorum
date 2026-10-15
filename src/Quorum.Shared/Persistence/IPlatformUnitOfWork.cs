using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Quorum.Shared.Persistence;

/// <summary>
/// The request's single connection to the platform database. Every module DbContext in the same scope
/// uses it, so one transaction can span several modules (e.g. completing the setup).
/// </summary>
public interface IPlatformUnitOfWork
{
    DbConnection Connection { get; }

    /// <summary>
    /// Starts a transaction that every module DbContext of the scope joins when it saves.
    /// Disposing it without <see cref="IPlatformTransaction.CommitAsync"/> rolls everything back.
    /// </summary>
    Task<IPlatformTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>Joins the open transaction, if any. Called by the save interceptor of module DbContexts.</summary>
    void Enlist(DbContext context);
}

public interface IPlatformTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}
