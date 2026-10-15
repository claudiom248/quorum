using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Quorum.Shared.Persistence;

internal sealed class ModuleMigrator<TContext>(IServiceScopeFactory scopeFactory) : IDatabaseInitializer
    where TContext : DbContext
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();

        // On a new database EF reads the history table before creating it and logs that as an error,
        // which looks alarming in a fresh install's logs. Creating it up front keeps the first start clean.
        if (context.Model.GetDefaultSchema() is { } schema)
        {
            // The schema is a constant of the module's DbContext, never user input.
            var createHistory = $"""
                CREATE SCHEMA IF NOT EXISTS "{schema}";
                CREATE TABLE IF NOT EXISTS "{schema}"."__EFMigrationsHistory" (
                    "MigrationId" character varying(150) NOT NULL PRIMARY KEY,
                    "ProductVersion" character varying(32) NOT NULL);
                """;
            await context.Database.ExecuteSqlRawAsync(createHistory, cancellationToken);
        }

        await context.Database.MigrateAsync(cancellationToken);
    }
}
