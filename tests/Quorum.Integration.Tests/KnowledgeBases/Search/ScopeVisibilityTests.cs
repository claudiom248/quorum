using Quorum.Modules.KnowledgeBases.Contracts;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Features.Search;
using Quorum.Modules.KnowledgeBases.Mapping;

namespace Quorum.Integration.Tests.KnowledgeBases.Search;

/// <summary>The rule between scopes (spec 003): a binding may use only what everyone in its scope can see.</summary>
public sealed class ScopeVisibilityTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();
    private static readonly Guid Third = Guid.NewGuid();
    private static readonly Guid AssistantA = Guid.NewGuid();
    private static readonly Guid AssistantB = Guid.NewGuid();

    private static BindingScope Global { get; } = new(BindingScopeKind.Global, [], []);

    private static BindingScope Tenants(params Guid[] tenants) => new(BindingScopeKind.Tenant, tenants, []);

    private static BindingScope Assistants(params Guid[] assistants) => new(BindingScopeKind.Assistant, [], assistants);

    private static KnowledgeBase Base(Guid owner, KnowledgeBaseScope scope, Guid[]? tenants = null, Guid[]? assistants = null)
    {
        var knowledgeBase = KnowledgeBase.Create(
            owner,
            "kb",
            "A knowledge base.",
            new QdrantConnectionSettings("http://qdrant.test:6334", "docs", null, null),
            "model",
            null,
            new KnowledgeBaseMapping(new MappingRoles("text", null, null, null, null), []),
            scope,
            tenants ?? [],
            assistants ?? [],
            DateTimeOffset.UtcNow);
        return knowledgeBase;
    }

    private static DictionaryField Field(Guid owner, DictionaryFieldScope scope) =>
        DictionaryField.Create(owner, "year", DictionaryFieldType.Number, null, scope, DateTimeOffset.UtcNow);

    [Fact]
    public void AGlobalToolMayOnlyUseGlobalKnowledgeBases()
    {
        Assert.True(ScopeVisibility.KnowledgeBaseIsVisible(Base(Owner, KnowledgeBaseScope.Global), Owner, Global));
        Assert.True(ScopeVisibility.KnowledgeBaseIsVisible(Base(Other, KnowledgeBaseScope.Global), Owner, Global));
        Assert.False(ScopeVisibility.KnowledgeBaseIsVisible(Base(Owner, KnowledgeBaseScope.Tenant), Owner, Global));
        Assert.False(ScopeVisibility.KnowledgeBaseIsVisible(Base(Owner, KnowledgeBaseScope.Tenant, [Other]), Owner, Global));
        Assert.False(ScopeVisibility.KnowledgeBaseIsVisible(Base(Owner, KnowledgeBaseScope.Assistant, assistants: [AssistantA]), Owner, Global));
    }

    [Fact]
    public void ATenantToolMayUseWhatEveryTenantOfItsScopeSees()
    {
        var scope = Tenants(Other, Third);

        Assert.True(ScopeVisibility.KnowledgeBaseIsVisible(Base(Owner, KnowledgeBaseScope.Global), Owner, scope));
        Assert.True(ScopeVisibility.KnowledgeBaseIsVisible(Base(Owner, KnowledgeBaseScope.Tenant, [Other, Third]), Owner, scope));
        // The owner of the tool and the one of the knowledge base are different tenants, and both are in the scope.
        Assert.True(ScopeVisibility.KnowledgeBaseIsVisible(Base(Third, KnowledgeBaseScope.Tenant, [Owner, Other]), Owner, scope));
        Assert.False(ScopeVisibility.KnowledgeBaseIsVisible(Base(Owner, KnowledgeBaseScope.Tenant, [Other]), Owner, scope));
        Assert.False(ScopeVisibility.KnowledgeBaseIsVisible(Base(Owner, KnowledgeBaseScope.Tenant), Owner, scope));
        Assert.False(ScopeVisibility.KnowledgeBaseIsVisible(Base(Third, KnowledgeBaseScope.Tenant, [Other]), Owner, scope));
    }

    [Fact]
    public void ATenantToolWithoutOtherTenantsMayUseTheOwnersPrivateKnowledgeBases()
    {
        Assert.True(ScopeVisibility.KnowledgeBaseIsVisible(Base(Owner, KnowledgeBaseScope.Tenant), Owner, Tenants()));
        Assert.False(ScopeVisibility.KnowledgeBaseIsVisible(Base(Other, KnowledgeBaseScope.Tenant), Owner, Tenants()));
    }

    [Fact]
    public void AnAssistantScopedKnowledgeBaseIsNeverForEveryAssistantOfATenant()
    {
        var scoped = Base(Owner, KnowledgeBaseScope.Assistant, assistants: [AssistantA, AssistantB]);

        Assert.False(ScopeVisibility.KnowledgeBaseIsVisible(scoped, Owner, Tenants()));
        Assert.False(ScopeVisibility.KnowledgeBaseIsVisible(scoped, Owner, Tenants(Other)));
    }

    [Fact]
    public void AnAssistantToolMayUseAnAssistantScopedKnowledgeBaseThatListsAllItsAssistants()
    {
        var scoped = Base(Owner, KnowledgeBaseScope.Assistant, assistants: [AssistantA, AssistantB]);

        Assert.True(ScopeVisibility.KnowledgeBaseIsVisible(scoped, Owner, Assistants(AssistantA)));
        Assert.True(ScopeVisibility.KnowledgeBaseIsVisible(scoped, Owner, Assistants(AssistantA, AssistantB)));
        Assert.False(ScopeVisibility.KnowledgeBaseIsVisible(scoped, Owner, Assistants(AssistantA, Guid.NewGuid())));
        Assert.False(ScopeVisibility.KnowledgeBaseIsVisible(Base(Other, KnowledgeBaseScope.Assistant, assistants: [AssistantA]), Owner, Assistants(AssistantA)));
    }

    [Fact]
    public void AnAssistantToolMayUseWhatItsOwnerSees()
    {
        var scope = Assistants(AssistantA);

        Assert.True(ScopeVisibility.KnowledgeBaseIsVisible(Base(Owner, KnowledgeBaseScope.Tenant), Owner, scope));
        Assert.True(ScopeVisibility.KnowledgeBaseIsVisible(Base(Other, KnowledgeBaseScope.Global), Owner, scope));
        Assert.True(ScopeVisibility.KnowledgeBaseIsVisible(Base(Other, KnowledgeBaseScope.Tenant, [Owner]), Owner, scope));
        Assert.False(ScopeVisibility.KnowledgeBaseIsVisible(Base(Other, KnowledgeBaseScope.Tenant, [Third]), Owner, scope));
    }

    [Fact]
    public void AGlobalToolMayOnlyUseGlobalFields()
    {
        Assert.True(ScopeVisibility.FieldIsVisible(Field(Other, DictionaryFieldScope.Global), Owner, Global));
        Assert.False(ScopeVisibility.FieldIsVisible(Field(Owner, DictionaryFieldScope.Tenant), Owner, Global));
    }

    [Fact]
    public void ATenantToolMayUseAPrivateFieldOnlyWhenTheOwnerIsTheOnlyTenant()
    {
        var own = Field(Owner, DictionaryFieldScope.Tenant);

        Assert.True(ScopeVisibility.FieldIsVisible(own, Owner, Tenants()));
        Assert.False(ScopeVisibility.FieldIsVisible(own, Owner, Tenants(Other)));
        Assert.True(ScopeVisibility.FieldIsVisible(Field(Third, DictionaryFieldScope.Global), Owner, Tenants(Other)));
        Assert.False(ScopeVisibility.FieldIsVisible(Field(Other, DictionaryFieldScope.Tenant), Owner, Tenants()));
    }

    [Fact]
    public void AnAssistantToolMayUseGlobalFieldsAndItsOwnersPrivateOnes()
    {
        Assert.True(ScopeVisibility.FieldIsVisible(Field(Owner, DictionaryFieldScope.Tenant), Owner, Assistants(AssistantA)));
        Assert.True(ScopeVisibility.FieldIsVisible(Field(Other, DictionaryFieldScope.Global), Owner, Assistants(AssistantA)));
        Assert.False(ScopeVisibility.FieldIsVisible(Field(Other, DictionaryFieldScope.Tenant), Owner, Assistants(AssistantA)));
    }
}
