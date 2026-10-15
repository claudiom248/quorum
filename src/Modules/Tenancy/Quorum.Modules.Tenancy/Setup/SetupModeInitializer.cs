using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quorum.Modules.Tenancy.Persistence;
using Quorum.Shared.Persistence;

namespace Quorum.Modules.Tenancy.Setup;

/// <summary>After the tenancy migrations, enters setup mode if the installation has never been set up.</summary>
internal sealed partial class SetupModeInitializer(
    IServiceScopeFactory scopeFactory,
    SetupMode setupMode,
    ILogger<SetupModeInitializer> logger) : IDatabaseInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        if (await db.SetupStates.AnyAsync(cancellationToken))
        {
            return;
        }

        LogSetupToken(logger, setupMode.Activate());
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Quorum is not set up yet. Open it in a browser and enter this setup token: {SetupToken}")]
    private static partial void LogSetupToken(ILogger logger, string setupToken);
}
