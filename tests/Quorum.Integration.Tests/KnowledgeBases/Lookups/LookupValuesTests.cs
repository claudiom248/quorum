using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.Tools;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Persistence;

namespace Quorum.Integration.Tests.KnowledgeBases.Lookups;

/// <summary>Reading a page of a lookup, whatever its kind (spec 007 D-12, D-13): paging, search, sort and the cursor.</summary>
public sealed class LookupValuesTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IReadOnlyList<(string Value, int Count)> Suppliers { get; } =
    [
        ("Acme", 90), ("Globex", 90), ("Initech", 70), ("umbrella", 40), ("Éclair", 40), ("Hooli", 10), ("Soylent", 5),
    ];

    private static async Task<List<string>> ReadAllAsync(HttpClient client, Guid lookupId, string query, int limit)
    {
        var names = new List<string>();
        string? cursor = null;
        do
        {
            var page = await LookupApi.ReadValuesOkAsync(client, lookupId, $"{query}&limit={limit}{(cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor))}");
            Assert.False(page.CursorRestarted);
            names.AddRange(page.Values.Select(v => v.Value));
            cursor = page.Cursor;
        }
        while (cursor is not null);

        return names;
    }

    [Fact] // AC-10 (admin view)
    public async Task ASearchFindsAManualValueByItsSynonymLabelOrValue()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var id = await LookupApi.CreateManualOkAsync(admin);

        var bySynonym = await LookupApi.ReadValuesOkAsync(admin, id, "search=italiana");
        var byLabel = await LookupApi.ReadValuesOkAsync(admin, id, "search=FRAN");
        var byValue = await LookupApi.ReadValuesOkAsync(admin, id, "search=it");
        var nothing = await LookupApi.ReadValuesOkAsync(admin, id, "search=spain");

        var italy = Assert.Single(bySynonym.Values);
        Assert.Equal(("IT", "Italy"), (italy.Value, italy.Label));
        Assert.Equal(["Italia", "italiana"], italy.Synonyms!);
        Assert.Equal(["FR"], byLabel.Values.Select(v => v.Value));
        Assert.Equal(["IT"], byValue.Values.Select(v => v.Value));
        Assert.Empty(nothing.Values);
    }

    [Fact]
    public async Task ASearchTreatsPercentUnderscoreAndBackslashAsPlainText()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var id = await LookupApi.CreateManualOkAsync(
            admin,
            LookupApi.ManualBody(values: [LookupApi.Value("100%"), LookupApi.Value("a_b"), LookupApi.Value("axb"), LookupApi.Value(@"c\d"), LookupApi.Value("cd")]));

        Assert.Equal(["100%"], (await LookupApi.ReadValuesOkAsync(admin, id, "search=%25")).Values.Select(v => v.Value));
        Assert.Equal(["a_b"], (await LookupApi.ReadValuesOkAsync(admin, id, "search=_")).Values.Select(v => v.Value));
        Assert.Equal([@"c\d"], (await LookupApi.ReadValuesOkAsync(admin, id, @"search=%5C")).Values.Select(v => v.Value));
    }

    [Fact]
    public async Task TheDefaultOrderIsByCountThenValueAndAPageByNameIsAlphabetical()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var (id, _) = await LookupApi.SeedComputedAsync(app, await TenantScope.TenantIdAsync(app), Suppliers);

        var byCount = await LookupApi.ReadValuesOkAsync(admin, id);
        var byName = await LookupApi.ReadValuesOkAsync(admin, id, "sort=name");

        Assert.Equal(["Acme", "Globex", "Initech", "umbrella", "Éclair", "Hooli", "Soylent"], byCount.Values.Select(v => v.Value).ToArray());
        Assert.Equal([90, 90, 70, 40, 40, 10, 5], byCount.Values.Select(v => v.Count).ToArray());
        // The order of a name is the database's own collation, so the table is asked what it is.
        var inTheDatabase = await ToolFixtures.ScalarAsync<string>(app, "SELECT string_agg(\"Value\", ',' ORDER BY \"Value\") FROM knowledge_base.lookup_values");
        Assert.Equal(inTheDatabase, string.Join(',', byName.Values.Select(v => v.Value)));
    }

    [Theory]
    [InlineData("sort=count", 1)]
    [InlineData("sort=count", 3)]
    [InlineData("sort=name", 1)]
    [InlineData("sort=name", 2)]
    [InlineData("sort=name&search=e", 2)]
    public async Task PagingWithTheCursorVisitsEveryValueOnceInTheOrderOfOnePage(string query, int limit)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var (id, _) = await LookupApi.SeedComputedAsync(app, await TenantScope.TenantIdAsync(app), Suppliers);

        var whole = (await LookupApi.ReadValuesOkAsync(admin, id, query)).Values.Select(v => v.Value).ToList();
        var paged = await ReadAllAsync(admin, id, query, limit);

        Assert.Equal(whole, paged);
        Assert.Equal(whole.Count, paged.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task TheLastPageHasNoCursorAndAFullPageThatEndsTheListNeedsNoMoreRequests()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var (id, _) = await LookupApi.SeedComputedAsync(app, await TenantScope.TenantIdAsync(app), Suppliers);

        var exact = await LookupApi.ReadValuesOkAsync(admin, id, $"limit={Suppliers.Count}");
        var partial = await LookupApi.ReadValuesOkAsync(admin, id, $"limit={Suppliers.Count - 1}");

        Assert.Null(exact.Cursor);
        Assert.NotNull(partial.Cursor);
    }

    [Theory]
    [InlineData("limit=1000", 200)]
    [InlineData("limit=0", 1)]
    [InlineData("limit=-5", 1)]
    [InlineData("", 50)]
    public async Task TheLimitDefaultsToFiftyAndStaysBetweenOneAndTwoHundred(string query, int expected)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var many = Enumerable.Range(0, 250).Select(i => ($"supplier-{i:D3}", 1000 - i)).ToList();
        var (id, _) = await LookupApi.SeedComputedAsync(app, await TenantScope.TenantIdAsync(app), many);

        var page = await LookupApi.ReadValuesOkAsync(admin, id, query);

        Assert.Equal(expected, page.Values.Count);
        Assert.NotNull(page.Cursor);
    }

    [Fact]
    public async Task ACursorOfAnOlderGenerationRestartsTheListAndSaysSo()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var tenantId = await TenantScope.TenantIdAsync(app);
        var (id, sliceId) = await LookupApi.SeedComputedAsync(app, tenantId, Suppliers);
        var first = await LookupApi.ReadValuesOkAsync(admin, id, "limit=2");
        Assert.NotNull(first.Cursor);

        await using (var scope = TenantScope.Open(app, tenantId))
        {
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
            var slice = await db.LookupSlices.SingleAsync(s => s.Id == sliceId, Ct);
            await LookupApi.AddGenerationAsync(db, tenantId, slice, 2, [("Zeta", 9), ("Yankee", 8), ("X-ray", 7)]);
        }

        var restarted = await LookupApi.ReadValuesOkAsync(admin, id, $"limit=2&cursor={Uri.EscapeDataString(first.Cursor)}");

        Assert.True(restarted.CursorRestarted);
        Assert.Equal(["Zeta", "Yankee"], restarted.Values.Select(v => v.Value));
        Assert.NotNull(restarted.Cursor);
        var next = await LookupApi.ReadValuesOkAsync(admin, id, $"limit=2&cursor={Uri.EscapeDataString(restarted.Cursor)}");
        Assert.False(next.CursorRestarted);
        Assert.Equal(["X-ray"], next.Values.Select(v => v.Value));
    }

    [Theory]
    [InlineData("cursor=not-a-cursor")]
    [InlineData("cursor=e30")]
    public async Task ACursorThatWasNeverIssuedIsRefused(string query)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var (id, _) = await LookupApi.SeedComputedAsync(app, await TenantScope.TenantIdAsync(app), Suppliers);

        using var response = await admin.GetAsync(LookupApi.Values(id, query), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("lookup.invalid_cursor", await LookupApi.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ACursorOfAnotherSortIsRefused()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var (id, _) = await LookupApi.SeedComputedAsync(app, await TenantScope.TenantIdAsync(app), Suppliers);
        var byName = await LookupApi.ReadValuesOkAsync(admin, id, "sort=name&limit=2");

        using var response = await admin.GetAsync(LookupApi.Values(id, $"sort=count&cursor={Uri.EscapeDataString(byName.Cursor!)}"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("lookup.invalid_cursor", await LookupApi.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task AnUnknownSortIsRefused()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var (id, _) = await LookupApi.SeedComputedAsync(app, await TenantScope.TenantIdAsync(app), Suppliers);

        using var response = await admin.GetAsync(LookupApi.Values(id, "sort=size"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("lookup.invalid_sort", await LookupApi.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task TheUnitTheDateAndTheFlagsOfTheSliceAreReturned()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var (id, _) = await LookupApi.SeedComputedAsync(app, await TenantScope.TenantIdAsync(app), Suppliers, LookupUnit.Chunks);
        await ToolFixtures.ExecuteAsync(app, "UPDATE knowledge_base.lookup_slices SET \"Stale\" = true, \"Truncated\" = true");

        var page = await LookupApi.ReadValuesOkAsync(admin, id);

        Assert.Equal(("chunks", true, true), (page.Unit, page.Stale, page.Truncated));
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero), page.AsOf);
    }

    [Fact]
    public async Task ASliceWithNoGenerationYetHasNoValuesAndNoDate()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var (id, _) = await LookupApi.SeedComputedAsync(app, await TenantScope.TenantIdAsync(app), []);
        await ToolFixtures.ExecuteAsync(app, "UPDATE knowledge_base.lookup_slices SET \"CurrentGeneration\" = 0, \"ComputedAt\" = NULL");

        var page = await LookupApi.ReadValuesOkAsync(admin, id);

        Assert.Empty(page.Values);
        Assert.Null(page.AsOf);
        Assert.Null(page.Cursor);
    }

    [Fact]
    public async Task OnlyTheCurrentGenerationIsRead()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var tenantId = await TenantScope.TenantIdAsync(app);
        var (id, sliceId) = await LookupApi.SeedComputedAsync(app, tenantId, [("old", 1)]);
        await using (var scope = TenantScope.Open(app, tenantId))
        {
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
            var slice = await db.LookupSlices.SingleAsync(s => s.Id == sliceId, Ct);
            // A computation that crashed left a generation nobody reads, and the previous one is still there.
            db.LookupValues.Add(LookupValue.Create(tenantId, sliceId, 3, 0, "half-written", 1));
            await LookupApi.AddGenerationAsync(db, tenantId, slice, 2, [("new", 1)]);
        }

        var page = await LookupApi.ReadValuesOkAsync(admin, id);

        Assert.Equal(["new"], page.Values.Select(v => v.Value));
    }

    [Fact]
    public async Task ASliceOtherThanTheWholeKnowledgeBaseIsFoundByItsHash()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var tenantId = await TenantScope.TenantIdAsync(app);
        var (id, _) = await LookupApi.SeedComputedAsync(app, tenantId, Suppliers);
        var hash = new string('a', 64);
        await using (var scope = TenantScope.Open(app, tenantId))
        {
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
            var slice = LookupSlice.Create(tenantId, id, hash, """[{"field":"00000000-0000-0000-0000-000000000001","is":true}]""", DateTimeOffset.UnixEpoch);
            db.LookupSlices.Add(slice);
            await db.SaveChangesAsync(Ct);
            await LookupApi.AddGenerationAsync(db, tenantId, slice, 1, [("Only", 1)]);
        }

        var narrow = await LookupApi.ReadValuesOkAsync(admin, id, $"slice={hash}");
        using var unknown = await admin.GetAsync(LookupApi.Values(id, $"slice={new string('b', 64)}"), Ct);

        Assert.Equal(["Only"], narrow.Values.Select(v => v.Value));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task AnUnknownLookupAndTheLookupsOfAnotherTenantReadAsMissing()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var (other, _) = await LookupApi.SeedComputedAsync(app, Guid.NewGuid(), Suppliers);

        using var unknown = await admin.GetAsync(LookupApi.Values(Guid.NewGuid()), Ct);
        using var ofAnotherTenant = await admin.GetAsync(LookupApi.Values(other), Ct);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, ofAnotherTenant.StatusCode);
    }
}
