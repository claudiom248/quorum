using System.Globalization;
using System.Text.Json.Nodes;

namespace Quorum.SampleKnowledgeBase;

/// <summary>
/// A larger company for trying the lookups and the lookup tool with questions that have more than one answer (spec 007): a few dozen
/// contracts of many kinds, in different departments, years, states and for different audiences, and the vendors they are with. The
/// small collections of <see cref="SampleCollections"/> stay as they are, since tests assert their exact numbers.
/// </summary>
public static class CompanySampleData
{
    private sealed record Vendor(string Name, string Country, string Category, string Since, double Rating, bool Preferred);

    private sealed record Contract(
        string Id,
        string Title,
        Vendor Vendor,
        string Department,
        string[] Audience,
        int Year,
        string Status,
        bool Confidential,
        int ValueEuros,
        string SignedOn,
        string[] Sections);

    // Different countries, categories and ratings, some preferred and some not, with a first year of supply from 2015 to 2024.
    private static readonly Vendor[] Vendors =
    [
        new("Acme", "IT", "facilities", "2019-03-01", 4.4, true),
        new("Globex", "US", "technology", "2021-06-15", 4.1, true),
        new("Initech", "DE", "legal", "2023-01-10", 4.7, true),
        new("Hooli", "US", "technology", "2020-09-01", 3.6, false),
        new("Umbrella", "UK", "security", "2018-05-20", 4.0, true),
        new("Stark", "US", "hardware", "2022-02-14", 4.8, true),
        new("Wayne", "US", "consulting", "2017-11-03", 3.9, false),
        new("Wonka", "CH", "catering", "2020-01-27", 4.2, true),
        new("Soylent", "FR", "catering", "2022-09-09", 3.2, false),
        new("Tyrell", "JP", "hardware", "2019-08-19", 4.6, true),
        new("Cyberdyne", "JP", "security", "2021-03-30", 3.8, false),
        new("Gringotts", "UK", "finance", "2016-04-12", 4.9, true),
        new("Oscorp", "US", "research", "2023-05-05", 3.4, false),
        new("Vandelay", "ES", "logistics", "2020-10-10", 3.7, false),
        new("Pied Piper", "US", "software", "2024-01-22", 4.3, true),
        new("Massive Dynamic", "DE", "research", "2018-12-01", 4.5, true),
        new("Aperture", "NL", "laboratory", "2022-07-07", 3.5, false),
        new("Dunder Mifflin", "US", "office supplies", "2015-06-06", 3.3, false),
    ];

    private static readonly string[] All = ["all"];
    private static readonly string[] It = ["it"];
    private static readonly string[] Managers = ["managers"];
    private static readonly string[] ItManagers = ["it", "managers"];
    private static readonly string[] HrManagers = ["hr", "managers"];
    private static readonly string[] FinanceExecutives = ["finance", "executives"];
    private static readonly string[] LegalExecutives = ["legal", "executives"];
    private static readonly string[] SecurityFacilities = ["security", "facilities"];
    private static readonly string[] NewHires = ["new_hires", "hr"];
    private static readonly string[] Executives = ["executives"];
    private static readonly string[] Procurement = ["procurement", "managers"];
    private static readonly string[] Marketing = ["marketing"];
    private static readonly string[] Operations = ["operations", "managers"];

    private static readonly Contract[] Contracts = BuildContracts();

    /// <summary>
    /// About forty contracts of sixteen kinds, several chunks each: <c>supplier</c>, <c>department</c>, <c>nationality</c> (the vendor's
    /// country), <c>audience</c> (a list), <c>year</c>, <c>status</c>, <c>confidential</c>, <c>value_eur</c>, <c>signed_on</c>,
    /// <c>is_first_chunk</c> and <c>chunk_index</c>, all indexed.
    /// </summary>
    public static SampleDataset CreateContracts(string name) => new(
        name,
        "About forty contracts of sixteen kinds, in ten departments, from 2018 to 2025, in four states, for many audiences, with indexes on every filter field.",
        [new SampleVector(null, DeterministicEmbedding.DefaultDimensions)],
        [
            new("document_id", "keyword"),
            new("supplier", "keyword"),
            new("department", "keyword"),
            new("nationality", "keyword"),
            new("audience", "keyword"),
            new("status", "keyword"),
            new("year", "integer"),
            new("value_eur", "integer"),
            new("confidential", "bool"),
            new("signed_on", "datetime"),
            new("is_first_chunk", "bool"),
            new("chunk_index", "integer"),
        ],
        ContractPoints());

