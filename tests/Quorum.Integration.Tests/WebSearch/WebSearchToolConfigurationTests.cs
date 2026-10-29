using System.Globalization;
using System.Net;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.Tools;

namespace Quorum.Integration.Tests.WebSearch;

/// <summary>What an administrator can save as a web search tool, and the definition it gives the model (spec 005, AC-1, AC-7, AC-10).</summary>
public sealed class WebSearchToolConfigurationTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-1
    public async Task AdminCanCreateAWebSearchToolWithItsParameters()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        await WebSearchApi.ConfigureAsync(admin);

        var created = await WebSearchApi.CreateOkAsync(
            admin,
            WebSearchApi.Body(
                name: "research",
                description: "Searches the open web.",
                topN: 10,
                searchEngines: ["google", "bing"],
                language: "it",
                safeSearch: 2,
                timeRange: "week",
                maxInvocationsPerConversation: 3));

        var tool = await WebSearchApi.GetAsync(admin, created.Id);
        Assert.Equal("research", tool.Name);
        Assert.Equal("Searches the open web.", tool.Description);
        Assert.Equal(10, tool.TopN);

        // Engines are kept as the instance knows them: lower case, once each, in a stable order, so the same list never looks like a change.
        Assert.Equal(["bing", "google"], tool.SearchEngines);
        Assert.Equal("it", tool.Language);
        Assert.Equal(2, tool.SafeSearch);
        Assert.Equal("week", tool.TimeRange);
        Assert.Equal(3, tool.MaxInvocationsPerConversation);
        Assert.Equal("Tenant", tool.Scope.Kind);
        Assert.Equal("Enabled", tool.State);
        Assert.Equal("Auto", tool.Policy);
        Assert.False(tool.Shared);
        Assert.Contains(await WebSearchApi.ListAsync(admin), t => t.Id == created.Id);
    }

    [Fact] // AC-1
    public async Task AWebSearchToolWithoutParametersKeepsTheDefaults()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        await WebSearchApi.ConfigureAsync(admin);

        var created = await WebSearchApi.CreateOkAsync(admin, WebSearchApi.Body(name: "web"));

        var tool = await WebSearchApi.GetAsync(admin, created.Id);
        Assert.Equal(5, tool.TopN);
        Assert.Equal(1, tool.SafeSearch);
        Assert.Equal(5, tool.MaxInvocationsPerConversation);
        Assert.Null(tool.SearchEngines);
        Assert.Null(tool.Language);
        Assert.Null(tool.TimeRange);
    }

    [Fact] // AC-7
    public async Task CreatingAWebSearchToolNeedsThePlatformConfiguration()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        using var response = await WebSearchApi.CreateAsync(admin, WebSearchApi.Body());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("web_search.not_configured", await WebSearchApi.ProblemCodeAsync(response));
        Assert.Contains("Web search configuration missing", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact] // AC-1
    public async Task TheModelIsOfferedOneFunctionThatTakesAQuery()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        await WebSearchApi.ConfigureAsync(admin);

        var created = await WebSearchApi.CreateOkAsync(admin, WebSearchApi.Body(name: "research"));

        var definition = created.Definition!;
        Assert.Equal("web_search__research", definition.Name);
        Assert.Equal(["query"], definition.Parameters);
        Assert.Equal("string", definition.Parameter("query").GetProperty("type").GetString());
        Assert.True(definition.EstimatedTokens > 0);

        var tool = await WebSearchApi.GetAsync(admin, created.Id);
        Assert.Equal("web_search__research", tool.Definition!.Name);
    }

    [Fact] // AC-10
    public async Task TwoWebSearchToolsCannotShareAName()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        await WebSearchApi.ConfigureAsync(admin);
        var first = await WebSearchApi.CreateOkAsync(admin, WebSearchApi.Body(name: "research"));

        using var again = await WebSearchApi.CreateAsync(admin, WebSearchApi.Body(name: "research", description: "Another one."));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("web_search.name_taken", await WebSearchApi.ProblemCodeAsync(again));

        using var renamed = await WebSearchApi.UpdateAsync(admin, first.Id, WebSearchApi.Body(name: "research", description: "Changed."));
        Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
    }

    [Theory] // AC-1
    [InlineData("topN", "0", "web_search.invalid_top_n")]
    [InlineData("topN", "21", "web_search.invalid_top_n")]
    [InlineData("safeSearch", "3", "web_search.invalid_safe_search")]
    [InlineData("maxInvocationsPerConversation", "0", "web_search.invalid_max_invocations")]
    [InlineData("timeRange", "fortnight", "web_search.invalid_time_range")]
    [InlineData("language", "italiano", "web_search.invalid_language")]
    [InlineData("searchEngines", "Google Search", "web_search.invalid_search_engine")]
    public async Task AParameterTheInstanceCannotAnswerWithIsRefused(string field, string value, string code)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        await WebSearchApi.ConfigureAsync(admin);

        var body = field switch
        {
            "topN" => WebSearchApi.Body(topN: int.Parse(value, CultureInfo.InvariantCulture)),
            "safeSearch" => WebSearchApi.Body(safeSearch: int.Parse(value, CultureInfo.InvariantCulture)),
            "maxInvocationsPerConversation" => WebSearchApi.Body(maxInvocationsPerConversation: int.Parse(value, CultureInfo.InvariantCulture)),
            "timeRange" => WebSearchApi.Body(timeRange: value),
            "language" => WebSearchApi.Body(language: value),
            _ => WebSearchApi.Body(searchEngines: [value]),
        };

        using var response = await WebSearchApi.CreateAsync(admin, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, await WebSearchApi.ProblemCodeAsync(response));
    }

    [Fact] // AC-10
    public async Task AdminCanUpdateTheParametersOfAWebSearchTool()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        await WebSearchApi.ConfigureAsync(admin);
        var created = await WebSearchApi.CreateOkAsync(admin, WebSearchApi.Body(name: "research", topN: 5));

        using var response = await WebSearchApi.UpdateAsync(admin, created.Id, WebSearchApi.Body(name: "research", topN: 12, language: "en", timeRange: "month"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var tool = await WebSearchApi.GetAsync(admin, created.Id);
        Assert.Equal(12, tool.TopN);
        Assert.Equal("en", tool.Language);
        Assert.Equal("month", tool.TimeRange);
    }

    [Fact] // AC-10
    public async Task AdminCanDeleteAWebSearchToolAndItLeavesTheLists()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        await WebSearchApi.ConfigureAsync(admin);
        var created = await WebSearchApi.CreateOkAsync(admin, WebSearchApi.Body(name: "research"));

        using var response = await admin.DeleteAsync(WebSearchApi.WebSearchTool(created.Id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.DoesNotContain(await WebSearchApi.ListAsync(admin), t => t.Id == created.Id);
        using var gone = await admin.GetAsync(WebSearchApi.WebSearchTool(created.Id), Ct);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.DoesNotContain(await ToolsApi.ListToolsAsync(admin), t => t.Id == created.Id);
    }

    [Fact] // AC-10
    public async Task AWebSearchToolIsListedWithTheToolsAnAdministratorManages()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        await WebSearchApi.ConfigureAsync(admin);
        var created = await WebSearchApi.CreateOkAsync(admin, WebSearchApi.Body(name: "research"));

        var listed = Assert.Single(await ToolsApi.ListToolsAsync(admin), t => t.Id == created.Id);

        Assert.Equal("WebSearch", listed.Origin);
        Assert.Equal("research", listed.Name);
        Assert.Equal("Enabled", listed.State);
    }

    [Fact] // AC-10
    public async Task OnlyAnAdministratorCanManageWebSearchTools()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        await WebSearchApi.ConfigureAsync(admin);
        var created = await WebSearchApi.CreateOkAsync(admin, WebSearchApi.Body(name: "research"));
        using var member = await app.CreateMemberClientAsync();

        using var list = await member.GetAsync(WebSearchApi.WebSearchTools, Ct);
        using var create = await WebSearchApi.CreateAsync(member, WebSearchApi.Body(name: "other"));
        using var read = await member.GetAsync(WebSearchApi.WebSearchTool(created.Id), Ct);
        using var anonymous = await app.CreateUiClient().GetAsync(WebSearchApi.WebSearchTools, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }
}
