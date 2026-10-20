using Microsoft.Extensions.DependencyInjection;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.KnowledgeBases.Contracts;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBases.Search;

/// <summary>What the Agents module learns about knowledge bases to describe a rag tool and to validate a binding (<see cref="IKnowledgeBaseCatalog"/>).</summary>
public sealed class KnowledgeBaseCatalogTests(PostgresFixture postgres, QdrantFixture qdrant)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(QuorumApp App, HttpClient Admin, Guid TenantId, Guid AssistantId, Guid KnowledgeBaseId, IReadOnlyDictionary<string, Guid> Fields) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Admin.Dispose();
            await App.DisposeAsync();
        }
    }

    private async Task<Setup> StartAsync(object? scope = null)
    {
        var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var collection = QdrantFixture.NewPrefix() + SampleCollections.Flat;
        await qdrant.SeedAsync([SampleCollections.CreateFlat(collection)]);
        var registered = await KnowledgeBaseApi.RegisterOkAsync(admin, (await qdrant.GetUrlAsync()).ToString(), collection, scope: scope);
        var fields = (await DictionaryApi.ListAsync(admin)).ToDictionary(f => f.Name, f => f.Id);
        return new Setup(app, admin, await TenantScope.TenantIdAsync(app), await SearchApi.AssistantIdAsync(app), registered.Id, fields);
    }

    private static async Task<T> InScopeAsync<T>(Setup setup, Guid tenantId, Func<IKnowledgeBaseCatalog, Task<T>> action)
    {
        await using var scope = TenantScope.Open(setup.App, tenantId);
        return await action(scope.ServiceProvider.GetRequiredService<IKnowledgeBaseCatalog>());
    }

    [Fact]
    public async Task AKnowledgeBaseIsDescribedWithItsNameItsDescriptionAndTheFieldsItMaps()
    {
        await using var setup = await StartAsync();

        var description = await InScopeAsync(
            setup,
            setup.TenantId,
            catalog => catalog.DescribeAsync(new BindingQuery(setup.TenantId, setup.AssistantId, [setup.KnowledgeBaseId], []), Ct));

        var knowledgeBase = Assert.Single(description.KnowledgeBases);
        Assert.Equal((setup.KnowledgeBaseId, "policies", "Company policies."), (knowledgeBase.Id, knowledgeBase.Name, knowledgeBase.Description));
        Assert.Equal(setup.Fields.Values.Order(), knowledgeBase.FieldIds.Order());
        Assert.Equal(
            [
                ("confidential", FilterFieldType.Boolean),
                ("department", FilterFieldType.Text),
                ("published_on", FilterFieldType.Date),
                ("year", FilterFieldType.Number),
            ],
            description.Fields.Select(f => (f.Name, f.Type)));
    }

    [Fact]
    public async Task AFieldTheKnowledgeBasesDoNotMapIsDescribedWhenItIsAskedFor()
    {
        await using var setup = await StartAsync();
        var region = await DictionaryApi.CreateOkAsync(setup.Admin, "region", "Text", "The sales region.");

        var description = await InScopeAsync(
            setup,
            setup.TenantId,
            catalog => catalog.DescribeAsync(new BindingQuery(setup.TenantId, setup.AssistantId, [setup.KnowledgeBaseId], [region]), Ct));

        var described = Assert.Single(description.Fields, f => f.Id == region);
        Assert.Equal(("region", FilterFieldType.Text, "The sales region."), (described.Name, described.Type, described.Description));
        Assert.DoesNotContain(region, Assert.Single(description.KnowledgeBases).FieldIds);
    }

    [Fact] // AC-44
    public async Task WhatDoesNotExistOrIsNotVisibleIsSimplyAbsent()
    {
        await using var setup = await StartAsync(new { kind = "Assistant", assistantIds = new[] { Guid.NewGuid() } });

        var forTheAssistant = await InScopeAsync(
            setup,
            setup.TenantId,
            catalog => catalog.DescribeAsync(new BindingQuery(setup.TenantId, setup.AssistantId, [setup.KnowledgeBaseId, Guid.NewGuid()], [Guid.NewGuid()]), Ct));
        var forAnAdministrator = await InScopeAsync(
            setup,
            setup.TenantId,
            catalog => catalog.DescribeAsync(new BindingQuery(setup.TenantId, AssistantId: null, [setup.KnowledgeBaseId], []), Ct));

        Assert.Empty(forTheAssistant.KnowledgeBases);
        Assert.Empty(forTheAssistant.Fields);
        Assert.Single(forAnAdministrator.KnowledgeBases);
    }

    [Fact]
    public async Task AnotherTenantThanTheRequestsSeesNothing()
    {
        await using var setup = await StartAsync();

        var description = await InScopeAsync(
            setup,
            setup.TenantId,
            catalog => catalog.DescribeAsync(new BindingQuery(Guid.NewGuid(), setup.AssistantId, [setup.KnowledgeBaseId], [.. setup.Fields.Values]), Ct));
        var check = await InScopeAsync(
            setup,
            setup.TenantId,
            catalog => catalog.CheckScopeAsync(new ScopeCheckQuery(Guid.NewGuid(), new BindingScope(BindingScopeKind.Tenant, [], []), [setup.KnowledgeBaseId], [.. setup.Fields.Values]), Ct));

        Assert.Empty(description.KnowledgeBases);
        Assert.Empty(description.Fields);
        Assert.Equal([setup.KnowledgeBaseId], check.HiddenKnowledgeBaseIds);
        Assert.Equal(setup.Fields.Count, check.HiddenFieldIds.Count);
    }

    [Fact]
    public async Task ABindingIsVisibleToAScopeWhenEveryoneInItSeesWhatItUses()
    {
        await using var setup = await StartAsync();
        var inTenant = new BindingScope(BindingScopeKind.Tenant, [], []);
        var forAssistant = new BindingScope(BindingScopeKind.Assistant, [], [setup.AssistantId]);

        var tenantScope = await InScopeAsync(setup, setup.TenantId, c => c.CheckScopeAsync(new ScopeCheckQuery(setup.TenantId, inTenant, [setup.KnowledgeBaseId], [.. setup.Fields.Values]), Ct));
        var assistantScope = await InScopeAsync(setup, setup.TenantId, c => c.CheckScopeAsync(new ScopeCheckQuery(setup.TenantId, forAssistant, [setup.KnowledgeBaseId], [.. setup.Fields.Values]), Ct));

        Assert.True(tenantScope.IsVisible);
        Assert.True(assistantScope.IsVisible);
    }

    [Fact] // AC-22
    public async Task APrivateKnowledgeBaseAndPrivateFieldsAreNotVisibleToAGlobalScope()
    {
        await using var setup = await StartAsync();
        var global = new BindingScope(BindingScopeKind.Global, [], []);

        var check = await InScopeAsync(setup, setup.TenantId, c => c.CheckScopeAsync(new ScopeCheckQuery(setup.TenantId, global, [setup.KnowledgeBaseId], [.. setup.Fields.Values]), Ct));

        Assert.False(check.IsVisible);
        Assert.Equal([setup.KnowledgeBaseId], check.HiddenKnowledgeBaseIds);
        Assert.Equal(setup.Fields.Values.Order(), check.HiddenFieldIds.Order());
    }

    [Fact]
    public async Task AKnowledgeBaseThatDoesNotExistIsHiddenLikeAnyOther()
    {
        await using var setup = await StartAsync();
        var missing = Guid.NewGuid();

        var check = await InScopeAsync(
            setup,
            setup.TenantId,
            c => c.CheckScopeAsync(new ScopeCheckQuery(setup.TenantId, new BindingScope(BindingScopeKind.Tenant, [], []), [setup.KnowledgeBaseId, missing], [missing]), Ct));

        Assert.Equal([missing], check.HiddenKnowledgeBaseIds);
        Assert.Equal([missing], check.HiddenFieldIds);
    }

    [Fact]
    public async Task ANarrowScopedKnowledgeBaseIsHiddenFromAWiderScope()
    {
        await using var setup = await StartAsync(new { kind = "Assistant", assistantIds = new[] { Guid.NewGuid() } });

        var inTenant = await InScopeAsync(
            setup,
            setup.TenantId,
            c => c.CheckScopeAsync(new ScopeCheckQuery(setup.TenantId, new BindingScope(BindingScopeKind.Tenant, [], []), [setup.KnowledgeBaseId], []), Ct));
        var otherAssistant = await InScopeAsync(
            setup,
            setup.TenantId,
            c => c.CheckScopeAsync(new ScopeCheckQuery(setup.TenantId, new BindingScope(BindingScopeKind.Assistant, [], [setup.AssistantId]), [setup.KnowledgeBaseId], []), Ct));

        Assert.Equal([setup.KnowledgeBaseId], inTenant.HiddenKnowledgeBaseIds);
        Assert.Equal([setup.KnowledgeBaseId], otherAssistant.HiddenKnowledgeBaseIds);
    }
}
