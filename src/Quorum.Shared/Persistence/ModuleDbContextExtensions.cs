using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Quorum.Shared.Persistence;

public static class ModuleDbContextExtensions
{
    /// <summary>
    /// Registers a module's DbContext on the shared platform connection, with its migrations history in the
    /// module's own schema, and migrates it at startup.
    /// </summary>
    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services, string schema)
        where TContext : DbContext
    {
        services.TryAddScoped<IPlatformUnitOfWork, PlatformUnitOfWork>();

        services.AddDbContext<TContext>((provider, options) =>
        {
            var unitOfWork = provider.GetRequiredService<IPlatformUnitOfWork>();
            options
                .UseNpgsql(unitOfWork.Connection, npgsql => npgsql.MigrationsHistoryTable(HistoryRepository.DefaultTableName, schema))
                .AddInterceptors(new EnlistInPlatformTransactionInterceptor(unitOfWork));
        });

        services.AddSingleton<IDatabaseInitializer, ModuleMigrator<TContext>>();

        return services;
    }

    /// <summary>Same options for design-time tools (<c>dotnet ef</c>), which never connect.</summary>
    public static DbContextOptions<TContext> DesignTimeOptions<TContext>(string schema)
        where TContext : DbContext =>
        new DbContextOptionsBuilder<TContext>()
            .UseNpgsql("Host=localhost;Database=quorum_design", npgsql => npgsql.MigrationsHistoryTable(HistoryRepository.DefaultTableName, schema))
            .Options;
}
