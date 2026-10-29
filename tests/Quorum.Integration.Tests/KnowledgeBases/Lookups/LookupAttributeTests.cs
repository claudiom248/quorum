using System.Net;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.Tools;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Persistence;

namespace Quorum.Integration.Tests.KnowledgeBases.Lookups;

/// <summary>Typed attributes on manual lookups: the rows, the filters on them, and the CSV that creates them (spec 012 D-3, D-5, D-7).</summary>
public sealed class LookupAttributeTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Uri ImportPreview = new("/api/knowledge-bases/manual-lookups/import-preview", UriKind.Relative);

    [Fact] // AC-3
    public async Task FiltersOnAttributesSelectTheRowsOfASnapshotAndAnUnknownAttributeIsRefused()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var id = await CreateVendorsAsync(admin, italyRating: 4.5, franceRating: 3);

        var filtered = await LookupApi.ReadValuesOkAsync(admin, id, "filters=" + Uri.EscapeDataString("{\"rating\":{\"min\":4}}"));
        Assert.Equal(["IT"], filtered.Values.Select(v => v.Value));

        using var unknown = await admin.GetAsync(LookupApi.Values(id, "filters=" + Uri.EscapeDataString("{\"nope\":{\"eq\":1}}")), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("lookup.invalid_filter", (await unknown.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString());
    }

    [Fact] // AC-7
    public async Task AValueThatDoesNotFitItsTypeNamesTheRowAndTheColumn()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        using var response = await LookupApi.CreateManualAsync(admin, VendorsBody(italyRating: 4.5, franceRating: "high"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("lookup.invalid_value", problem.GetProperty("code").GetString());
        var detail = problem.GetProperty("detail").GetString()!;
        Assert.Contains("Row 'FR'", detail, StringComparison.Ordinal);
        Assert.Contains("column 'rating'", detail, StringComparison.Ordinal);
    }

    [Fact] // AC-7
    public async Task TheImportPreviewProposesATypeForEachAttributeColumn()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var file = new MultipartFormDataContent { { new ByteArrayContent("value,rating,since\nIT,4.5,2020-01-01\nFR,3,2021-01-01\n"u8.ToArray()), "file", "vendors.csv" } };

        using var response = await admin.PostAsync(ImportPreview, file, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var columns = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("columns").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("type").GetString());
        Assert.Equal("Number", columns["rating"]);
        Assert.Equal("Date", columns["since"]);
    }

    [Fact] // AC-9
    public async Task AFilteredPageOfFiftyThousandRowsIsReadInUnderASecond()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var id = await CreateVendorsAsync(admin, italyRating: 4.5, franceRating: 3);
        var tenant = await TenantScope.TenantIdAsync(app);
        await using (var services = app.Services.CreateAsyncScope())
        {
            var db = services.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
            var slice = await db.LookupSlices.IgnoreQueryFilters().SingleAsync(s => s.LookupId == id, Ct);
            db.LookupValues.AddRange(Enumerable.Range(0, 50_000).Select(i => LookupValue.Create(
                tenant,
                slice.Id,
                slice.CurrentGeneration,
                1_000 + i,
                $"vendor-{i}",
                attributes: JsonSerializer.Serialize(new { rating = i % 10, since = "2020-01-01" }))));
            await db.SaveChangesAsync(Ct);
        }

        var clock = Stopwatch.StartNew();
        var page = await LookupApi.ReadValuesOkAsync(admin, id, "filters=" + Uri.EscapeDataString("{\"rating\":{\"min\":4}}") + "&limit=50");
        clock.Stop();

        Assert.Equal(50, page.Values.Count);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"The filtered page of 50,000 rows took {clock.Elapsed}.");
    }

    private static async Task<Guid> CreateVendorsAsync(HttpClient admin, double italyRating, double franceRating) =>
        await LookupApi.CreateManualOkAsync(admin, VendorsBody(italyRating, franceRating));

    private static object VendorsBody(object italyRating, object franceRating) => new
    {
        name = "vendors",
        description = "Vendors and their rating.",
        scope = "Tenant",
        attributes = new object[] { new { key = "rating", type = "Number" }, new { key = "since", type = "Date" } },
        values = new object[]
        {
            new { value = "IT", label = "Italy", attributes = new { rating = italyRating, since = "2020-01-01" } },
            new { value = "FR", attributes = new { rating = franceRating, since = "2021-01-01" } },
        },
    };
}
