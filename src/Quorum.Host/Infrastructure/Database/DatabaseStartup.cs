using Microsoft.Extensions.Options;
using Npgsql;
using Quorum.Shared.Persistence;

namespace Quorum.Host.Infrastructure.Database;

/// <summary>
/// Waits for the platform database, then runs every module's <see cref="IDatabaseInitializer"/>.
/// Hosted services start before the server, so no request is served until this completes.
/// </summary>
internal sealed partial class DatabaseStartup(
    NpgsqlDataSource dataSource,
    IEnumerable<IDatabaseInitializer> initializers,
    IOptions<DatabaseOptions> options,
    TimeProvider timeProvider,
    ILogger<DatabaseStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await WaitForDatabaseAsync(cancellationToken);

        foreach (var initializer in initializers)
        {
            await initializer.InitializeAsync(cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task WaitForDatabaseAsync(CancellationToken cancellationToken)
    {
        var timeout = options.Value.StartupTimeout;
        var deadline = timeProvider.GetUtcNow() + timeout;

        while (true)
        {
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
                LogDatabaseReachable(logger);
                return;
            }
            catch (NpgsqlException exception) when (timeProvider.GetUtcNow() < deadline)
            {
                LogDatabaseNotReady(logger, exception.Message);
                await Task.Delay(options.Value.RetryInterval, timeProvider, cancellationToken);
            }
            catch (NpgsqlException exception)
            {
                LogDatabaseUnreachable(logger, exception, timeout);
                throw new InvalidOperationException(
                    $"The platform database is unreachable after {timeout}. Check ConnectionStrings__Platform (QUORUM_DB_CONNECTION in docker compose).",
                    exception);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Platform database is reachable")]
    private static partial void LogDatabaseReachable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Platform database not ready yet: {Reason}")]
    private static partial void LogDatabaseNotReady(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Platform database is unreachable after {Timeout}")]
    private static partial void LogDatabaseUnreachable(ILogger logger, Exception exception, TimeSpan timeout);
}
