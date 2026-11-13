using Quorum.Modules.Models.Contracts;
using Quorum.Modules.Models.Domain;

namespace Quorum.Modules.Models.Features.Models;

/// <summary>
/// The changes to a model that its consumers would not survive (spec 019 D-10, D-11, D-12), answered from the consumers' own reports
/// through <see cref="IModelUsageSource"/>, so this module never references agents or knowledge bases.
/// </summary>
internal sealed class ModelUsageGuard(IEnumerable<IModelUsageSource> sources)
{
    private readonly IReadOnlyList<IModelUsageSource> _sources = [.. sources];

    /// <summary>Every consumer of the model, of every kind.</summary>
    public async Task<IReadOnlyList<ModelUse>> UsesAsync(Guid modelId, CancellationToken cancellationToken)
    {
        var uses = new List<ModelUse>();
        foreach (var source in _sources)
        {
            uses.AddRange(await source.UsesOfAsync(modelId, cancellationToken));
        }

        return uses;
    }

    /// <summary>Consumers that would block the delete (D-10).</summary>
    public Task<IReadOnlyList<ModelUse>> ForDeleteAsync(Guid modelId, CancellationToken cancellationToken) => UsesAsync(modelId, cancellationToken);

    /// <summary>Consumers that rely on a capability the model is about to lose (D-11).</summary>
    public async Task<IReadOnlyList<ModelUse>> ForCapabilityRemovalAsync(Guid modelId, ModelCapabilities before, ModelCapabilities after, CancellationToken cancellationToken)
    {
        var removed = before & ~after;
        if (removed == ModelCapabilities.None)
        {
            return [];
        }

        return [.. (await UsesAsync(modelId, cancellationToken)).Where(use => (use.Required & removed) != ModelCapabilities.None)];
    }

    /// <summary>Consumers the model would no longer reach with its new scope (D-12).</summary>
    public async Task<IReadOnlyList<ModelUse>> ForScopeNarrowingAsync(Model model, ModelScope scope, IEnumerable<Guid> scopeTenantIds, CancellationToken cancellationToken)
    {
        var tenants = scopeTenantIds.ToList();
        return [.. (await UsesAsync(model.Id, cancellationToken))
            .Where(use => !ModelVisibility.Covers(model.TenantId, scope, tenants, use.Scope))];
    }
}
