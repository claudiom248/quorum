using System.Globalization;
using System.Text.Json.Nodes;

namespace Quorum.SampleKnowledgeBase;

/// <summary>
/// The people and the rules of the same company as <see cref="CompanySampleData"/>, for the PostgreSQL knowledge bases: a handbook of
/// policies and a directory of employees. They are not contracts, but they share its vocabulary on purpose: the same ten departments,
/// the countries of the vendors (IT, US, DE, UK, CH, FR, JP, ES, NL), the same audiences, and the names of the vendors the employees
/// who deal with them are in touch with, so a question can go from one domain to the other. Everything is deterministic.
/// </summary>
public static class PeopleSampleData
{
    /// <summary>The departments, the countries and the audiences the contracts use: the references the two domains share.</summary>
    public static IReadOnlyList<string> Departments { get; } = ["facilities", "finance", "hr", "it", "legal", "marketing", "operations", "procurement", "research", "security"];

    public static IReadOnlyList<string> Countries { get; } = ["CH", "DE", "ES", "FR", "IT", "JP", "NL", "UK", "US"];

    public sealed record Policy(
        string Id,
        string Title,
        string Department,
        string[] Audience,
        string? Country,
        string Status,
        int Year,
        string EffectiveOn,
        bool Confidential,
        string[] Sections);

    public sealed record Employee(
        long Id,
        string Name,
        string Department,
        string Country,
        string Role,
        string Level,
        string HiredOn,
        bool Remote,
        string[] Skills,
        string[] VendorContacts)
    {
        public string Description =>
            $"{Name} is a {Level} {Role} in {Department}, based in {Country}, working {(Remote ? "remotely" : "on site")} since {HiredOn}."
            + (VendorContacts.Length > 0 ? $" Day-to-day contact for {string.Join(", ", VendorContacts)}." : string.Empty);
    }

    private static readonly string[] All = ["all"];
    private static readonly string[] AllNewHires = ["all", "new_hires"];
    private static readonly string[] NewHires = ["new_hires", "hr", "managers"];
    private static readonly string[] AllIt = ["all", "it"];
    private static readonly string[] ItSecurity = ["it", "security"];
    private static readonly string[] Procurement = ["procurement", "managers"];
    private static readonly string[] Thresholds = ["procurement", "managers", "executives", "finance"];
    private static readonly string[] Marketing = ["marketing"];
    private static readonly string[] SecurityTeam = ["security", "it", "managers"];
    private static readonly string[] AllFacilities = ["all", "facilities"];
    private static readonly string[] Research = ["research"];
    private static readonly string[] AllManagers = ["all", "managers"];
    private static readonly string[] Export = ["executives", "legal", "research"];

