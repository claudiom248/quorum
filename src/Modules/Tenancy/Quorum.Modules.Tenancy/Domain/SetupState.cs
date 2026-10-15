namespace Quorum.Modules.Tenancy.Domain;

/// <summary>
/// Present once the installation is set up. The fixed primary key makes completion atomic:
/// a second, concurrent completion fails on the unique key.
/// </summary>
internal sealed class SetupState
{
    public const int SingletonId = 1;

    private SetupState()
    {
    }

    public int Id { get; private set; }

    public DateTimeOffset CompletedAt { get; private set; }

    public static SetupState Completed(DateTimeOffset now) => new() { Id = SingletonId, CompletedAt = now };
}
