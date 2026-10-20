namespace Quorum.Modules.Agents.Contracts;

/// <summary>Lets other modules check the assistants of the current tenant that a scope names.</summary>
public interface IAssistantDirectory
{
    /// <summary>The ids among <paramref name="assistantIds"/> that are not assistants of the current tenant.</summary>
    Task<IReadOnlyList<Guid>> MissingAsync(IReadOnlyCollection<Guid> assistantIds, CancellationToken cancellationToken);
}
