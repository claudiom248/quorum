namespace Quorum.Host.Infrastructure.Database;

internal static class PlatformDatabaseExtensions
{
    public const string ConnectionStringName = "Platform";

    public static IServiceCollection AddPlatformDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"The platform database connection string is missing: set ConnectionStrings__{ConnectionStringName} (QUORUM_DB_CONNECTION in docker compose).");
        }

        services.AddNpgsqlDataSource(connectionString);
        services.AddOptions<DatabaseOptions>().BindConfiguration(DatabaseOptions.SectionName);
        services.AddHostedService<DatabaseStartup>();

        return services;
    }
}
