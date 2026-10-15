namespace Quorum.Shared.Persistence;

/// <summary>
/// Prepares a module's database objects (e.g. applies its migrations). Runs once at startup,
/// after the platform database is reachable and before the app accepts requests.
/// </summary>
public interface IDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken);
}
