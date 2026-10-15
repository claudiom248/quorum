using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Quorum.Shared.Persistence;

/// <summary>
/// Makes a module DbContext save inside the platform transaction, when one is open,
/// instead of starting its own transaction on the shared connection.
/// </summary>
internal sealed class EnlistInPlatformTransactionInterceptor(IPlatformUnitOfWork unitOfWork) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } context)
        {
            unitOfWork.Enlist(context);
        }

        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(SavingChanges(eventData, result));
}
