using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.KnowledgeBases.Lookups;
using Quorum.Integration.Tests.Rag;
using Quorum.Integration.Tests.Tools;
using static Quorum.Integration.Tests.Fixtures.FakeModelServer;

namespace Quorum.Integration.Tests.LookupTools;

/// <summary>The lean manifest of a lookup tool: describe and query in place of the full functions (spec 012 D-8, D-9).</summary>
public sealed class LeanManifestTests(PostgresFixture postgres, QdrantFixture qdrant)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-14, AC-15, AC-16, AC-22
    public async Task ALeanActivationOffersDescribeAndQueryAndACountOfAListCountsItsMatchingValues()
    {
        var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        await using var _ = setup;
        var (_, lookup) = await LookupToolApi.RegisterWithLookupAsync(setup, "policies");
        await LookupToolApi.PublishAsync(setup.App, lookup, [("legal", 4), ("hr", 9), ("finance", 4)]);
        await LookupToolApi.CreateOkAsync(setup.Admin, LookupToolApi.Body(lookups: [LookupToolApi.Bind(lookup, "department")]));
        var assistant = await ToolsApi.DefaultAssistantIdAsync(setup.Admin);
        var toolId = (await ToolsApi.GetAssistantToolsAsync(setup.Admin, assistant)).Pool.Single(t => t.Name == "lookup__lists").ToolId;

        using var activate = await setup.Admin.PutAsJsonAsync(
            ToolsApi.AssistantTools(assistant),
            new { toolIds = new[] { toolId }, manifests = new Dictionary<string, string> { [toolId.ToString()] = "lean" } },
            Ct);
        Assert.Equal(HttpStatusCode.NoContent, activate.StatusCode);

        var describe = ResultOf(await AskAsync(setup, new FakeToolCall("lookup_describe__lists", "{\"targets\":[\"department\"]}", "call_1")));
        Assert.Contains("\"operations\":{\"values\":{", describe, StringComparison.Ordinal);
        Assert.Contains("\"count\":{", describe, StringComparison.Ordinal);

        var count = ResultOf(await AskAsync(setup, new FakeToolCall("lookup_query__lists", "{\"target\":\"department\",\"operation\":\"count\"}", "call_2")));
        Assert.Contains("department: 3 values match.", count, StringComparison.Ordinal);
    }

    [Fact] // AC-22
    public async Task ACountOfAListWithFiltersCountsTheValuesThatMatchThem()
    {
        var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        await using var _ = setup;
        var (_, lookup) = await LookupToolApi.RegisterWithLookupAsync(setup, "policies");
        await LookupToolApi.PublishAsync(setup.App, lookup, [("legal", 4), ("hr", 9), ("finance", 4)]);
        var fields = await setup.FieldsAsync();
        await LookupToolApi.CreateOkAsync(setup.Admin, LookupToolApi.Body(lookups: [LookupToolApi.Bind(lookup, "department")], exposedFields: [new { fieldId = fields["department"] }]));
        var assistant = await ToolsApi.DefaultAssistantIdAsync(setup.Admin);
        var toolId = (await ToolsApi.GetAssistantToolsAsync(setup.Admin, assistant)).Pool.Single(t => t.Name == "lookup__lists").ToolId;
        using var activate = await setup.Admin.PutAsJsonAsync(
            ToolsApi.AssistantTools(assistant),
            new { toolIds = new[] { toolId }, manifests = new Dictionary<string, string> { [toolId.ToString()] = "lean" } },
            Ct);
        Assert.Equal(HttpStatusCode.NoContent, activate.StatusCode);

        var count = ResultOf(await AskAsync(setup, new FakeToolCall("lookup_query__lists", "{\"target\":\"department\",\"operation\":\"count\",\"filters\":{\"department\":[\"hr\"]}}", "call_1")));

        Assert.Contains("department: 1 values match.", count, StringComparison.Ordinal);
    }

    [Fact] // AC-A1-8
    public async Task EveryFilterThatDescribeListsIsAcceptedByQueryAndAnotherIsRefused()
    {
        var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        await using var _ = setup;
        var (knowledgeBase, lookup) = await LookupToolApi.RegisterWithLookupAsync(setup, "policies");
        await LookupToolApi.PublishAsync(setup.App, lookup, [("legal", 4), ("hr", 9)]);
        var fields = await setup.FieldsAsync();
        await LookupToolApi.CreateOkAsync(setup.Admin, LookupToolApi.Body(
            lookups: [LookupToolApi.Bind(lookup, "department")],
            knowledgeBaseIds: [knowledgeBase],
            exposedFields: [new { fieldId = fields["department"] }, new { fieldId = fields["year"] }, new { fieldId = fields["confidential"] }]));
        var assistant = await ToolsApi.DefaultAssistantIdAsync(setup.Admin);
        var toolId = (await ToolsApi.GetAssistantToolsAsync(setup.Admin, assistant)).Pool.Single(t => t.Name == "lookup__lists").ToolId;
        using var activate = await setup.Admin.PutAsJsonAsync(
            ToolsApi.AssistantTools(assistant),
            new { toolIds = new[] { toolId }, manifests = new Dictionary<string, string> { [toolId.ToString()] = "lean" } },
            Ct);
        Assert.Equal(HttpStatusCode.NoContent, activate.StatusCode);

        var describe = ResultOf(await AskAsync(setup, new FakeToolCall("lookup_describe__lists", "{\"targets\":[\"department\",\"policies\"]}", "call_1")));
        var targets = JsonDocument.Parse(describe).RootElement.GetProperty("targets").EnumerateArray().ToList();

        // The query the model is offered says which operations a list takes and which a knowledge base takes (spec 012 D-8, A1-6).
        var offered = setup.App.ModelServer.ChatRequests[0].GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("function")).Single(f => f.GetProperty("name").GetString() == "lookup_query__lists");
        Assert.Contains("A list takes the operations values and count only; documents is for a knowledge base only", offered.GetProperty("description").GetString(), StringComparison.Ordinal);

        // The schema states the operations per target: a list takes values and count, a knowledge base documents and count.
        var rules = offered.GetProperty("parameters").GetProperty("allOf").EnumerateArray().Select(r => r.GetProperty("then").GetProperty("properties").GetProperty("operation").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToList()).ToList();
        Assert.Equal([["values", "count"], ["documents", "count"]], rules);

        // A number states its type and its operators, min and max among them (spec 012 D-5, D-8).
        var years = targets.SelectMany(t => t.GetProperty("operations").EnumerateObject()).SelectMany(o => o.Value.GetProperty("filters").EnumerateArray())
            .Where(f => f.GetProperty("name").GetString() == "year").ToList();
        Assert.NotEmpty(years);
        Assert.All(years, year =>
        {
            Assert.Equal("Number", year.GetProperty("type").GetString());
            Assert.Equal(["eq", "in", "min", "max"], year.GetProperty("operators").EnumerateArray().Select(e => e.GetString()));
            // AC-A1-9: no list of the tool holds the values of year, so there is no pointer and the filter says so in words.
            Assert.False(year.TryGetProperty("valuesIn", out var pointer), pointer.ToString());
            Assert.Contains("Not listed by this tool", year.GetProperty("values").GetString(), StringComparison.Ordinal);
        });

        // Each filter describe lists for an operation is accepted by query for that operation: the answer is never "cannot be filtered".
        var call = 1;
        var listed = 0;
        foreach (var target in targets)
        {
            var name = target.GetProperty("name").GetString()!;
            foreach (var operation in target.GetProperty("operations").EnumerateObject())
            {
                foreach (var filter in operation.Value.GetProperty("filters").EnumerateArray())
                {
                    var parameter = filter.GetProperty("name").GetString()!;
                    var arguments = JsonSerializer.Serialize(new { target = name, operation = operation.Name, filters = new Dictionary<string, string[]> { [parameter] = ["x"] } });
                    var answer = ResultOf(await AskAsync(setup, new FakeToolCall("lookup_query__lists", arguments, $"call_{++call}")));
                    Assert.DoesNotContain("cannot be filtered", answer, StringComparison.Ordinal);
                    listed++;
                }
            }
        }

        Assert.Equal(12, listed);
        var refused = ResultOf(await AskAsync(setup, new FakeToolCall("lookup_query__lists", "{\"target\":\"department\",\"operation\":\"values\",\"filters\":{\"nothing\":[\"x\"]}}", $"call_{++call}")));
        Assert.Contains("cannot be filtered by nothing", refused, StringComparison.Ordinal);

        // A range takes min or max; any other bound under a field is refused by name.
        var bound = ResultOf(await AskAsync(setup, new FakeToolCall("lookup_query__lists", "{\"target\":\"policies\",\"operation\":\"documents\",\"filters\":{\"year\":{\"between\":[1,2]}}}", $"call_{++call}")));
        Assert.Contains("'between' is not one of them", bound, StringComparison.Ordinal);

        // The sort values are written out in describe; a knowledge base takes no search or sort.
        var departmentSort = targets.Single(t => t.GetProperty("name").GetString() == "department").GetProperty("operations").GetProperty("values").GetProperty("sort").EnumerateArray().Select(e => e.GetString());
        Assert.Equal(["name", "count"], departmentSort);
        var withSearch = await AskAsync(setup, new FakeToolCall("lookup_query__lists", "{\"target\":\"policies\",\"operation\":\"documents\",\"search\":\"x\"}", $"call_{++call}"));
        Assert.Contains("takes no search or sort", ResultOf(withSearch), StringComparison.Ordinal);
    }

    [Fact] // AC-19
    public async Task AManifestOnAToolThatIsNotALookupToolIsRefused()
    {
        var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        await using var _ = setup;
        var assistant = await ToolsApi.DefaultAssistantIdAsync(setup.Admin);
        var calculator = (await ToolsApi.GetAssistantToolsAsync(setup.Admin, assistant)).Pool.First(t => !t.Name.StartsWith("lookup__", StringComparison.Ordinal));

        using var response = await setup.Admin.PutAsJsonAsync(
            ToolsApi.AssistantTools(assistant),
            new { toolIds = new[] { calculator.ToolId }, manifests = new Dictionary<string, string> { [calculator.ToolId.ToString()] = "lean" } },
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("tool.manifest_not_supported", (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString());
    }

    private static string ResultOf(IEnumerable<SseEvent> events, int index = 0) =>
        events.Where(e => e.Type == "tool_result").ElementAt(index).Data.GetProperty("content").GetString()!;

    private static async Task<IReadOnlyList<SseEvent>> AskAsync(RagApi.Setup setup, params FakeToolCall[] calls)
    {
        setup.App.ModelServer.EnqueueRounds([.. calls.Select(c => ChatScript.CallTools(c)), ChatScript.Reply("Done.")]);
        var id = await ChatApi.CreateConversationAsync(setup.Admin);
        return await ChatApi.SendAsync(setup.Admin, id, "Question");
    }
}
