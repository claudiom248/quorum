using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.KnowledgeBases;
using Quorum.Integration.Tests.Tools;

namespace Quorum.Integration.Tests.Rag;

/// <summary>What the administrator is told about a rag tool whose knowledge bases or fields changed since it was saved.</summary>
public sealed class RagToolWarningTests(SharedQuorumApp host, QdrantFixture qdrant) : IClassFixture<SharedQuorumApp>, IAsyncLifetime
{
    public ValueTask InitializeAsync() => new(host.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(RagApi.Setup Setup, Guid Kb, Guid Tool)> StartAsync(SharedQuorumApp host, QdrantFixture qdrant)
    {
        var setup = await RagApi.Setup.OverAsync(host, qdrant);
        var kb = await setup.RegisterAsync("policies");
        var fields = await setup.FieldsAsync();
        var tool = await RagApi.CreateOkAsync(
            setup.Admin,
            RagApi.Body(knowledgeBaseIds: [kb], exposedFieldIds: [fields["year"]], fixedFilters: new { confidential = false }));
        return (setup, kb, tool.Id);
    }

    private static async Task<string[]> CodesAsync(RagApi.Setup setup, Guid tool)
    {
        var detail = (await RagApi.GetAsync(setup.Admin, tool)).Warnings.Select(w => w.Code).Order(StringComparer.Ordinal).ToArray();
        var listed = Assert.Single(await RagApi.ListAsync(setup.Admin)).Warnings.Select(w => w.Code).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(detail, listed);
        return detail;
    }

    [Fact]
    public async Task AToolWhoseKnowledgeBasesAndFieldsAreAllThereHasNoWarning()
    {
        var (setup, _, tool) = await StartAsync(host, qdrant);
        await using var owner = setup;

        Assert.Empty(await CodesAsync(setup, tool));
    }

    [Fact]
    public async Task AFieldExposedToTheModelThatIsGoneIsReported()
    {
        var (setup, _, tool) = await StartAsync(host, qdrant);
        await using var owner = setup;
        await ToolFixtures.ExecuteAsync(setup.App, "DELETE FROM knowledge_base.dictionary_fields WHERE \"Name\" = 'year'");

        Assert.Equal(["rag.field_missing"], await CodesAsync(setup, tool));
    }

    [Fact]
    public async Task AFieldOfAFixedFilterThatIsGoneIsReportedAsBreakingEverySearch()
    {
        var (setup, _, tool) = await StartAsync(host, qdrant);
        await using var owner = setup;
        await ToolFixtures.ExecuteAsync(setup.App, "DELETE FROM knowledge_base.dictionary_fields WHERE \"Name\" = 'confidential'");

        var warning = Assert.Single((await RagApi.GetAsync(setup.Admin, tool)).Warnings);

        Assert.Equal("rag.fixed_filter_field_missing", warning.Code);
        Assert.Contains("every search fails", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeletedKnowledgeBaseIsReported()
    {
        await using var setup = await RagApi.Setup.OverAsync(host, qdrant);
        var kept = await setup.RegisterAsync("kept");
        var gone = await setup.RegisterAsync("gone");
        var both = await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [kept, gone]));
        using var deleteOne = await setup.Admin.DeleteAsync(KnowledgeBaseApi.One(gone), Ct);
        deleteOne.EnsureSuccessStatusCode();

        Assert.Equal(["rag.knowledge_base_missing"], await CodesAsync(setup, both.Id));

        using var deleteLast = await setup.Admin.DeleteAsync(KnowledgeBaseApi.One(kept), Ct);
        deleteLast.EnsureSuccessStatusCode();

        Assert.Equal(["rag.no_knowledge_base_visible"], await CodesAsync(setup, both.Id));
    }
}
