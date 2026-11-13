using Quorum.Modules.Models.Contracts;
using Quorum.Modules.Models.Domain;

namespace Quorum.Modules.Models.Features.Models;

/// <summary>
/// Who sees a model and whether a consumer may use it (spec 019 D-7, D-8). The single predicate for "visible to a tenant", used by the lists,
/// the gets and the consumers' checks, as <c>VisibleAgents</c> is for agents.
/// </summary>
internal static class ModelVisibility
{
    /// <summary>The tenants a model reaches: its owner and the ones it lists.</summary>
    public static IReadOnlySet<Guid> Reach(Model model) => new HashSet<Guid>(model.ScopeTenantIds) { model.TenantId };

    /// <summary>
    /// A consumer (an agent or a knowledge base) may use a model only when the model reaches everything the consumer reaches: a global
    /// consumer needs a global model, a limited one needs a model that lists its owner and its tenants, or is global (D-8).
    /// </summary>
    public static bool Covers(Model model, ModelUseScope consumer)
    {
        if (model.Scope == ModelScope.Global)
        {
            return true;
        }

        if (consumer.Global)
        {
            return false;
        }

        var reach = Reach(model);
        return reach.Contains(consumer.OwnerTenantId) && consumer.ScopeTenantIds.All(reach.Contains);
    }

    /// <summary>The same rule for a model whose scope is about to change: the scope it would have, from its owner.</summary>
    public static bool Covers(Guid ownerTenantId, ModelScope scope, IEnumerable<Guid> scopeTenantIds, ModelUseScope consumer)
    {
        if (scope == ModelScope.Global)
        {
            return true;
        }

        if (consumer.Global)
        {
            return false;
        }

        var reach = new HashSet<Guid>(scopeTenantIds) { ownerTenantId };
        return reach.Contains(consumer.OwnerTenantId) && consumer.ScopeTenantIds.All(reach.Contains);
    }
}
