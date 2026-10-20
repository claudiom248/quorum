using Quorum.Modules.KnowledgeBases.Contracts;
using Quorum.Modules.KnowledgeBases.Domain;

namespace Quorum.Modules.KnowledgeBases.Features.Search;

/// <summary>
/// The rule between scopes (spec 003 D-5, D-10): what a binding of a given scope may use is what every tenant and assistant in that
/// scope can see. A knowledge base or field only some of them see would be offered to the others and then ignored, or worse, work
/// for some and fail for the rest.
/// </summary>
internal static class ScopeVisibility
{
    public static bool KnowledgeBaseIsVisible(KnowledgeBase knowledgeBase, Guid ownerTenantId, BindingScope scope)
    {
        ArgumentNullException.ThrowIfNull(knowledgeBase);
        ArgumentNullException.ThrowIfNull(scope);

        return scope.Kind switch
        {
            BindingScopeKind.Global => knowledgeBase.Scope == KnowledgeBaseScope.Global,

            // An assistant-scoped knowledge base is for some assistants of its owner, never for every assistant of a tenant.
            BindingScopeKind.Tenant => knowledgeBase.Scope != KnowledgeBaseScope.Assistant
                && Tenants(ownerTenantId, scope).All(tenantId => SeenByTenant(knowledgeBase, tenantId)),

            BindingScopeKind.Assistant => knowledgeBase.Scope == KnowledgeBaseScope.Assistant
                ? knowledgeBase.TenantId == ownerTenantId && scope.AssistantIds.All(knowledgeBase.ScopeAssistantIds.Contains)
                : SeenByTenant(knowledgeBase, ownerTenantId),

            _ => false,
        };
    }

    public static bool FieldIsVisible(DictionaryField field, Guid ownerTenantId, BindingScope scope)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(scope);

        return scope.Kind switch
        {
            BindingScopeKind.Global => field.Scope == DictionaryFieldScope.Global,
            BindingScopeKind.Tenant => Tenants(ownerTenantId, scope).All(tenantId => field.Scope == DictionaryFieldScope.Global || field.TenantId == tenantId),
            BindingScopeKind.Assistant => field.Scope == DictionaryFieldScope.Global || field.TenantId == ownerTenantId,
            _ => false,
        };
    }

    private static IEnumerable<Guid> Tenants(Guid ownerTenantId, BindingScope scope) => scope.TenantIds.Append(ownerTenantId).Distinct();

    private static bool SeenByTenant(KnowledgeBase knowledgeBase, Guid tenantId) =>
        knowledgeBase.Scope != KnowledgeBaseScope.Assistant
            ? knowledgeBase.TenantId == tenantId
              || knowledgeBase.Scope == KnowledgeBaseScope.Global
              || knowledgeBase.ScopeTenantIds.Contains(tenantId)
            : knowledgeBase.TenantId == tenantId;
}
