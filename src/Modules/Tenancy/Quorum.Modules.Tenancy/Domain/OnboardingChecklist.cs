namespace Quorum.Modules.Tenancy.Domain;

[Flags]
internal enum ChecklistSteps
{
    None = 0,
    Model = 1,
    Prompt = 2,
    Tools = 4,
    Knowledge = 8,
}

/// <summary>
/// The getting-started checklist of the installation (one row, fixed key). Completed steps are a flag set so that marking
/// one done is an atomic bitwise update that two concurrent requests cannot lose.
/// </summary>
internal sealed class OnboardingChecklist
{
    public const int SingletonId = 1;

    private OnboardingChecklist()
    {
    }

    public int Id { get; private set; }

    public ChecklistSteps Completed { get; private set; }

    public bool Dismissed { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>A fresh checklist: completing the setup is what makes the model step done.</summary>
    public static OnboardingChecklist Start(DateTimeOffset now) =>
        new() { Id = SingletonId, Completed = ChecklistSteps.Model, UpdatedAt = now };
}