    /// <summary>Eighteen policies of two sections each, for the handbook: some for everybody, some for an audience, some for one country, in three states.</summary>
    public static IReadOnlyList<Policy> Policies { get; } =
    [
        new("remote-work", "Remote work policy", "hr", All, null, "current", 2024, "2024-02-01", false,
            ["Employees may work remotely up to three days per week after agreement with their manager. Remote workers must be reachable during core hours.",
             "The company provides a laptop and a monthly allowance for the home office. Equipment must be returned when the contract ends."]),
        new("expenses", "Expense reimbursement", "finance", All, null, "current", 2023, "2023-03-15", false,
            ["Expenses are reimbursed within thirty days with the receipts attached. Meals during business travel are reimbursed up to forty euros per day.",
             "Expenses above five hundred euros need the approval of the line manager before they are incurred."]),
        new("security-awareness", "Security awareness training", "security", AllNewHires, null, "current", 2024, "2024-01-10", false,
            ["Every employee completes the security awareness course within the first month and then once a year. Phishing simulations run every quarter.",
             "Lost or stolen devices must be reported to the security desk within one hour. Passwords are never shared and are changed when a breach is suspected."]),
        new("onboarding", "New hire onboarding", "hr", NewHires, null, "current", 2025, "2025-02-03", false,
            ["New employees receive a laptop, a badge and their accounts on the first day. A buddy introduces the team and the tools during the first week.",
             "Managers agree three objectives with each new hire in the first month and review them at the end of the probation period."]),
        new("code-of-conduct", "Code of conduct", "legal", All, null, "current", 2022, "2022-06-01", false,
            ["Employees act with integrity, respect their colleagues and avoid conflicts of interest. Gifts from vendors above fifty euros are declined or reported.",
             "Concerns can be reported anonymously through the ethics line. Retaliation against someone who reports in good faith is a breach of the code."]),
        new("personal-data", "Personal data handling", "legal", AllIt, "DE", "current", 2024, "2024-05-25", true,
            ["Personal data of customers and employees in Germany is processed only for the purpose it was collected for and kept no longer than needed. Requests of data subjects are answered within one month.",
             "Systems that hold personal data are listed in the register of processing activities, which the data protection officer reviews every year."]),
        new("leave-it", "Leave rules in Italy", "hr", All, "IT", "current", 2024, "2024-01-15", false,
            ["Employees in Italy accrue twenty-six days of paid annual leave per year. Leave requests must be approved by the line manager at least two weeks in advance.",
             "Unused leave can be carried over up to five days and expires on the thirty-first of March."]),
        new("leave-us", "Time off in the United States", "hr", All, "US", "current", 2023, "2023-01-02", false,
            ["Employees in the United States have twenty days of paid time off and ten public holidays. Time off is requested through the HR portal.",
             "Parental leave is twelve weeks, paid at full salary for the first six."]),
        new("leave-uk", "Annual leave in the UK", "hr", All, "UK", "current", 2024, "2024-04-01", false,
            ["Employees in the United Kingdom have twenty-eight days of paid leave including bank holidays. Leave years run from April to March.",
             "Sick leave is paid in full for the first ten days of each year with a self-certification."]),
        new("datacenter-access", "Data center access", "it", ItSecurity, null, "current", 2024, "2024-03-10", true,
            ["Access to the data centers is granted by badge to the named staff of IT and security and logged. Visitors are always escorted.",
             "Maintenance by vendors is scheduled in advance and supervised. Removable media never leave the data center."]),
        new("vendor-onboarding", "Vendor onboarding checklist", "procurement", Procurement, null, "current", 2023, "2023-09-01", false,
            ["A new vendor is registered after a due diligence on its finances, its security posture and its references. Procurement keeps the file for the life of the contract.",
             "Vendors from countries with export restrictions need the review of legal before the first order."]),
        new("purchasing-thresholds", "Purchasing approval thresholds", "procurement", Thresholds, null, "current", 2024, "2024-07-01", false,
            ["Purchases up to ten thousand euros are approved by the budget owner, up to one hundred thousand by the head of department, and above that by the executive committee.",
             "Three quotes are required above twenty thousand euros unless the vendor is on the preferred list."]),
        new("brand-guidelines", "Brand and media guidelines", "marketing", Marketing, null, "retired", 2022, "2022-02-14", false,
            ["The logo is used only in the approved colors and never stretched. Press requests are forwarded to the communications team.",
             "Social media accounts of the company are managed by marketing; employees speak for themselves and not for the company."]),
        new("incident-response", "Security incident response", "security", SecurityTeam, null, "current", 2025, "2025-01-20", true,
            ["A security incident is declared by the security desk and led by an incident manager. Affected systems are isolated before they are investigated.",
             "Customers and authorities are notified within the deadlines of the law, with the text approved by legal."]),
        new("workplace-safety", "Workplace safety and evacuation", "facilities", AllFacilities, null, "current", 2023, "2023-04-12", false,
            ["Evacuation routes are marked on every floor and tested twice a year. Visitors must be registered at the reception and wear a badge.",
             "Accidents, however small, are reported to facilities the same day."]),
        new("lab-safety", "Laboratory safety rules", "research", Research, "NL", "current", 2024, "2024-09-02", false,
            ["Everyone entering the laboratory in the Netherlands wears protective equipment and has completed the safety briefing. Chemicals are stored and labeled by class.",
             "Waste is sorted at the source and collected by the authorized contractor every week."]),
        new("business-travel", "Business travel policy", "operations", AllManagers, null, "draft", 2025, "2025-06-01", false,
            ["Flights are booked in economy class for journeys shorter than six hours. Trains are preferred when the journey takes less than four.",
             "Managers approve trips in advance and travelers register their itinerary with the travel agency."]),
        new("export-controls", "Export controls", "legal", Export, "US", "current", 2024, "2024-11-04", true,
            ["Research results, software and hardware are classified before they leave the company. Exports to restricted countries need a license.",
             "Executives sign the annual export compliance statement and legal keeps the register of licenses."]),
    ];

