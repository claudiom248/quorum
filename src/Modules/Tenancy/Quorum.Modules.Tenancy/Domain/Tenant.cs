namespace Quorum.Modules.Tenancy.Domain;

internal sealed class Tenant
{
    public const string DefaultName = "Default";

    private Tenant()
    {
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }

    public static Tenant CreateDefault(DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        Name = DefaultName,
        CreatedAt = now,
    };
}
