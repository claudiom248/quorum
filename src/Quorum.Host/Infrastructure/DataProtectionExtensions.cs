using Microsoft.AspNetCore.DataProtection;

namespace Quorum.Host.Infrastructure;

internal static class DataProtectionExtensions
{
    /// <summary>
    /// The key ring is the master key for secrets stored in the database (e.g. model API keys),
    /// so it lives in its own volume and never in the database itself.
    /// </summary>
    public static IServiceCollection AddQuorumDataProtection(this IServiceCollection services, IConfiguration configuration)
    {
        var keysPath = configuration["DataProtection:KeysPath"];
        if (string.IsNullOrWhiteSpace(keysPath))
        {
            throw new InvalidOperationException("DataProtection:KeysPath is not configured: set it to a persistent directory (a Docker volume in production).");
        }

        services.AddDataProtection()
            .SetApplicationName("Quorum")
            .PersistKeysToFileSystem(new DirectoryInfo(keysPath));

        return services;
    }
}
