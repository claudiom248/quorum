using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.KnowledgeBases;
using Quorum.Integration.Tests.KnowledgeBases.Lookups;
using Quorum.Integration.Tests.Rag;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.LookupTools;

/// <summary>
/// The roles of a knowledge base in a lookup tool, one namespace for its targets, and the guards that keep a mapping or a name from breaking
/// a saved tool (spec 012 Amendment 1, AC-A1-3, AC-A1-4, AC-A1-5, AC-A1-11).
/// </summary>
public sealed class ToolTargetRulesTests(PostgresFixture postgres, QdrantFixture qdrant)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A knowledge base whose entities are named by <c>title</c>: its entity lookup is created with it, and is named as the knowledge base.</summary>
    private static readonly object EntityMapping = new
    {
        roles = new { text = "text", title = "title" },
        fields = Array.Empty<object>(),
        entityName = "title",
    };

    [Fact] // AC-A1-1
    public async Task ALiveKnowledgeBaseIsADocumentKnowledgeBaseAndNotOneOfEntitiesOnly()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var entitiesOnly = await setup.RegisterAsync("vendors", mapping: EntityOnlyKnowledgeBaseTests.Mapping);
        var manual = await LookupApi.CreateManualOkAsync(setup.Admin, LookupApi.ManualBody(name: "lists"));

        using var live = await LookupToolApi.CreateAsync(setup.Admin, LookupToolApi.Body(lookups: [LookupToolApi.Bind(manual)], knowledgeBaseIds: [entitiesOnly]));

        Assert.Equal(HttpStatusCode.BadRequest, live.StatusCode);
        Assert.Equal("lookup_tool.knowledge_base_not_documentary", await LookupToolApi.ProblemCodeAsync(live));
    }

    [Fact] // AC-A1-3
    public async Task AKnowledgeBaseIsNotBothAnEntityLookupAndALiveKnowledgeBaseOfOneTool()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var knowledgeBase = await RegisterEntityKnowledgeBaseAsync(setup, "vendors");
        var entity = await EntityLookupOfAsync(setup, knowledgeBase);

        // The lookup has the name of its knowledge base, so it takes an alias here: otherwise the names clash first (AC-A1-4).
        using var both = await LookupToolApi.CreateAsync(setup.Admin, LookupToolApi.Body(lookups: [LookupToolApi.Bind(entity, "vendor_list")], knowledgeBaseIds: [knowledgeBase]));
        var lookupOnly = await LookupToolApi.CreateOkAsync(setup.Admin, LookupToolApi.Body(lookups: [LookupToolApi.Bind(entity, "vendor_list")]));

        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        Assert.Equal("lookup_tool.knowledge_base_both_views", await LookupToolApi.ProblemCodeAsync(both));
        Assert.NotNull(lookupOnly.Definition);
    }

    [Fact] // AC-A1-4
    public async Task ALookupAndALiveKnowledgeBaseOfOneToolCannotShareAName()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var (knowledgeBase, _) = await LookupToolApi.RegisterWithLookupAsync(setup, "policies");
        var manual = await LookupApi.CreateManualOkAsync(setup.Admin, LookupApi.ManualBody(name: "policies"));

        using var clash = await LookupToolApi.CreateAsync(setup.Admin, LookupToolApi.Body(lookups: [LookupToolApi.Bind(manual)], knowledgeBaseIds: [knowledgeBase]));
        var byAlias = await LookupToolApi.CreateOkAsync(setup.Admin, LookupToolApi.Body(lookups: [LookupToolApi.Bind(manual, "policy_list")], knowledgeBaseIds: [knowledgeBase]));

        Assert.Equal("lookup_tool.target_name_taken", await LookupToolApi.ProblemCodeAsync(clash));
        Assert.Equal(["policy_list"], byAlias.Definition!.Parameter("lookup").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact] // AC-A1-5
    public async Task RenamingALookupToTheNameOfAToolsLiveKnowledgeBaseIsRefused()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var (knowledgeBase, _) = await LookupToolApi.RegisterWithLookupAsync(setup, "policies");
        var manual = await LookupApi.CreateManualOkAsync(setup.Admin, LookupApi.ManualBody(name: "lists"));
        await LookupToolApi.CreateOkAsync(setup.Admin, LookupToolApi.Body(lookups: [LookupToolApi.Bind(manual)], knowledgeBaseIds: [knowledgeBase]));

        using var rename = await setup.Admin.PutAsJsonAsync(new Uri($"/api/knowledge-bases/lookups/{manual}", UriKind.Relative), new { name = "policies" }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, rename.StatusCode);
        Assert.Equal("lookup_tool.target_name_taken", await LookupToolApi.ProblemCodeAsync(rename));
    }

    [Fact] // AC-A1-11
    public async Task RemovingTheEntityNameOfAKnowledgeBaseTheToolReadsIsRefused()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var knowledgeBase = await RegisterEntityKnowledgeBaseAsync(setup, "vendors");
        var entity = await EntityLookupOfAsync(setup, knowledgeBase);
        await LookupToolApi.CreateOkAsync(setup.Admin, LookupToolApi.Body(lookups: [LookupToolApi.Bind(entity, "vendor_list")]));

        var collection = await qdrant.SeedSharedAsync(SampleCollections.CreateFlat);
        using var edit = await setup.Admin.PutAsJsonAsync(
            new Uri($"/api/knowledge-bases/{knowledgeBase}", UriKind.Relative),
            KnowledgeBaseApi.Body(setup.Url, collection, name: "vendors", description: "Vendors.", mapping: new { roles = new { text = "text" }, fields = Array.Empty<object>() }),
            Ct);

        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        Assert.Equal("knowledge_base.role_in_use", await LookupToolApi.ProblemCodeAsync(edit));
    }

    /// <summary>Registers a knowledge base with an entity lookup, computed in the background: the list stays as it is until a test publishes one.</summary>
    private static async Task<Guid> RegisterEntityKnowledgeBaseAsync(RagApi.Setup setup, string name)
    {
        setup.App.LookupComputer.Handler = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable: the delay only ends by cancellation.");
        };
        return await setup.RegisterAsync(name, mapping: EntityMapping);
    }

    private static async Task<Guid> EntityLookupOfAsync(RagApi.Setup setup, Guid knowledgeBase)
    {
        await using var services = setup.App.Services.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
        return (await db.Lookups.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.KnowledgeBaseId == knowledgeBase && l.Kind == LookupKind.Entity, Ct)).Id;
    }
}