    // The thirty-six employees written by hand.
    private static readonly Employee[] Directory =
    [
        new(1, "Giulia Rossi", "hr", "IT", "HR business partner", "senior", "2019-04-01", false, ["recruiting", "labour law"], []),
        new(2, "Marco Bianchi", "it", "IT", "Systems engineer", "senior", "2018-09-17", false, ["linux", "networking"], ["Acme", "Hooli"]),
        new(3, "Anna Schmidt", "legal", "DE", "Counsel", "lead", "2017-02-06", false, ["contracts", "data protection"], ["Initech", "Massive Dynamic"]),
        new(4, "Lukas Müller", "it", "DE", "Platform engineer", "mid", "2021-05-03", true, ["kubernetes", "cloud"], ["Globex"]),
        new(5, "Emily Carter", "finance", "US", "Financial controller", "lead", "2016-11-14", false, ["budgeting", "audit"], ["Gringotts", "Dunder Mifflin"]),
        new(6, "James Walker", "procurement", "UK", "Category manager", "senior", "2018-01-22", false, ["negotiation", "sourcing"], ["Umbrella", "Gringotts", "Wonka"]),
        new(7, "Sophie Martin", "marketing", "FR", "Brand manager", "mid", "2022-03-07", true, ["branding", "events"], []),
        new(8, "Hiro Tanaka", "research", "JP", "Research scientist", "senior", "2019-10-01", false, ["robotics", "machine learning"], ["Tyrell", "Cyberdyne"]),
        new(9, "Elena García", "operations", "ES", "Logistics coordinator", "mid", "2020-06-15", false, ["logistics", "planning"], ["Vandelay"]),
        new(10, "Pieter de Vries", "research", "NL", "Laboratory manager", "lead", "2017-08-28", false, ["chemistry", "safety"], ["Aperture"]),
        new(11, "Chiara Ferrari", "finance", "IT", "Accountant", "junior", "2024-01-08", false, ["accounting", "excel"], []),
        new(12, "Daniel Brown", "security", "US", "Security analyst", "mid", "2021-09-20", true, ["incident response", "forensics"], ["Cyberdyne", "Umbrella"]),
        new(13, "Laura Keller", "facilities", "CH", "Facility manager", "senior", "2015-05-11", false, ["maintenance", "safety"], ["Acme", "Wonka"]),
        new(14, "Tomás Alvarez", "operations", "ES", "Operations manager", "lead", "2016-02-01", false, ["planning", "lean"], []),
        new(15, "Nina Fischer", "hr", "DE", "Recruiter", "junior", "2023-09-04", true, ["recruiting", "interviewing"], []),
        new(16, "Oliver Smith", "it", "UK", "Security engineer", "senior", "2019-03-18", true, ["cloud", "iam"], ["Umbrella", "Stark"]),
        new(17, "Camille Dubois", "legal", "FR", "Paralegal", "junior", "2024-05-13", false, ["contracts", "research"], ["Soylent"]),
        new(18, "Matteo Conti", "procurement", "IT", "Buyer", "mid", "2020-11-02", false, ["sourcing", "tendering"], ["Acme", "Pied Piper", "Stark"]),
        new(19, "Hannah Wagner", "marketing", "DE", "Content strategist", "senior", "2018-07-09", true, ["writing", "seo"], []),
        new(20, "Ryan Johnson", "research", "US", "Data scientist", "senior", "2020-02-17", true, ["statistics", "machine learning"], ["Oscorp", "Massive Dynamic"]),
        new(21, "Yuki Sato", "finance", "JP", "Financial analyst", "mid", "2022-10-03", false, ["forecasting", "reporting"], ["Tyrell"]),
        new(22, "Isabel Moreno", "security", "ES", "Security engineer", "mid", "2022-01-24", false, ["network security", "audit"], []),
        new(23, "Jan Bakker", "it", "NL", "Support engineer", "junior", "2024-03-04", false, ["helpdesk", "windows"], ["Hooli"]),
        new(24, "Grace Taylor", "hr", "UK", "HR director", "lead", "2016-06-06", false, ["labour law", "compensation"], []),
        new(25, "Paolo Greco", "facilities", "IT", "Safety officer", "senior", "2017-10-23", false, ["safety", "compliance"], ["Acme"]),
        new(26, "Mia Hoffmann", "marketing", "CH", "Marketing analyst", "junior", "2023-06-12", true, ["analytics", "sql"], []),
        new(27, "Samuel Green", "operations", "US", "Supply planner", "mid", "2021-04-26", true, ["planning", "sql"], ["Dunder Mifflin", "Vandelay"]),
        new(28, "Lea Moreau", "procurement", "FR", "Contract manager", "senior", "2019-09-02", false, ["contracts", "negotiation"], ["Soylent", "Initech"]),
        new(29, "Kenji Watanabe", "it", "JP", "Software engineer", "mid", "2021-11-08", true, ["dotnet", "cloud"], ["Pied Piper"]),
        new(30, "Sara Lindqvist", "legal", "NL", "Compliance officer", "senior", "2018-04-30", false, ["export controls", "data protection"], ["Aperture", "Oscorp"]),
        new(31, "Federico Neri", "security", "IT", "Head of security", "lead", "2016-09-12", false, ["risk", "incident response"], ["Cyberdyne", "Umbrella"]),
        new(32, "Charlotte Evans", "finance", "UK", "Treasury analyst", "senior", "2017-12-04", true, ["treasury", "forecasting"], ["Gringotts"]),
        new(33, "Diego Fernández", "research", "ES", "Research engineer", "mid", "2023-02-20", false, ["robotics", "python"], []),
        new(34, "Eva Novak", "operations", "DE", "Process analyst", "junior", "2024-09-02", true, ["lean", "sql"], []),
        new(35, "Brian O'Connor", "it", "US", "IT director", "lead", "2015-03-02", false, ["architecture", "vendor management"], ["Globex", "Stark", "Hooli"]),
        new(36, "Martina Keller", "facilities", "DE", "Workplace coordinator", "mid", "2022-08-15", false, ["events", "safety"], []),
    ];


