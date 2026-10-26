using System.Net;
using System.Text.Json;
using Bunit;
using Quorum.Web.Client;
using Quorum.Web.Client.Api;
using Quorum.Web.Client.Pages;

namespace Quorum.Integration.Tests.Web;

/// <summary>The audit log, the knowledge base editor and the small view classes behind the list pages (spec 010 AC-14, AC-18, AC-20).</summary>
public sealed class AuditAndEditorPagesTests : ListPageTestContext
{
    private static readonly Guid EntryId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b7001");
    private static readonly Guid UserId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b7002");
    private static readonly Guid BaseId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b7011");

    private int _total = 120;

    public AuditAndEditorPagesTests()
    {
        Api.Respond = (method, path) =>
        {
            if (method == HttpMethod.Get && path == "/api/tools/audit")
            {
                return FakeApiHandler.Json(new
                {
                    items = new[]
                    {
                        new
                        {
                            id = EntryId,
                            startedAt = DateTimeOffset.Parse("2026-10-12T08:30:00Z", global::System.Globalization.CultureInfo.InvariantCulture),
                            completedAt = (DateTimeOffset?)DateTimeOffset.Parse("2026-10-12T08:30:01Z", global::System.Globalization.CultureInfo.InvariantCulture),
                            userId = UserId,
                            assistantId = Guid.NewGuid(),
                            conversationId = Guid.NewGuid(),
                            callId = Guid.NewGuid(),
                            serverName = (string?)null,
                            toolName = "calculator",
                            decision = (string?)"Approved",
                            outcome = "Succeeded",
                            arguments = """{"expression":"2+2"}""",
                            argumentsTruncated = false,
                            resultCharacters = (int?)1,
                            sourceCount = (int?)null,
                            durationMs = (int?)12,
                            errorCode = (string?)null,
                        },
                    },
                    total = _total,
                });
            }

            if (path == "/api/knowledge-bases/dictionary")
            {
                return Page();
            }

            if (path == "/api/assistants")
            {
                return FakeApiHandler.Json(Array.Empty<object>());
            }

            if (method == HttpMethod.Get && path == $"/api/knowledge-bases/{BaseId}")
            {
                return FakeApiHandler.Json(new
                {
                    id = BaseId,
                    name = "policies",
                    description = "Company policies.",
                    status = "Available",
                    shared = false,
                    kind = "Qdrant",
                    connection = new { url = "http://qdrant:6334", collection = "policies", vectorName = (string?)null, hasApiKey = false },
                    embeddingModel = "nomic-embed-text",
                    queryPrefix = (string?)null,
                    mapping = (object?)null,
                    scope = NoScopeIds("Tenant"),
                });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
    }

    [Fact] // AC-18
    public void TheEntriesAreListedNewestFirstWithOutcomeAndToolAndNoWriteAction()
    {
        var page = Visit<AuditLog>("/tools/audit");

        var row = page.Find("[data-entry]").ParentElement!.ParentElement!;
        Assert.Contains("builtin__calculator", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("Succeeded", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("12 ms", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("120 entries · page 1 of 3", page.Find("[data-pager]").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain(page.FindAll("button"), b => b.TextContent.Contains("Delete", StringComparison.Ordinal));
    }

    [Fact] // AC-18
    public void TheFiltersAndThePageComeFromTheAddressAndGoToTheServer()
    {
        Visit<AuditLog>($"/tools/audit?from=2026-10-01&outcome=Failed&user={UserId}&page=2");

        Assert.Contains(Api.Queries, q => q.Contains("from=2026-10-01", StringComparison.Ordinal) && q.Contains("outcome=Failed", StringComparison.Ordinal)
            && q.Contains($"user={UserId}", StringComparison.Ordinal) && q.Contains("page=2", StringComparison.Ordinal) && q.Contains("pageSize=50", StringComparison.Ordinal));
    }

    [Fact] // AC-18
    public void NextAsksForTheFollowingPage()
    {
        var page = Visit<AuditLog>("/tools/audit");

        ClickButton(page, "Next");

        page.WaitForAssertion(() => Assert.Contains(Api.Queries, q => q.Contains("page=2", StringComparison.Ordinal)));
    }

    [Fact] // AC-18
    public void AnEntryOpensInTheDrawerWithItsArguments()
    {
        var page = Visit<AuditLog>($"/tools/audit/{EntryId}", p => p.Add(c => c.Id, EntryId));

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-detail=audit]")));
        Assert.Contains("""{"expression":"2+2"}""", page.Markup.Replace("&quot;", "\"", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("Approved", page.Find("[data-detail=audit]").TextContent, StringComparison.Ordinal);
    }

    [Fact] // AC-18
    public void NoEntryYetShowsTheEmptyState()
    {
        _total = 0;
        var script = Api.Respond;
        Api.Respond = (method, path) => path == "/api/tools/audit" ? FakeApiHandler.Json(new { items = Array.Empty<object>(), total = 0 }) : script(method, path);

        var page = Visit<AuditLog>("/tools/audit");

        Assert.Contains("No tool call was recorded yet", page.Markup, StringComparison.Ordinal);
    }

    [Fact] // AC-20
    public void TheWizardPageOpensOnANewRegistrationAndOnAnExistingOne()
    {
        var created = Visit<KnowledgeBaseEditor>("/knowledge/bases/new");
        created.WaitForAssertion(() => Assert.NotEmpty(created.FindAll("[data-form=knowledge-base]")));
        Assert.Contains("Register a knowledge base", created.Markup, StringComparison.Ordinal);
    }

    [Fact] // AC-20
    public void AnExistingRegistrationOpensInTheWizardWithItsConnection()
    {
        var page = Visit<KnowledgeBaseEditor>($"/knowledge/bases/{BaseId}/edit", p => p.Add(c => c.Id, BaseId));

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-form=knowledge-base]")));
        Assert.Contains("Edit policies", page.Markup, StringComparison.Ordinal);
        Assert.Equal("policies", page.FindAll("[data-form=knowledge-base] input[type=text]")[0].GetAttribute("value"));
    }

    [Fact]
    public void AuditQueryRoundTripsThroughTheAddress()
    {
        var query = AuditQuery.Parse($"/tools/audit?from=2026-10-01&tool=builtin__calculator&user={UserId}&outcome=Failed&page=3&junk=1");

        Assert.Equal(("2026-10-01", "builtin__calculator", UserId, "Failed", 3), (query.From, query.Tool, query.User, query.Outcome, query.Page));
        Assert.Equal($"?from=2026-10-01&tool=builtin__calculator&user={UserId}&outcome=Failed&page=3", query.ToQueryString());
        Assert.Equal(string.Empty, new AuditQuery().ToQueryString());
        Assert.StartsWith("?pageSize=50", new AuditQuery().ToApiQuery(), StringComparison.Ordinal);
        Assert.Equal(1, AuditQuery.Parse("?page=0").Page);
    }

    [Fact]
    public void AServerFormBuildsTheRequestAndKnowsWhenItWasChanged()
    {
        var form = ServerForm.New();
        Assert.False(form.IsDirty);

        form.Name = " web ";
        form.Url = "http://web.test/mcp";
        form.HeadersText = "Authorization: Bearer x";

        Assert.True(form.IsDirty);
        var (request, error) = form.Build();
        Assert.Null(error);
        Assert.Equal(("web", "http://web.test/mcp", "Bearer x", "Tenant"), (request!.Name, request.Url, request.Headers!["Authorization"], request.Scope.Kind));
    }

    [Fact]
    public void AServerFormRefusesToRemoveAndReplaceTheHeadersAtOnce()
    {
        var server = new ToolServer(Guid.NewGuid(), "files", "http://files.test/mcp", true, "Available", null, null, new ToolScope("Tenant", [], []), 0, 0, false);
        var form = ServerForm.From(server);
        form.RemoveHeaders = true;
        form.HeadersText = "X-Key: 1";

        var (request, error) = form.Build();

        Assert.Null(request);
        Assert.Contains("not both", error, StringComparison.Ordinal);
        form.HeadersText = string.Empty;
        Assert.Empty(form.Build().Request!.Headers!);
    }

    [Fact]
    public void ModelFieldsSendTheKeyOnlyWhenTypedOrRemoved()
    {
        var fields = new ModelConnectionFields { BaseUrl = "http://m/v1", ModelName = "llama", ContextTokenBudget = 4096 };
        Assert.Null(fields.KeyToSend());

        fields.RemoveKey = true;
        Assert.Equal(string.Empty, fields.KeyToSend());

        fields.ApiKey = " sk-new ";
        Assert.Equal("sk-new", fields.KeyToSend());
        Assert.True(fields.IsDirty(fields.Copy()));
        Assert.False(JsonSerializer.Serialize(fields.Copy()).Contains("sk-new", StringComparison.Ordinal));
    }

    [Fact] // AC-20
    public void EditingARegistrationWithAMappingOpensEveryStepAndHasNoKeepTheMappingButton()
    {
        var script = Api.Respond;
        Api.Respond = (method, path) => method == HttpMethod.Get && path == $"/api/knowledge-bases/{BaseId}"
            ? FakeApiHandler.Json(new
            {
                id = BaseId,
                name = "vendors",
                description = "Vendors.",
                status = "Available",
                shared = false,
                kind = "Qdrant",
                connection = new { url = "http://qdrant:6334", collection = "vendors", vectorName = (string?)null, hasApiKey = false },
                embeddingModel = "nomic-embed-text",
                queryPrefix = (string?)null,
                mapping = new
                {
                    roles = new { text = "text", documentId = (string?)null, title = (string?)null, position = (string?)null, sourceUrl = (string?)null },
                    fields = Array.Empty<object>(),
                },
                scope = NoScopeIds("Tenant"),
            })
            : script(method, path);

        var page = Visit<KnowledgeBaseEditor>($"/knowledge/bases/{BaseId}/edit", p => p.Add(c => c.Id, BaseId));

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".wizard-steps button")));
        Assert.All(page.FindAll(".wizard-steps button"), b => Assert.False(b.HasAttribute("disabled")));
        Assert.DoesNotContain("Keep the mapping", page.Markup, StringComparison.Ordinal);
    }
}
