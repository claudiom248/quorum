using System.Globalization;
using System.Text.Json.Nodes;

namespace Quorum.SampleKnowledgeBase;

/// <summary>
/// The company of the samples: <c>sample_company</c>, six hundred and twenty agreements of thirty-six kinds across twenty-two sites, and
/// <c>sample_vendors</c>, the forty-two vendors they are with. Every agreement is written from the facts <see cref="CompanyContractGraph"/>
/// decided for it, so no two chunks read alike, and the references it makes are true: the vendor fits the kind, the owner is one of the
/// people of <see cref="PeopleSampleData"/> who really deal with that vendor, the rules it quotes are the policies of the handbook, and the
/// agreements it names exist in the same collection.
/// </summary>
public static class CompanySampleData
{
    private static readonly Lazy<IReadOnlyList<CompanyContractGraph.Contract>> Contracts = new(CompanyContractGraph.Build);

    /// <summary>
    /// The contracts, one point for every clause of every agreement: <c>supplier</c>, <c>department</c>, <c>nationality</c>,
    /// <c>audience</c> (a list), <c>kind</c>, <c>contract_role</c>, <c>family_id</c>, <c>sites</c>, <c>contract_manager</c>,
    /// <c>requester</c>, <c>governed_by</c>, <c>cost_center</c>, <c>related_document_ids</c>, <c>year</c>, <c>status</c>,
    /// <c>confidential</c>, <c>value_eur</c>, <c>term_months</c>, <c>signed_on</c>, <c>ends_on</c>, <c>is_first_chunk</c> and
    /// <c>chunk_index</c>, all indexed.
    /// </summary>
    public static SampleDataset CreateContracts(string name) => new(
        name,
        "Six hundred and twenty agreements of thirty-six kinds in twenty-two sites with forty-two vendors, from 2018 to 2025, in five states, for many audiences (a list), each one a family of framework, statements of work, amendments, extensions and renewals that quote the agreements, the sites, the people and the handbook rules it really refers to.",
        [new SampleVector(null, DeterministicEmbedding.DefaultDimensions)],
        [
            new("document_id", "keyword"),
            new("related_document_ids", "keyword"),
            new("family_id", "keyword"),
            new("supplier", "keyword"),
            new("kind", "keyword"),
            new("contract_role", "keyword"),
            new("department", "keyword"),
            new("nationality", "keyword"),
            new("audience", "keyword"),
            new("sites", "keyword"),
            new("contract_manager", "keyword"),
            new("requester", "keyword"),
            new("governed_by", "keyword"),
            new("cost_center", "keyword"),
            new("status", "keyword"),
            new("year", "integer"),
            new("term_months", "integer"),
            new("value_eur", "integer"),
            new("confidential", "bool"),
            new("signed_on", "datetime"),
            new("ends_on", "datetime"),
            new("is_first_chunk", "bool"),
            new("chunk_index", "integer"),
        ],
        ContractPoints());

    /// <summary>
    /// The vendors of those agreements as entities: country, category, how many agreements they hold, the kinds and the sites they work
    /// in, the date they started, a rating, whether they are preferred, the value the company has spent with them and the people who deal
    /// with them — the counts and the names come from the same data the contracts are made of.
    /// </summary>
    public static SampleDataset CreateVendors(string name) => new(
        name,
        "One point per vendor (name, country, category, contract_count, supplier_since, rating, preferred, kinds, sites, departments, total_value_eur, largest_contract, contacts) with the figures and the relationships of the contracts collection.",
        [new SampleVector(null, DeterministicEmbedding.DefaultDimensions)],
        [
            new("country", "keyword"),
            new("category", "keyword"),
            new("contract_count", "integer"),
            new("supplier_since", "datetime"),
            new("rating", "float"),
            new("preferred", "bool"),
            new("kinds", "keyword"),
            new("sites", "keyword"),
            new("departments", "keyword"),
            new("total_value_eur", "integer"),
        ],
        VendorPoints());