    /// <summary>
    /// Four hundred and twenty employees across the ten departments and the nine countries (36 written by hand, the rest generated), with
    /// their skills, and for those who deal with vendors the names of the vendors of <see cref="CompanySampleData"/> they are in touch with.
    /// </summary>
    public static IReadOnlyList<Employee> Employees => AllEmployees.Value;

    // Lazy because the generator reads tables that are written below: static fields are initialized in the order they are written.
    private static readonly Lazy<IReadOnlyList<Employee>> AllEmployees = new(() => [.. Directory, .. GeneratedEmployees(Directory)]);

    private const int GeneratedEmployeeCount = 384;

    // Names by country, eight first names and eight surnames each: sixty-four people a country, none repeated.
    private static readonly Dictionary<string, (string[] First, string[] Last)> Names = new()
    {
        ["IT"] = (["Luca", "Sofia", "Alessandro", "Francesca", "Davide", "Elisa", "Simone", "Valentina"], ["Romano", "Colombo", "Ricci", "Marino", "Bruno", "Gallo", "Costa", "Fontana"]),
        ["US"] = (["Michael", "Jessica", "Christopher", "Ashley", "Matthew", "Amanda", "Joshua", "Megan"], ["Miller", "Davis", "Wilson", "Anderson", "Thomas", "Moore", "Jackson", "Harris"]),
        ["DE"] = (["Felix", "Lena", "Jonas", "Marie", "Maximilian", "Tobias", "Julia", "Stefan"], ["Weber", "Becker", "Schulz", "Koch", "Richter", "Wolf", "Neumann", "Braun"]),
        ["UK"] = (["Harry", "Amelia", "George", "Olivia", "Jack", "Isla", "Thomas", "Poppy"], ["Clarke", "Bennett", "Hughes", "Edwards", "Turner", "Parker", "Cooper", "Morgan"]),
        ["FR"] = (["Lucas", "Chloé", "Hugo", "Emma", "Louis", "Manon", "Jules", "Inès"], ["Bernard", "Petit", "Robert", "Richard", "Durand", "Leroy", "Fournier", "Girard"]),
        ["ES"] = (["Pablo", "Lucía", "Javier", "Carmen", "Sergio", "Marta", "Álvaro", "Paula"], ["Ruiz", "Díaz", "Hernández", "Muñoz", "Jiménez", "Navarro", "Torres", "Romero"]),
        ["NL"] = (["Daan", "Sanne", "Bram", "Eva", "Thijs", "Lotte", "Sem", "Fleur"], ["Jansen", "Visser", "Smit", "Meijer", "Mulder", "Bos", "Vos", "Peters"]),
        ["JP"] = (["Haruto", "Yui", "Sota", "Hina", "Ren", "Mio", "Takumi", "Aoi"], ["Suzuki", "Takahashi", "Ito", "Yamamoto", "Nakamura", "Kobayashi", "Kato", "Yoshida"]),
        ["CH"] = (["Noah", "Mia", "Elias", "Lara", "Luca", "Nina", "Jonas", "Sara"], ["Meier", "Keller", "Frei", "Steiner", "Gerber", "Brunner", "Baumann", "Huber"]),
    };

