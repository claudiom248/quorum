using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBases;

/// <summary>
/// The sample documents server: every document the sample data point to is served as a page and as text, so a citation of the sample
/// knowledge bases can always be opened.
/// </summary>
public sealed class SampleDocumentsServerTests : IAsyncLifetime
{
    private WebApplication? _app;
    private HttpClient? _http;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The relations and collections that exist to exercise one schema case have the few columns of that case on purpose, and no address.
    private static readonly string[] SchemaCases =
    [
        "sample_tags", "sample_two_vectors", "sample_halfvec", "sample_unspecified_dimensions", "sample_other_vector_size", "sample_empty", "sample_view",
        "sample_ivfflat_l2", "sample_inner_product", "Sample Mixed Case", "sample_dotted_column", "sample_unmappable", "sample_named_vectors", "sample_tags",
    ];

    public async ValueTask InitializeAsync()
    {
        _app = SampleDocumentsServer.Build("http://127.0.0.1:0");
        await _app.StartAsync(Ct);
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _http = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private static string? UrlOf(JsonObject payload) =>
        payload["source_url"]?.GetValue<string>() ?? (payload["meta"] as JsonObject)?["source_url"]?.GetValue<string>();

    private static IEnumerable<(string Source, string Url)> EveryAddress()
    {
        foreach (var dataset in SampleCollections.All())
        {
            foreach (var point in dataset.Points.Where(point => UrlOf(point.Payload) is not null))
            {
                yield return (dataset.Name, UrlOf(point.Payload)!);
            }
        }

        foreach (var relation in SampleRelations.All())
        {
            foreach (var row in relation.Rows)
            {
                var url = row.Values.TryGetValue("source_url", out var direct) ? direct as string
                    : row.Values.TryGetValue("cmetadata", out var metadata) && metadata is JsonObject json ? UrlOf(json) : null;
                if (url is not null)
                {
                    yield return (relation.Name, url);
                }
            }
        }
    }

    [Fact]
    public async Task EveryAddressTheSamplesPointToOpensAPageWithTheTextThatWasEmbedded()
    {
        var addresses = EveryAddress().ToList();
        Assert.True(addresses.Count > 200);

        foreach (var (source, url) in addresses.DistinctBy(a => a.Url))
        {
            using var response = await _http!.GetAsync(new Uri(url).PathAndQuery, Ct);

            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} (from {source}) answered {(int)response.StatusCode}.");
            Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
            Assert.Contains("<section>", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task APageHasTheTitleTheDetailsAndEachSectionAndAPlainTextTwin()
    {
        var page = await _http!.GetStringAsync("/policies/leave", Ct);
        var text = await _http.GetStringAsync("/policies/leave.txt", Ct);

        Assert.Contains("<h1>Annual leave policy</h1>", page, StringComparison.Ordinal);
        Assert.Contains("Section 2", page, StringComparison.Ordinal);
        Assert.Contains("twenty-five days of paid annual leave", page, StringComparison.Ordinal);
        Assert.Contains("department", page, StringComparison.Ordinal);
        Assert.StartsWith("Annual leave policy", text, StringComparison.Ordinal);
        Assert.Contains("carried over to the next year", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheContractsThePoliciesAndThePeopleAreEachServedUnderTheirOwnKind()
    {
        var contract = SampleDocuments.Catalog().First(d => d.Kind == "contracts");
        Assert.Contains(WebUtility.HtmlEncode(contract.Title), await _http!.GetStringAsync(contract.Path, Ct), StringComparison.Ordinal);
        Assert.Contains("Remote work policy", await _http.GetStringAsync("/handbook/remote-work", Ct), StringComparison.Ordinal);
        Assert.Contains("Giulia Rossi", await _http.GetStringAsync("/people/1", Ct), StringComparison.Ordinal);
        Assert.Contains("Pied Piper", await _http.GetStringAsync("/vendors/Pied%20Piper", Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheIndexListsEveryDocumentByKindAndAnUnknownAddressIsNotFound()
    {
        var index = await _http!.GetStringAsync("/", Ct);

        Assert.All(SampleDocuments.Catalog(), document => Assert.Contains($"href=\"{document.Path}\"", index, StringComparison.Ordinal));
        using var missing = await _http.GetAsync("/contracts/no-such-contract", Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("no-such-contract", await missing.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }


    [Fact]
    public void ThereAreAtLeastThreeHundredContractsAndFourHundredEmployeesAndNobodyIsRepeated()
    {
        var contracts = SampleDocuments.Catalog().Where(document => document.Kind == "contracts").ToList();
        var employees = PeopleSampleData.Employees;

        Assert.True(contracts.Count >= 300, $"{contracts.Count} contracts.");
        Assert.True(employees.Count >= 400, $"{employees.Count} employees.");
        Assert.Equal(employees.Count, employees.Select(e => e.Name).Distinct().Count());
        Assert.Equal(employees.Count, employees.Select(e => e.HiredOn).Distinct().Count());
        Assert.Equal(employees.Count, employees.Select(e => e.Id).Distinct().Count());
        Assert.All(PeopleSampleData.Departments, department => Assert.Contains(employees, e => e.Department == department));
        Assert.All(PeopleSampleData.Countries, country => Assert.Contains(employees, e => e.Country == country));
    }
    [Fact]
    public void EveryChunkOfTheSamplesThatAreNotSchemaCasesCarriesTheAddressOfItsDocument()
    {
        var without = SampleCollections.All()
            .Where(dataset => !SchemaCases.Contains(dataset.Name) && dataset.Points.Any(point => UrlOf(point.Payload) is null))
            .Select(dataset => dataset.Name)
            .Concat(SampleRelations.All()
                .Where(relation => !SchemaCases.Contains(relation.Name) && relation.Rows.Any(row => !row.Values.ContainsKey("source_url") && !(row.Values.TryGetValue("cmetadata", out var metadata) && metadata is JsonObject json && UrlOf(json) is not null)))
                .Select(relation => relation.Name))
            .ToList();

        Assert.Empty(without);
    }
}
