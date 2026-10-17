using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;

namespace Quorum.Integration.Tests.Tools;

/// <summary>Reading the audit log through the API (spec 004, AC-46, D-12, D-13).</summary>
public sealed class ToolAuditApiTests(PostgresFixture postgres)
{
    private static readonly Uri Audit = new("/api/tools/audit", UriKind.Relative);

    private static readonly Guid UserA = Guid.Parse("01a14800-0000-7000-8000-00000000000a");
    private static readonly Guid UserB = Guid.Parse("01a14800-0000-7000-8000-00000000000b");

    private static readonly DateTimeOffset Noon = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Uri Query(params (string Name, object Value)[] parameters) =>
        new($"/api/tools/audit?{string.Join('&', parameters.Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value is DateTimeOffset at ? at.ToString("O") : p.Value.ToString()!)}"))}", UriKind.Relative);

    private static async Task<AuditPage> ReadAsync(HttpClient client, Uri uri)
    {
        using var response = await client.GetAsync(uri, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuditPage>(Ct))!;
    }

    [Fact]
    public async Task OnlyAnAdministratorCanReadTheLog()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        using var member = await app.CreateMemberClientAsync();
        using var anonymous = app.CreateUiClient();

        using var forbidden = await member.GetAsync(Audit, Ct);
        using var unauthorized = await anonymous.GetAsync(Audit, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
    }

    [Fact]
    public async Task EntriesComeNewestFirstWithTheirTotalAndTheFieldsOfTheLog()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await ToolFixtures.AddAuditEntryAsync(app, Noon.AddHours(-2), tool: "old");
        await ToolFixtures.AddAuditEntryAsync(app, Noon, tool: "new", server: "files", outcome: ToolOutcome.Failed, userId: UserA, arguments: """{"path":"a.txt"}""");
        await ToolFixtures.AddAuditEntryAsync(app, Noon.AddHours(-1), tool: "middle");

        var page = await ReadAsync(client, Audit);

        Assert.Equal(3, page.Total);
        Assert.Equal(["new", "middle", "old"], page.Items.Select(i => i.ToolName));
        var newest = page.Items[0];
        Assert.Equal("files", newest.ServerName);
        Assert.Equal("Failed", newest.Outcome);
        Assert.Equal("Auto", newest.Decision);
        Assert.Equal("""{"path":"a.txt"}""", newest.Arguments);
        Assert.Equal(UserA, newest.UserId);
        Assert.Equal(Noon, newest.StartedAt);
        Assert.Equal(40, newest.DurationMs);
        Assert.Equal(5, newest.ResultCharacters);
        Assert.NotEqual(Guid.Empty, newest.AssistantId);
        Assert.NotEqual(Guid.Empty, newest.ConversationId);
    }

    [Fact]
    public async Task AnEntryStillStartedHasNoCompletionYet()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await ToolFixtures.AddAuditEntryAsync(app, Noon, outcome: ToolOutcome.Started);

        var item = Assert.Single((await ReadAsync(client, Audit)).Items);

        Assert.Equal("Started", item.Outcome);
        Assert.Null(item.CompletedAt);
        Assert.Null(item.DurationMs);
    }

    [Fact] // AC-46
    public async Task FilteringByPeriodReturnsOnlyEntriesStartedWithinIt()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await ToolFixtures.AddAuditEntryAsync(app, Noon.AddDays(-3), tool: "before");
        await ToolFixtures.AddAuditEntryAsync(app, Noon.AddDays(-1), tool: "inside");
        await ToolFixtures.AddAuditEntryAsync(app, Noon.AddDays(2), tool: "after");

        var page = await ReadAsync(client, Query(("from", Noon.AddDays(-2)), ("to", Noon)));

        Assert.Equal(["inside"], page.Items.Select(i => i.ToolName));
        Assert.Equal(1, page.Total);
    }

    [Fact] // AC-46
    public async Task ThePeriodIncludesItsBounds()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await ToolFixtures.AddAuditEntryAsync(app, Noon, tool: "at-from");
        await ToolFixtures.AddAuditEntryAsync(app, Noon.AddHours(1), tool: "at-to");

        var page = await ReadAsync(client, Query(("from", Noon), ("to", Noon.AddHours(1))));

        Assert.Equal(["at-to", "at-from"], page.Items.Select(i => i.ToolName));
    }

    [Theory] // AC-46
    [InlineData("builtin__calculator", 1)]
    [InlineData("files__read", 1)]
    [InlineData("calculator", 1)]
    [InlineData("read", 1)]
    [InlineData("files__calculator", 0)]
    [InlineData("missing", 0)]
    public async Task FilteringByToolMatchesTheNameTheModelSawOrTheToolsOwnName(string tool, int expected)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await ToolFixtures.AddAuditEntryAsync(app, Noon, tool: "calculator");
        await ToolFixtures.AddAuditEntryAsync(app, Noon.AddMinutes(1), tool: "read", server: "files");

        var page = await ReadAsync(client, Query(("tool", tool)));

        Assert.Equal(expected, page.Total);
    }

    [Fact] // AC-46
    public async Task FilteringByUserReturnsOnlyThatUsersCalls()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await ToolFixtures.AddAuditEntryAsync(app, Noon, tool: "mine", userId: UserA);
        await ToolFixtures.AddAuditEntryAsync(app, Noon.AddMinutes(1), tool: "theirs", userId: UserB);

        var page = await ReadAsync(client, Query(("userId", UserB)));

        Assert.Equal(["theirs"], page.Items.Select(i => i.ToolName));
    }

    [Theory] // AC-46
    [InlineData("Denied")]
    [InlineData("denied")]
    [InlineData("DENIED")]
    public async Task FilteringByOutcomeIgnoresCaseAndReturnsOnlyThatOutcome(string outcome)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await ToolFixtures.AddAuditEntryAsync(app, Noon, tool: "ok", outcome: ToolOutcome.Succeeded);
        await ToolFixtures.AddAuditEntryAsync(app, Noon.AddMinutes(1), tool: "no", outcome: ToolOutcome.Denied);

        var page = await ReadAsync(client, Query(("outcome", outcome)));

        Assert.Equal(["no"], page.Items.Select(i => i.ToolName));
    }

    [Fact] // AC-46
    public async Task FiltersCombineSoAnEntryMustMatchAllOfThem()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await ToolFixtures.AddAuditEntryAsync(app, Noon, tool: "calculator", userId: UserA, outcome: ToolOutcome.Failed);
        await ToolFixtures.AddAuditEntryAsync(app, Noon.AddMinutes(1), tool: "calculator", userId: UserB, outcome: ToolOutcome.Failed);
        await ToolFixtures.AddAuditEntryAsync(app, Noon.AddMinutes(2), tool: "calculator", userId: UserA, outcome: ToolOutcome.Succeeded);

        var page = await ReadAsync(client, Query(("tool", "calculator"), ("userId", UserA), ("outcome", "failed")));

        Assert.Equal(1, page.Total);
        Assert.Equal(Noon, Assert.Single(page.Items).StartedAt);
    }

    [Fact]
    public async Task PagesSplitTheLogAndTheTotalCountsAllMatchingEntries()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        for (var i = 0; i < 5; i++)
        {
            await ToolFixtures.AddAuditEntryAsync(app, Noon.AddMinutes(i), tool: $"t{i}");
        }

        var second = await ReadAsync(client, Query(("page", 2), ("pageSize", 2)));
        var last = await ReadAsync(client, Query(("page", 3), ("pageSize", 2)));
        var beyond = await ReadAsync(client, Query(("page", 4), ("pageSize", 2)));

        Assert.Equal(["t2", "t1"], second.Items.Select(i => i.ToolName));
        Assert.Equal(["t0"], last.Items.Select(i => i.ToolName));
        Assert.Empty(beyond.Items);
        Assert.All(new[] { second, last, beyond }, p => Assert.Equal(5, p.Total));
    }

    [Fact]
    public async Task WithoutPagingTheFirstPageHolds50Entries()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        for (var i = 0; i < 52; i++)
        {
            await ToolFixtures.AddAuditEntryAsync(app, Noon.AddMinutes(i));
        }

        var page = await ReadAsync(client, Audit);

        Assert.Equal(50, page.Items.Count);
        Assert.Equal(52, page.Total);
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=-1")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=201")]
    [InlineData("outcome=exploded")]
    [InlineData("userId=not-a-guid")]
    [InlineData("from=yesterday")]
    [InlineData("from=2026-10-12T00:00:00Z&to=2026-10-11T00:00:00Z")]
    public async Task InvalidParametersAreRejectedAsBadRequests(string query)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();

        using var response = await client.GetAsync(new Uri($"/api/tools/audit?{query}", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnotherTenantsEntriesAreNeverReturned()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await ToolFixtures.AddAuditEntryAsync(app, Noon, tool: "mine");
        await ToolFixtures.AddAuditEntryAsync(app, Noon.AddMinutes(1), tool: "foreign", tenantId: Guid.CreateVersion7());

        var page = await ReadAsync(client, Audit);

        Assert.Equal(["mine"], page.Items.Select(i => i.ToolName));
        Assert.Equal(1, page.Total);
    }

    [Theory] // AC-46
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task NoRouteModifiesOrDeletesEntries(string method)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await ToolFixtures.AddAuditEntryAsync(app, Noon);
        var id = await ToolFixtures.ScalarAsync<Guid>(app, "SELECT \"Id\" FROM agents.tool_audit_entries LIMIT 1");

        foreach (var uri in new[] { Audit, new Uri($"/api/tools/audit/{id}", UriKind.Relative) })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), uri) { Content = JsonContent.Create(new { outcome = "Succeeded" }) };
            using var response = await client.SendAsync(request, Ct);

            Assert.True(
                response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
                $"{method} {uri} answered {(int)response.StatusCode}.");
        }

        Assert.Equal(1L, await ToolFixtures.ScalarAsync<long>(app, "SELECT count(*) FROM agents.tool_audit_entries"));
    }

    [Fact]
    public async Task TheLogHoldsNoResultContentOnlyItsSize()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await ToolFixtures.AddAuditEntryAsync(app, Noon);

        using var response = await client.GetAsync(Audit, Ct);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var properties = document.RootElement.GetProperty("items")[0].EnumerateObject().Select(p => p.Name).ToArray();

        Assert.Contains("resultCharacters", properties);
        Assert.DoesNotContain(properties, name => name.Contains("content", StringComparison.OrdinalIgnoreCase) || name.Equals("result", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(21_474_837)]
    [InlineData(int.MaxValue)]
    public async Task APageFarBeyondTheEndIsEmptyNotAnError(int page)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await ToolFixtures.AddAuditEntryAsync(app, Noon);

        var result = await ReadAsync(client, Query(("page", page), ("pageSize", 200)));

        Assert.Empty(result.Items);
        Assert.Equal(1, result.Total);
    }

    [Fact]
    public async Task ADateWithoutATimeCoversTheWholeDayInUtc()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var day = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
        await ToolFixtures.AddAuditEntryAsync(app, day.AddSeconds(-1), tool: "previous");
        await ToolFixtures.AddAuditEntryAsync(app, day, tool: "start");
        await ToolFixtures.AddAuditEntryAsync(app, day.AddHours(23).AddMinutes(59).AddSeconds(59), tool: "end");
        await ToolFixtures.AddAuditEntryAsync(app, day.AddDays(1), tool: "next");

        var page = await ReadAsync(client, Query(("from", "2026-10-12"), ("to", "2026-10-12")));

        Assert.Equal(["end", "start"], page.Items.Select(i => i.ToolName));
    }

    [Fact]
    public async Task ATimeWithoutAnOffsetIsTakenAsUtcAndAnOffsetIsRespected()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await ToolFixtures.AddAuditEntryAsync(app, new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero), tool: "ten-utc");

        var withoutOffset = await ReadAsync(client, Query(("from", "2026-10-12T10:00:00"), ("to", "2026-10-12T10:00:00")));
        var withOffset = await ReadAsync(client, Query(("from", "2026-10-12T12:00:00+02:00"), ("to", "2026-10-12T12:00:00+02:00")));
        var elsewhere = await ReadAsync(client, Query(("from", "2026-10-12T10:00:00+02:00"), ("to", "2026-10-12T10:00:00+02:00")));

        Assert.Equal(["ten-utc"], withoutOffset.Items.Select(i => i.ToolName));
        Assert.Equal(["ten-utc"], withOffset.Items.Select(i => i.ToolName));
        Assert.Empty(elsewhere.Items);
    }

    private sealed record AuditPage(IReadOnlyList<AuditItem> Items, int Total);

    private sealed record AuditItem(
        Guid Id,
        DateTimeOffset StartedAt,
        DateTimeOffset? CompletedAt,
        Guid UserId,
        Guid AssistantId,
        Guid ConversationId,
        Guid CallId,
        string? ServerName,
        string ToolName,
        string? Decision,
        string Outcome,
        string Arguments,
        bool ArgumentsTruncated,
        int? ResultCharacters,
        int? DurationMs,
        string? ErrorCode);
}
