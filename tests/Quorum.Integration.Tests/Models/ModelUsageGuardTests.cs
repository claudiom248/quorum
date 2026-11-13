using Quorum.Modules.Models.Contracts;
using Quorum.Modules.Models.Domain;
using Quorum.Modules.Models.Features.Models;

namespace Quorum.Integration.Tests.Models;

/// <summary>What the guard refuses and what the scope rule allows, without a database (spec 019, AC-7 to AC-10).</summary>
public sealed class ModelUsageGuardTests
{
    private static readonly Guid TenantA = Guid.Parse("01a10000-0000-7000-8000-00000000000a");
    private static readonly Guid TenantB = Guid.Parse("01a10000-0000-7000-8000-00000000000b");
    private static readonly Guid ModelId = Guid.Parse("01a10000-0000-7000-8000-0000000000aa");

    [Fact] // AC-7: deleting a model in use lists its consumers by kind
    public async Task ADeleteIsBlockedByEveryConsumerOfTheModel()
    {
        var agent = new ModelUse(ModelUseKinds.Agent, Guid.NewGuid(), "Support", Tenant(TenantA), ModelCapabilities.Chat);
        var kb = new ModelUse(ModelUseKinds.KnowledgeBase, Guid.NewGuid(), "Docs", Tenant(TenantA), ModelCapabilities.Embeddings);
        var guard = new ModelUsageGuard([new FakeSource(agent), new FakeSource(kb)]);

        var uses = await guard.ForDeleteAsync(ModelId, CancellationToken.None);

        Assert.Equal([ModelUseKinds.Agent, ModelUseKinds.KnowledgeBase], uses.Select(use => use.Kind));
    }

    [Fact] // AC-8: removing Tools blocks the agents that need them, removing Embeddings blocks the knowledge bases on it
    public async Task RemovingACapabilityIsBlockedOnlyForTheConsumersThatNeedIt()
    {
        var withTools = new ModelUse(ModelUseKinds.Agent, Guid.NewGuid(), "Tools agent", Tenant(TenantA), ModelCapabilities.Chat | ModelCapabilities.Tools);
        var chatOnly = new ModelUse(ModelUseKinds.Agent, Guid.NewGuid(), "Chat agent", Tenant(TenantA), ModelCapabilities.Chat);
        var guard = new ModelUsageGuard([new FakeSource(withTools, chatOnly)]);

        var blocked = await guard.ForCapabilityRemovalAsync(ModelId, ModelCapabilities.Chat | ModelCapabilities.Tools, ModelCapabilities.Chat, CancellationToken.None);
        var kept = await guard.ForCapabilityRemovalAsync(ModelId, ModelCapabilities.Chat | ModelCapabilities.Tools, ModelCapabilities.Chat | ModelCapabilities.Tools, CancellationToken.None);

        Assert.Equal(["Tools agent"], blocked.Select(use => use.Name));
        Assert.Empty(kept);
    }

    [Fact] // AC-10: narrowing the scope so a consumer loses the model is refused
    public async Task NarrowingTheScopeIsBlockedWhenAConsumerWouldLoseTheModel()
    {
        var sharedWithB = new ModelUse(ModelUseKinds.Agent, Guid.NewGuid(), "Shared", new ModelUseScope(TenantB, false, []), ModelCapabilities.Chat);
        var guard = new ModelUsageGuard([new FakeSource(sharedWithB)]);
        var model = Model.Create(TenantA, "m", new Uri("http://model.test/v1"), "m", null, ModelCapabilities.Chat, 4096, null, null, ModelScope.Tenant, [TenantB], DateTimeOffset.UnixEpoch);

        var lost = await guard.ForScopeNarrowingAsync(model, ModelScope.Tenant, [], CancellationToken.None);
        var kept = await guard.ForScopeNarrowingAsync(model, ModelScope.Tenant, [TenantB], CancellationToken.None);

        Assert.Equal(["Shared"], lost.Select(use => use.Name));
        Assert.Empty(kept);
    }

    [Fact] // AC-9: a global consumer needs a global model; a limited one needs a model that lists its tenants
    public void AConsumerUsesOnlyModelsThatReachEverythingItReaches()
    {
        var limitedToB = new ModelUseScope(TenantB, false, []);
        var global = new ModelUseScope(TenantA, true, []);
        var tenantModel = Model.Create(TenantA, "m", new Uri("http://model.test/v1"), "m", null, ModelCapabilities.Chat, 4096, null, null, ModelScope.Tenant, [TenantB], DateTimeOffset.UnixEpoch);
        var globalModel = Model.Create(TenantA, "g", new Uri("http://model.test/v1"), "g", null, ModelCapabilities.Chat, 4096, null, null, ModelScope.Global, [], DateTimeOffset.UnixEpoch);

        Assert.True(ModelVisibility.Covers(tenantModel, limitedToB));
        Assert.False(ModelVisibility.Covers(tenantModel, global));
        Assert.True(ModelVisibility.Covers(globalModel, global));
    }

    private static ModelUseScope Tenant(Guid owner) => new(owner, false, []);

    private sealed class FakeSource(params ModelUse[] uses) : IModelUsageSource
    {
        public Task<IReadOnlyList<ModelUse>> UsesOfAsync(Guid modelId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ModelUse>>(uses);
    }
}