    /// <summary>
    /// The vendors of those contracts as entities: country, category, how many contracts they have in <see cref="CreateContracts"/>, the
    /// date they became a vendor, a rating and whether they are preferred.
    /// </summary>
    public static SampleDataset CreateVendors(string name) => new(
        name,
        "One point per vendor (name, country, category, contract_count, supplier_since, rating, preferred), with the contract counts of the contracts collection.",
        [new SampleVector(null, DeterministicEmbedding.DefaultDimensions)],
        [
            new("country", "keyword"),
            new("category", "keyword"),
            new("contract_count", "integer"),
            new("supplier_since", "datetime"),
            new("rating", "float"),
            new("preferred", "bool"),
        ],
        VendorPoints());

    private static List<SamplePoint> VendorPoints()
    {
        var points = new List<SamplePoint>();
        ulong id = 1;
        foreach (var vendor in Vendors)
        {
            var text = $"{vendor.Name} is a {vendor.Category} vendor from {vendor.Country}, rated {vendor.Rating.ToString("0.0", CultureInfo.InvariantCulture)} out of 5"
                + (vendor.Preferred ? " and preferred by the company." : ".");
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
                    ["contract_count"] = Contracts.Count(c => c.Vendor == vendor),
                    ["supplier_since"] = vendor.Since,
                    ["rating"] = vendor.Rating,
                    ["preferred"] = vendor.Preferred,
                }));
        }

        return points;
    }

    private static List<SamplePoint> ContractPoints()
    {
        var points = new List<SamplePoint>();
        ulong id = 1;
        foreach (var contract in Contracts)
        {
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
                        ["title"] = contract.Title,
                        ["source_url"] = SampleDocuments.Url("contracts", contract.Id),
                        ["supplier"] = contract.Vendor.Name,
                        ["department"] = contract.Department,
                        ["nationality"] = contract.Vendor.Country,
                        ["audience"] = new JsonArray(contract.Audience.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
                        ["year"] = contract.Year,
                        ["status"] = contract.Status,
                        ["confidential"] = contract.Confidential,
                        ["value_eur"] = contract.ValueEuros,
                        ["signed_on"] = contract.SignedOn,
                        ["is_first_chunk"] = chunk == 0,
                        ["chunk_index"] = chunk,
                    }));
            }
        }

        return points;
    }

    private static Vendor V(string name) => Vendors.Single(v => v.Name == name);

    private static Contract[] BuildContracts()
    {
        // Kind, vendor, audience, year, status, confidential, months, euros per month (or per year for the audit), days of notice.
        (string Kind, string Vendor, string[] Audience, int Year, string Status, bool Confidential, int Months, int Price, int Notice)[] specs =
        [
            ("cleaning", "Acme", All, 2025, "active", false, 24, 4800, 90),
            ("cleaning", "Acme", All, 2022, "expired", false, 24, 4100, 60),
            ("cleaning", "Aperture", All, 2024, "active", false, 12, 3900, 60),
            ("catering", "Acme", All, 2024, "active", false, 24, 12500, 90),
            ("catering", "Wonka", All, 2021, "expired", false, 36, 11200, 120),
            ("catering", "Wonka", NewHires, 2025, "draft", false, 24, 13400, 90),
            ("catering", "Soylent", All, 2023, "active", false, 24, 9800, 60),
            ("laptops", "Acme", It, 2023, "active", false, 24, 18000, 60),
            ("laptops", "Stark", ItManagers, 2022, "active", false, 36, 26000, 90),
            ("laptops", "Tyrell", It, 2021, "expired", false, 24, 21000, 60),
            ("cloud", "Globex", ItManagers, 2024, "active", true, 36, 9500, 90),
            ("cloud", "Globex", It, 2022, "expired", true, 24, 7800, 60),
            ("cloud", "Pied Piper", ItManagers, 2025, "under_review", true, 36, 11800, 90),
            ("legal", "Initech", Managers, 2023, "active", true, 24, 6000, 60),
            ("legal", "Initech", LegalExecutives, 2020, "expired", true, 36, 5200, 90),
            ("travel", "Globex", Managers, 2023, "active", false, 24, 2100, 60),
            ("security", "Umbrella", SecurityFacilities, 2021, "expired", false, 36, 15800, 90),
            ("security", "Umbrella", SecurityFacilities, 2024, "active", false, 36, 17200, 90),
            ("security", "Cyberdyne", It, 2022, "active", true, 24, 6400, 60),
            ("recruitment", "Wayne", HrManagers, 2023, "active", false, 24, 2500, 30),
            ("recruitment", "Vandelay", NewHires, 2024, "draft", false, 12, 1900, 30),
            ("software", "Hooli", It, 2022, "active", false, 36, 8400, 90),
            ("software", "Hooli", ItManagers, 2024, "under_review", false, 24, 9100, 60),
            ("software", "Tyrell", It, 2023, "active", false, 24, 6700, 60),
            ("software", "Cyberdyne", It, 2025, "under_review", true, 24, 7300, 60),
            ("software", "Pied Piper", It, 2024, "active", false, 24, 5600, 60),
            ("marketing", "Wayne", Marketing, 2020, "expired", false, 24, 14500, 60),
            ("marketing", "Dunder Mifflin", Marketing, 2022, "active", false, 12, 9800, 30),
            ("training", "Wayne", HrManagers, 2023, "active", false, 12, 3600, 30),
            ("training", "Oscorp", NewHires, 2025, "active", false, 12, 4200, 30),
            ("training", "Massive Dynamic", HrManagers, 2024, "active", false, 12, 3900, 30),
            ("logistics", "Vandelay", Operations, 2021, "expired", false, 24, 8800, 60),
            ("logistics", "Vandelay", Operations, 2024, "active", false, 24, 9400, 60),
            ("audit", "Gringotts", FinanceExecutives, 2024, "active", true, 36, 84000, 120),
            ("insurance", "Gringotts", FinanceExecutives, 2022, "active", false, 36, 5400, 90),
            ("insurance", "Gringotts", Executives, 2025, "draft", false, 36, 5900, 90),
            ("office", "Dunder Mifflin", Procurement, 2018, "expired", false, 24, 3000, 30),
            ("office", "Dunder Mifflin", Procurement, 2023, "active", false, 24, 3600, 30),
            ("research", "Oscorp", Executives, 2023, "active", true, 24, 22000, 90),
            ("research", "Massive Dynamic", Executives, 2022, "active", true, 36, 31000, 90),
            ("research", "Stark", Executives, 2024, "draft", true, 24, 27500, 90),
            ("research", "Aperture", Executives, 2023, "under_review", true, 24, 18500, 90),
        ];

        return [.. specs.Select((spec, index) =>
        {
            var vendor = V(spec.Vendor);
            var (title, department, sections) = Describe(spec.Kind, vendor.Name, spec.Months, spec.Price, spec.Notice);
            var month = (index * 5 % 12) + 1;
            var day = (index * 7 % 27) + 1;
            return new Contract(
                $"ct-{index + 1:000}",
                $"{title} ({vendor.Name}, {spec.Year})",
                vendor,
                department,
                spec.Audience,
                spec.Year,
                spec.Status,
                spec.Confidential,
                spec.Months * spec.Price,
                $"{spec.Year}-{month:00}-{day:00}",
                sections);
        })];
    }

    // Each kind of contract reads like one: what is supplied, then what it costs and how long it lasts, then the clauses that matter.
    private static (string Title, string Department, string[] Sections) Describe(string kind, string vendor, int months, int price, int notice)
    {
        var term = $"The contract runs for {months} months and can be ended with {notice} days' written notice.";
        var euros = price.ToString("N0", CultureInfo.InvariantCulture);
        return kind switch
        {
            "cleaning" => ("Cleaning services contract", "facilities",
            [
                $"{vendor} provides daily cleaning of all office floors, meeting rooms and restrooms, Monday to Friday before opening hours, and a monthly deep clean of the kitchens and the windows. Consumables are supplied by {vendor}.",
                $"{term} The fee is {euros} euros per month, invoiced monthly and payable within 30 days.",
                "A missed cleaning must be redone within 24 hours at no cost, and the company may deduct 2% of the monthly fee for each missed service beyond three in a month.",
            ]),
            "catering" => ("Catering contract", "facilities",
            [
                $"{vendor} provides lunch for up to 150 employees on every working day from a weekly menu that always includes vegetarian, vegan and gluten-free dishes, served in the company canteen between 12:00 and 14:30.",
                $"The fee is {euros} euros per month for the agreed volume, with extra meals billed at 8.50 euros. {term}",
                $"{vendor} must follow the food-safety rules in force, allow audits of the kitchen with 48 hours' notice and replace any dish that does not meet the agreed quality.",
            ]),
            "laptops" => ("Laptop supply agreement", "it",
            [
                $"{vendor} supplies business laptops in the models chosen by the IT department, each with a three-year on-site warranty and replacement by the next business day, delivered to the IT warehouse within 15 working days of an order.",
                $"Prices are fixed for the whole term: 1,250 euros for a standard laptop and 1,900 euros for a high-performance one, with an expected spending of {euros} euros per month. Returned devices are wiped by {vendor} under the company's data-erasure procedure, with a certificate for each device. {term}",
            ]),
            "cloud" => ("Cloud hosting agreement", "it",
            [
                $"{vendor} hosts the internal applications of the company on dedicated cloud infrastructure in two European regions, guarantees an availability of 99.9% per month, monitors the systems around the clock and keeps encrypted daily backups for 30 days.",
                $"The fee is {euros} euros per month plus any usage above the agreed quota. Security incidents must be reported within four hours. The data remains the property of the company and is returned in an open format within 30 days of the end of the contract. {term}",
            ]),
            "legal" => ("Legal retainer", "legal",
            [
                $"{vendor} gives legal advice on commercial contracts, employment matters and data protection for a fixed monthly retainer of {euros} euros that covers up to 25 hours. Further hours are billed at 220 euros.",
                $"All advice is confidential and is delivered within three working days of a request. {term}",
            ]),
            "travel" => ("Travel agency agreement", "finance",
            [
                $"{vendor} books flights, hotels and rail tickets for business trips at the negotiated corporate rates, with an emergency line for travellers open around the clock.",
                $"Bookings above 1,500 euros need the approval of the traveller's manager, and {vendor} charges a service fee of 25 euros per booking, plus {euros} euros per month for the emergency line. {term}",
            ]),
            "security" => ("Security guarding contract", "security",
            [
                $"{vendor} provides security guards at the main entrance and the car park twenty-four hours a day, with a patrol of all floors every two hours at night and a written report of any incident within 24 hours.",
                $"The fee is {euros} euros per month. Guards must hold the licence required by law and complete the company's safety induction. {term}",
            ]),
            "recruitment" => ("Recruitment agreement", "hr",
            [
                $"{vendor} presents candidates for the positions the HR department opens, with a shortlist within ten working days and a screening interview already done for each candidate.",
                $"The fee is 18% of the first year's gross salary of the person hired, with a replacement at no cost if the person leaves within six months. A retainer of {euros} euros per year covers priority handling. {term}",
            ]),
            "software" => ("Software licence agreement", "it",
            [
                $"{vendor} grants the company a non-exclusive licence for its software for up to 500 users, including updates, a support line on working days and a response within four hours for critical defects.",
                $"The licence costs {euros} euros per month for the agreed number of users, adjusted once a year with the consumer price index. {term} The company keeps the right to export its data at any time.",
            ]),
            "marketing" => ("Marketing services agreement", "marketing",
            [
                $"{vendor} designs and runs the marketing campaigns agreed each quarter, including the creative material, the media plan and a monthly report on the results.",
                $"The fee is {euros} euros per month plus the media costs, which the company approves in advance. All the material produced is owned by the company. {term}",
            ]),
            "training" => ("Training agreement", "hr",
            [
                $"{vendor} delivers training courses for employees on safety, leadership and software tools, in person or online, in groups of up to 20 people, with a certificate of attendance.",
                $"The price is {euros} euros per month for the agreed programme, with extra sessions on request. {term}",
            ]),
            "logistics" => ("Logistics agreement", "operations",
            [
                $"{vendor} collects and delivers parcels and pallets between the sites of the company and its customers, with delivery within two working days in Europe and tracking of every shipment.",
                $"The fee is {euros} euros per month plus a rate per shipment, and goods that are damaged or lost are refunded at their declared value. {term}",
            ]),
            "audit" => ("Audit engagement", "finance",
            [
                $"{vendor} audits the annual accounts of the company and reports its findings to the management and the board, with an interim review each half year.",
                $"The fee is {euros} euros per year, paid in four instalments. The auditor must stay independent and cannot supply other consulting services to the company. {term}",
            ]),
            "insurance" => ("Insurance policy", "finance",
            [
                $"{vendor} insures the offices and their contents against fire, flood and theft, and covers the liability of the company towards third parties up to 5 million euros.",
                $"The premium is {euros} euros per month. Claims must be reported within five days and are settled within 30 days of the documents being complete. {term}",
            ]),
            "office" => ("Office supplies agreement", "procurement",
            [
                $"{vendor} supplies paper, stationery and small office equipment from a catalogue, with delivery within three working days and free return of unused goods within 14 days.",
                $"Prices follow the catalogue with a 12% discount for the company, and the minimum order is 150 euros. The expected spending is {euros} euros per month. {term}",
            ]),
            "research" => ("Research partnership", "operations",
            [
                $"{vendor} carries out joint research projects with the engineers of the company on new products, sharing its laboratories and a team of up to six researchers.",
                $"The company funds the projects with {euros} euros per month. Results and patents belong to both parties in equal parts, and either party can use them freely inside its own business. {term}",
            ]),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown kind of contract."),
        };
    }
}
