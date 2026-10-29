using System.Net;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.WebSearch;

namespace Quorum.Integration.Tests.WebFetch;

/// <summary>The web fetch tools of a tenant: an administrator creates, lists, edits and removes them (spec 006, AC-1, AC-15).</summary>
public sealed class WebFetchToolConfigurationTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-1
    public async Task AdminCreatesAWebFetchToolWithItsDefaults()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        var created = await WebFetchApi.CreateOkAsync(admin, WebFetchApi.Body(name: "read"));

        Assert.NotEqual(Guid.Empty, created.Id);
        var tool = await WebFetchApi.GetAsync(admin, created.Id);
        Assert.Equal("read", tool.Name);

        // A tool that was not given a number of its own keeps none: what it will use is what the platform's row says (AC-13).
        Assert.Null(tool.TokenBudgetPerPage);
        Assert.Null(tool.TotalTokenBudget);
        Assert.Null(tool.MaxUrlsPerInvocation);
        Assert.Null(tool.Timeout);
        Assert.Equal("Tenant", tool.Scope.Kind);
        Assert.Equal("Enabled", tool.State);
        Assert.Equal("Auto", tool.Policy);
        Assert.False(tool.Shared);

        // The model is still told a number: the platform's, not an empty slot.
        Assert.Equal(5, created.Definition!.Parameter("urls").GetProperty("maxItems").GetInt32());
    }

    [Fact] // AC-13
    public async Task TheDefinitionOfAnInheritingToolSaysWhatThePlatformSaysNow()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var created = await WebFetchApi.CreateOkAsync(admin, WebFetchApi.Body(name: "read"));

        using var changed = await WebFetchApi.UpdateConfigurationAsync(
            admin,
            WebSearchApi.DefaultConfiguration with { FetchDelayMs = 0, FetchMaxUrlsPerInvocation = 2 });
        changed.EnsureSuccessStatusCode();

        var tool = await WebFetchApi.GetAsync(admin, created.Id);
        Assert.Null(tool.MaxUrlsPerInvocation);
        Assert.Equal(2, tool.Definition!.Parameter("urls").GetProperty("maxItems").GetInt32());
    }

    [Fact] // AC-1
    public async Task AdminCanOverrideTheGlobalDefaultsOnTheTool()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        var created = await WebFetchApi.CreateOkAsync(admin, WebFetchApi.Body(
            name: "read",
            tokenBudgetPerPage: 1000,
            totalTokenBudget: 6000,
            maxUrlsPerInvocation: 3,
            timeout: 15));

        var tool = await WebFetchApi.GetAsync(admin, created.Id);
        Assert.Equal(1000, tool.TokenBudgetPerPage);
        Assert.Equal(6000, tool.TotalTokenBudget);
        Assert.Equal(3, tool.MaxUrlsPerInvocation);
        Assert.Equal(15, tool.Timeout);
    }

    [Fact] // AC-1
    public async Task AdminReadsTheGeneratedToolDefinition()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        var created = await WebFetchApi.CreateOkAsync(admin, WebFetchApi.Body(name: "read", maxUrlsPerInvocation: 4));

        Assert.NotNull(created.Definition);
        Assert.Equal("web_fetch__read", created.Definition.Name);
        Assert.Equal(["urls"], created.Definition.Parameters);

        var urls = created.Definition.Parameter("urls");
        Assert.Equal("array", urls.GetProperty("type").GetString());
        Assert.Equal(4, urls.GetProperty("maxItems").GetInt32());
    }

    [Fact] // AC-1
    public async Task TwoToolsCannotShareAName()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        await WebFetchApi.CreateOkAsync(admin, WebFetchApi.Body(name: "read"));

        using var second = await WebFetchApi.CreateAsync(admin, WebFetchApi.Body(name: "read"));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("web_fetch.name_taken", await WebFetchApi.ProblemCodeAsync(second));
    }

    [Fact] // AC-1
    public async Task AdminUpdatesTheToolAndItsBinding()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var created = await WebFetchApi.CreateOkAsync(admin, WebFetchApi.Body(name: "read"));

        using var update = await WebFetchApi.UpdateAsync(admin, created.Id, WebFetchApi.Body(
            name: "reader",
            description: "Fetches pages the search just found.",
            tokenBudgetPerPage: 5000,
            totalTokenBudget: 25000,
            maxUrlsPerInvocation: 7,
            timeout: 45,
            policy: "Confirm"));

        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        var tool = await WebFetchApi.GetAsync(admin, created.Id);
        Assert.Equal("reader", tool.Name);
        Assert.Equal("Fetches pages the search just found.", tool.Description);
        Assert.Equal(5000, tool.TokenBudgetPerPage);
        Assert.Equal(25000, tool.TotalTokenBudget);
        Assert.Equal(7, tool.MaxUrlsPerInvocation);
        Assert.Equal(45, tool.Timeout);
        Assert.Equal("Confirm", tool.Policy);
    }

    [Fact] // AC-1
    public async Task AdminListsTheToolsOfTheTenant()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        await WebFetchApi.CreateOkAsync(admin, WebFetchApi.Body(name: "read"));
        await WebFetchApi.CreateOkAsync(admin, WebFetchApi.Body(name: "fetch-docs"));

        var list = await WebFetchApi.ListAsync(admin);

        Assert.Equal(2, list.Count);
        Assert.Contains(list, t => t.Name == "read");
        Assert.Contains(list, t => t.Name == "fetch-docs");
    }

    [Fact] // AC-1
    public async Task AdminDeletesAToolAndItsBinding()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var created = await WebFetchApi.CreateOkAsync(admin, WebFetchApi.Body(name: "read"));

        using var delete = await admin.DeleteAsync(WebFetchApi.WebFetchTool(created.Id), Ct);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        using var gone = await admin.GetAsync(WebFetchApi.WebFetchTool(created.Id), Ct);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact] // AC-1
    public async Task AnInvalidNameIsRefused()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        using var response = await WebFetchApi.CreateAsync(admin, WebFetchApi.Body(name: "Read Pages"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("web_fetch.invalid_name", await WebFetchApi.ProblemCodeAsync(response));
    }

    [Fact] // AC-1
    public async Task AnInvalidTokenBudgetPerPageIsRefused()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        using var response = await WebFetchApi.CreateAsync(admin, WebFetchApi.Body(tokenBudgetPerPage: 100));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("web_fetch.invalid_token_budget_per_page", await WebFetchApi.ProblemCodeAsync(response));
    }

    [Fact] // AC-1
    public async Task AnInvalidMaxUrlsPerInvocationIsRefused()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        using var response = await WebFetchApi.CreateAsync(admin, WebFetchApi.Body(maxUrlsPerInvocation: 20));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("web_fetch.invalid_max_urls", await WebFetchApi.ProblemCodeAsync(response));
    }

    [Fact] // AC-1
    public async Task AnInvalidTimeoutIsRefused()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        using var response = await WebFetchApi.CreateAsync(admin, WebFetchApi.Body(timeout: 2));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("web_fetch.invalid_timeout", await WebFetchApi.ProblemCodeAsync(response));
    }

    [Fact] // AC-1
    public async Task NonAdminCannotManageTheTools()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        using var member = await app.CreateMemberClientAsync();

        using var read = await member.GetAsync(WebFetchApi.WebFetchTools, Ct);
        using var write = await WebFetchApi.CreateAsync(member, WebFetchApi.Body());

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }
}
