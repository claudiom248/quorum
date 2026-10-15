using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Quorum.Shared.Persistence;

public static class PostgresErrors
{
    public static bool IsUniqueViolation(this DbUpdateException exception) =>
        exception?.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
