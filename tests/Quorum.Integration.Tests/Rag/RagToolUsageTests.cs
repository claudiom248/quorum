using System.Net;
using System.Net.Http.Json;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.KnowledgeBases;
using Quorum.Integration.Tests.Tools;

namespace Quorum.Integration.Tests.Rag;

/// <summary>What rag tools and the dictionary and the assistants of a scope owe each other (spec 003, AC-4, AC-48 and the check of assistant ids).</summary>
public sealed class RagToolUsageTests(PostgresFixture postgres, QdrantFixture qdrant)
{
    private static readonly string[] Hr = ["hr"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-4
    public async Task AFieldAToolExposesIsCountedAsInUseAndCannotBeDeleted()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");
        var fields = await setup.FieldsAsync();
        var tool = await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [kb], exposedFieldIds: [fields["department"]]));

        var before = (await DictionaryApi.ListAsync(setup.Admin)).Single(f => f.Name == "department");
        using var deleteWhileUsed = await setup.Admin.DeleteAsync(DictionaryApi.Field(fields["department"]), Ct);
        using var removeTool = await setup.Admin.DeleteAsync(RagApi.One(tool.Id), Ct);
        var after = (await DictionaryApi.ListAsync(setup.Admin)).Single(f => f.Name == "department");

        // The knowledge base that maps it and the tool that exposes it.
        Assert.Equal(2, before.UsedByCount);
        Assert.Equal(HttpStatusCode.Conflict, deleteWhileUsed.StatusCode);
        Assert.Equal("knowledge_base.field_in_use", await DictionaryApi.ProblemCodeAsync(deleteWhileUsed));
        var detail = await deleteWhileUsed.Content.ReadAsStringAsync(Ct);
        Assert.Contains("rag tools company", detail, StringComparison.Ordinal);
        Assert.Contains("knowledge bases policies", detail, StringComparison.Ordinal);
        Assert.Equal(1, after.UsedByCount);
    }

    [Fact] // AC-4
    public async Task AFieldOnlyAFixedFilterNamesIsInUseToo()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");
        var fields = await setup.FieldsAsync();
        var tool = await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [kb], fixedFilters: new { department = Hr }));
        // Take the field out of the knowledge base's mapping, leaving the fixed filter as the only user.
        var current = await KnowledgeBaseApi.GetAsync(setup.Admin, kb);
        var withoutDepartment = new
        {
            roles = new { text = "text", documentId = "document_id", title = "title", position = "page", sourceUrl = "source_url" },
            fields = current.Mapping!.Fields.Where(f => f.Name != "department").Select(f => new { path = f.Path, name = f.Name }).ToArray(),
        };
        using var edit = await setup.Admin.PutAsJsonAsync(
            KnowledgeBaseApi.One(kb),
            KnowledgeBaseApi.Body(setup.Url, current.Connection!.Collection, "policies", mapping: withoutDepartment, createFields: []),
            Ct);
        edit.EnsureSuccessStatusCode();

        var field = (await DictionaryApi.ListAsync(setup.Admin)).Single(f => f.Name == "department");
        using var delete = await setup.Admin.DeleteAsync(DictionaryApi.Field(fields["department"]), Ct);

        Assert.Equal(1, field.UsedByCount);
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Contains("rag tools company", await delete.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.NotEqual(Guid.Empty, tool.Id);
    }

    [Fact] // AC-48
    public async Task DeletingAKnowledgeBaseLeavesTheToolsItWasInWithTheOthers()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kept = await setup.RegisterAsync("kept");
        var gone = await setup.RegisterAsync("gone");
        var tool = await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [kept, gone]));

        using var delete = await setup.Admin.DeleteAsync(KnowledgeBaseApi.One(gone), Ct);

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        var listed = Assert.Single(await RagApi.ListAsync(setup.Admin));
        Assert.Equal(["kept"], listed.KnowledgeBases.Select(k => k.Name));
        var detail = await RagApi.GetAsync(setup.Admin, tool.Id);
        Assert.DoesNotContain("knowledge_base", detail.Definition!.Parameters);
    }

    [Fact]
    public async Task AKnowledgeBaseScopedToAnAssistantThatDoesNotExistIsRejected()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);

        var collection = QdrantFixture.NewPrefix() + "sample_flat";
        await qdrant.SeedAsync([Quorum.SampleKnowledgeBase.SampleCollections.CreateFlat(collection)]);
        using var response = await KnowledgeBaseApi.RegisterAsync(
            setup.Admin,
            setup.Url,
            collection,
            scope: new { kind = "Assistant", assistantIds = new[] { Guid.NewGuid() } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("knowledge_base.invalid_scope", await KnowledgeBaseApi.ProblemCodeAsync(response));
        Assert.Empty(await KnowledgeBaseApi.ListAsync(setup.Admin));
    }

    [Fact]
    public async Task AKnowledgeBaseCanBeScopedToAnExistingAssistantAndEditedToAnotherThatDoesNotExistIsRejected()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies", scope: new { kind = "Assistant", assistantIds = new[] { setup.AssistantId } });
        var current = await KnowledgeBaseApi.GetAsync(setup.Admin, kb);

        using var edit = await setup.Admin.PutAsJsonAsync(
            KnowledgeBaseApi.One(kb),
            KnowledgeBaseApi.Body(setup.Url, current.Connection!.Collection, "policies", scope: new { kind = "Assistant", assistantIds = new[] { Guid.NewGuid() } }),
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, edit.StatusCode);
        Assert.Equal("knowledge_base.invalid_scope", await KnowledgeBaseApi.ProblemCodeAsync(edit));
        Assert.Equal([setup.AssistantId], (await KnowledgeBaseApi.GetAsync(setup.Admin, kb)).Scope!.AssistantIds);
    }
}