    private static List<SamplePoint> ContractPoints()
    {
        var points = new List<SamplePoint>();
        ulong id = 1;
        foreach (var contract in Contracts.Value)
        {
            var title = TitleOf(contract);
            for (var chunk = 0; chunk < contract.Sections.Length; chunk++)
            {
                var text = contract.Sections[chunk];
                points.Add(new SamplePoint(
                    id++,
                    text,
                    new JsonObject
                    {
                        ["text"] = text,
                        ["document_id"] = contract.Id,
                        ["title"] = title,
                        ["source_url"] = SampleDocuments.Url("contracts", contract.Id),
                        ["supplier"] = contract.Vendor.Name,
                        ["nationality"] = contract.Vendor.Country,
                        ["kind"] = contract.Kind.Id,
                        ["contract_role"] = contract.Role,
                        ["family_id"] = contract.FamilyId,
                        ["department"] = contract.Kind.Department,
                        ["sites"] = List(contract.SiteNameList),
                        ["contract_manager"] = contract.Owner.Name,
                        ["requester"] = contract.Requester.Name,
                        ["governed_by"] = List(contract.Policies.Select(policy => policy.Id)),
                        ["cost_center"] = contract.CostCenter,
                        ["related_document_ids"] = List(contract.RelatedIds),
                        ["audience"] = List(contract.Audience),
                        ["year"] = contract.Year,
                        ["status"] = contract.Status,
                        ["confidential"] = contract.Confidential,
                        ["term_months"] = contract.TermMonths,
                        ["value_eur"] = contract.ValueEur,
                        ["signed_on"] = Iso(contract.Signed),
                        ["ends_on"] = Iso(contract.Ends),
                        ["is_first_chunk"] = chunk == 0,
                        ["chunk_index"] = chunk,
                    }));
            }
        }

        return points;
    }

    private static List<SamplePoint> VendorPoints()
    {
        var points = new List<SamplePoint>();
        ulong id = 1;
        foreach (var vendor in CompanyFacts.Vendors)
        {
            var held = Contracts.Value.Where(contract => contract.Vendor == vendor).OrderBy(contract => contract.Signed).ToArray();
            var largest = held.MaxBy(contract => contract.ValueEur)!;
            var contacts = PeopleSampleData.Employees
                .Where(employee => employee.VendorContacts.Contains(vendor.Name, StringComparer.Ordinal))
                .Select(employee => employee.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            var kinds = held.Select(contract => contract.Kind.Title).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var sites = held.Select(contract => contract.Site.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var departments = held.GroupBy(contract => contract.Kind.Department)
                .OrderByDescending(group => group.Count())
                .Select(group => group.Key)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var busiest = held.GroupBy(contract => contract.Kind.Department).OrderByDescending(group => group.Count()).First();
            var total = held.Sum(contract => contract.ValueEur);

            var text = $"{vendor.Name} is a {vendor.Category} vendor from {vendor.Country}, rated {vendor.Rating.ToString("0.0", CultureInfo.InvariantCulture)} out of 5"
                + (vendor.Preferred ? " and on the preferred list of the group" : " and not on the preferred list")
                + $", supplying the company since {vendor.Since}. It holds {held.Length} agreements of the company, {CompanyContractGraph.Euros(total)} euros in all, across {kinds.Length} kinds of contract and {sites.Length} sites: {string.Join(", ", kinds)}. "
                + $"The work is done at {string.Join(", ", sites)}. The biggest paper is the {largest.Kind.Title.ToLowerInvariant()} at {largest.Site.Name} ({CompanyContractGraph.Euros(largest.ValueEur)} euros, {largest.Year}). "
                + $"{busiest.Key} is the department that buys most from it, and the file is carried by {busiest.First().Owner.Name} ({busiest.First().Owner.Role}). "
                + (contacts.Length > 0
                    ? $"The people of the company who deal with {vendor.Name} are {string.Join(", ", contacts)}."
                    : $"Nobody in the directory deals with {vendor.Name} day to day: the relationships go through procurement.");

            points.Add(new SamplePoint(
                id++,
                text,
                new JsonObject
                {
                    ["text"] = text,
                    ["name"] = vendor.Name,
                    ["source_url"] = SampleDocuments.Url("vendors", vendor.Name),
                    ["country"] = vendor.Country,
                    ["category"] = vendor.Category,
                    ["contract_count"] = held.Length,
                    ["supplier_since"] = vendor.Since,
                    ["rating"] = vendor.Rating,
                    ["preferred"] = vendor.Preferred,
                    ["kinds"] = List(kinds),
                    ["sites"] = List(sites),
                    ["departments"] = List(departments),
                    ["total_value_eur"] = total,
                    ["largest_contract"] = largest.Id,
                    ["contacts"] = List(contacts),
                }));
        }

        return points;
    }

    private static string TitleOf(CompanyContractGraph.Contract contract)
    {
        var role = contract.Role switch
        {
            "work_order" => "Statement of work — ",
            "amendment" => "Amendment — ",
            "extension" => "Extension — ",
            "renewal" => "Renewal — ",
            _ => string.Empty,
        };

        return $"{role}{contract.Kind.Title}: {contract.Site.Name} ({contract.Vendor.Name}, {contract.Year})";
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static JsonArray List(IEnumerable<string> items) => [.. items.Select(item => (JsonNode?)JsonValue.Create(item))];
}