    private static readonly Dictionary<string, (string[] Roles, string[] Skills)> Jobs = new()
    {
        ["facilities"] = (["Facility technician", "Maintenance planner", "Space planner", "Safety coordinator", "Reception lead"], ["maintenance", "safety", "events", "space planning", "compliance", "contracts", "energy", "cleaning"]),
        ["finance"] = (["Accountant", "Financial analyst", "Controller", "Payroll specialist", "Tax specialist"], ["accounting", "reporting", "forecasting", "audit", "excel", "treasury", "tax", "budgeting"]),
        ["hr"] = (["Recruiter", "HR business partner", "Compensation analyst", "Learning specialist", "HR assistant"], ["recruiting", "labour law", "interviewing", "compensation", "training", "onboarding", "coaching", "payroll"]),
        ["it"] = (["Software engineer", "Systems engineer", "Support engineer", "Data engineer", "Platform engineer"], ["dotnet", "cloud", "linux", "kubernetes", "networking", "sql", "helpdesk", "iam"]),
        ["legal"] = (["Counsel", "Paralegal", "Compliance officer", "Contract specialist", "Privacy officer"], ["contracts", "data protection", "export controls", "litigation", "research", "compliance", "negotiation", "ip"]),
        ["marketing"] = (["Brand manager", "Content strategist", "Marketing analyst", "Designer", "Events manager"], ["branding", "writing", "seo", "analytics", "events", "design", "social media", "sql"]),
        ["operations"] = (["Logistics coordinator", "Supply planner", "Process analyst", "Operations manager", "Quality controller"], ["logistics", "planning", "lean", "sql", "quality", "procurement", "warehouse", "forecasting"]),
        ["procurement"] = (["Buyer", "Category manager", "Contract manager", "Sourcing specialist", "Vendor manager"], ["sourcing", "negotiation", "tendering", "contracts", "vendor management", "category management", "excel", "risk"]),
        ["research"] = (["Research scientist", "Data scientist", "Research engineer", "Laboratory technician", "Research manager"], ["machine learning", "statistics", "robotics", "python", "chemistry", "safety", "simulation", "writing"]),
        ["security"] = (["Security analyst", "Security engineer", "Incident responder", "Risk analyst", "Security architect"], ["incident response", "forensics", "network security", "audit", "risk", "iam", "cloud", "pentesting"]),
    };

