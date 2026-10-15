namespace Quorum.Host.Infrastructure.Database;

internal sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>
    /// How long startup waits for the platform database before giving up.
    /// </summary>
    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(2);
}
