using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Agents.Persistence;

namespace Quorum.Modules.Agents.Provisioning;

/// <summary><see cref="IAssistantDirectory"/>: the tenant filter of the context is what limits the answer to the current tenant's assistants.</summary>
internal sealed class AssistantDirectory(AgentsDbContext db) : IAssistantDirectory
{
    public async Task<IReadOnlyList<Guid>> MissingAsync(IReadOnlyCollection<Guid> assistantIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assistantIds);

        var wanted = assistantIds.Distinct().ToList();
        var found = await db.Assistants.AsNoTracking().Where(a => wanted.Contains(a.Id)).Select(a => a.Id).ToListAsync(cancellationToken);
        return [.. wanted.Except(found)];
    }
}