    // The vendors of the contracts, for the employees who deal with them.
    private static readonly string[] VendorNames =
    [
        "Acme", "Globex", "Initech", "Hooli", "Umbrella", "Stark", "Wayne", "Wonka", "Soylent", "Tyrell", "Cyberdyne", "Gringotts", "Oscorp",
        "Vandelay", "Pied Piper", "Massive Dynamic", "Aperture", "Dunder Mifflin",
    ];

    private static readonly string[] Levels = ["junior", "mid", "mid", "mid", "senior", "senior", "senior", "lead", "mid", "senior"];

    /// <summary>
    /// 384 more employees, deterministic: spread over the ten departments and the nine countries, each with a name nobody else has, a date of
    /// hiring nobody else shares (so an order by it is the same everywhere), and for the departments that deal with vendors the vendors they
    /// are in touch with.
    /// </summary>
    private static List<Employee> GeneratedEmployees(Employee[] handwritten)
    {
        var taken = handwritten.Select(e => e.HiredOn).ToHashSet(StringComparer.Ordinal);
        var names = handwritten.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        var countries = Names.Keys.ToArray();
        var perCountry = countries.ToDictionary(country => country, _ => 0, StringComparer.Ordinal);
        var date = new DateOnly(2015, 1, 5);
        var generated = new List<Employee>(GeneratedEmployeeCount);
        for (var i = 0; i < GeneratedEmployeeCount; i++)
        {
            var country = countries[(i * 4) % countries.Length == 0 ? i % countries.Length : (i * 4) % countries.Length];
            var k = perCountry[country]++;
            var (first, last) = Names[country];
            var name = $"{first[k % 8]} {last[(k / 8) % 8]}";
            if (!names.Add(name))
            {
                name += $" {(char)('A' + (i % 26))}.";
                names.Add(name);
            }

            var department = Departments[((i * 3) + (i / 9)) % Departments.Count];
            var (roles, skills) = Jobs[department];
            while (!taken.Add(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
            {
                date = date.AddDays(1);
            }

            var hired = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            date = date.AddDays(9);
            string[] vendors = department is "procurement" or "it" or "legal" or "finance" or "security" or "operations" or "research" or "facilities"
                ? [.. Enumerable.Range(0, 1 + (i % 3)).Select(step => VendorNames[((i * 5) + (step * 7)) % VendorNames.Length]).Distinct()]
                : [];
            generated.Add(new Employee(
                37 + i,
                name,
                department,
                country,
                roles[i % roles.Length],
                Levels[i % Levels.Length],
                hired,
                i % 3 == 0,
                [.. new[] { skills[i % skills.Length], skills[((i * 3) + 1) % skills.Length] }.Distinct()],
                vendors));
        }

        return generated;
    }

    /// <summary>The chunks of the handbook, one per section, with the first of each policy marked.</summary>
    internal static IEnumerable<(Policy Policy, int Chunk, string Text, long Id)> Chunks()
    {
        long id = 1;
        foreach (var policy in Policies)
        {
            for (var chunk = 0; chunk < policy.Sections.Length; chunk++)
            {
                yield return (policy, chunk, policy.Sections[chunk], id++);
            }
        }
    }

    internal static string UrlOf(Policy policy) => SampleDocuments.Url("handbook", policy.Id);

    internal static string UrlOf(Employee employee) => SampleDocuments.Url("people", employee.Id.ToString(CultureInfo.InvariantCulture));

    /// <summary>The metadata of a chunk as one JSON object, the way a LangChain-style table keeps it.</summary>
    internal static JsonObject MetadataOf(Policy policy, int chunk) => new()
    {
        ["document_id"] = policy.Id,
        ["title"] = policy.Title,
        ["department"] = policy.Department,
        ["audience"] = new JsonArray(policy.Audience.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
        ["country"] = policy.Country,
        ["status"] = policy.Status,
        ["year"] = policy.Year,
        ["effective_on"] = policy.EffectiveOn,
        ["confidential"] = policy.Confidential,
        ["is_first_chunk"] = chunk == 0,
        ["chunk_index"] = chunk,
        ["source_url"] = UrlOf(policy),
    };

    internal static DateOnly Date(string text) => DateOnly.Parse(text, CultureInfo.InvariantCulture);
}
