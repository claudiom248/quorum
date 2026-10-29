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
        Assert.Contains("\"operations\":[\"values\",\"count\"]", describe, StringComparison.Ordinal);

        var count = ResultOf(await AskAsync(setup, new FakeToolCall("lookup_query__lists", "{\"target\":\"department\",\"operation\":\"count\"}", "call_2")));
        Assert.Contains("department: 3 values match.", count, StringComparison.Ordinal);
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
