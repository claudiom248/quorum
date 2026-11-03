namespace Quorum.SampleKnowledgeBase;

/// <summary>
/// The company the contracts of <see cref="CompanySampleData"/> are about: its sites, its kinds of contract and the pools of clauses
/// the writers pick from. The facts are deliberately interlocked with the rest of the samples: a kind is supplied only by the vendors
/// whose category fits it, it lives in sites of a type that can host it, it is owned by people of <see cref="PeopleSampleData"/> and it
/// is governed by policies of the same handbook, so a question can leave the contracts and reach the vendors, the employees or the rules.
/// </summary>
public static class CompanyFacts
{
    public sealed record Vendor(string Name, string Country, string Category, string Since, double Rating, bool Preferred);

    /// <summary>Forty-two vendors, each with the category that decides which kinds of contract it can hold.</summary>
    public static IReadOnlyList<Vendor> Vendors { get; } =
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
        new("Nakatomi", "JP", "finance", "2019-02-11", 4.5, true),
        new("Weyland", "UK", "research", "2017-06-19", 4.2, true),
        new("Dharma", "US", "logistics", "2018-09-24", 3.4, false),
        new("Buy n Large", "US", "office supplies", "2021-01-18", 3.1, false),
        new("Prestige Worldwide", "NL", "logistics", "2022-03-21", 3.9, false),
        new("Sterling Cooper", "US", "marketing", "2016-10-03", 4.0, true),
        new("Bluth Company", "US", "facilities", "2020-05-25", 2.9, false),
        new("Los Pollos", "ES", "catering", "2019-11-04", 4.4, true),
        new("Paper Street", "IT", "cleaning", "2023-04-03", 3.6, false),
        new("Sirius Cybernetics", "UK", "software", "2018-02-12", 3.3, false),
        new("Zorg Industries", "DE", "security", "2020-08-17", 4.1, true),
        new("Spacely Sprockets", "US", "hardware", "2021-09-27", 3.8, false),
        new("Cogswell Cogs", "US", "hardware", "2022-12-05", 3.5, false),
        new("Planet Express", "US", "logistics", "2019-07-15", 4.3, true),
        new("Hanso Foundation", "CH", "research", "2017-03-06", 4.6, true),
        new("Rekall", "FR", "training", "2022-06-13", 3.7, false),
        new("Virtucon", "DE", "legal", "2018-12-03", 4.2, true),
        new("Praxis", "IT", "recruitment", "2021-11-22", 3.9, false),
        new("Genco", "IT", "insurance", "2016-01-25", 4.7, true),
        new("Wernham Hogg", "UK", "recruitment", "2019-10-14", 3.2, false),
        new("Bubba Gump", "US", "catering", "2023-08-28", 3.6, false),
        new("Vehement Capital", "UK", "audit", "2020-02-24", 4.1, true),
        new("Duff", "NL", "catering", "2020-07-06", 3.8, false),
        new("Pearson Hardman", "US", "legal", "2017-09-11", 4.4, true),
    ];

    public static Vendor VendorOf(string name) => Vendors.Single(vendor => vendor.Name == name);

    /// <summary>The vendors whose category can hold a kind of contract; a vendor never appears outside its category.</summary>
    public static Vendor[] VendorsFor(ContractKind kind) =>
        [.. Vendors.Where(vendor => kind.VendorCategories.Contains(vendor.Category, StringComparer.Ordinal)).OrderBy(vendor => vendor.Name, StringComparer.Ordinal)];

    /// <param name="Kind">office, campus, laboratory, warehouse, plant, data_center: what a contract of a given kind can be about.</param>
    /// <param name="Note">One true thing about the site, quoted by the contracts that work there.</param>
    public sealed record Site(
        string Id, string Name, string City, string Country, string Kind,
        int Floors, int AreaSqm, int Desks, string Opened, string Note);

    /// <param name="FeeBasis">monthly, annual or per_work_order: how the money of this kind of contract is reckoned.</param>
    /// <param name="VendorCategories">the categories of vendor that can hold this kind; a vendor never appears outside them.</param>
    /// <param name="SiteKinds">the kinds of site this kind of contract can be performed in.</param>
    /// <param name="PolicyIds">handbook policies the contract points at, from <see cref="PeopleSampleData.Policies"/>.</param>
    public sealed record ContractKind(
        string Id, string Title, string Department, string[] VendorCategories, string[] SiteKinds,
        string FeeBasis, int BaseFee, int MinTermMonths, int MaxTermMonths, bool ConfidentialByDefault,
        string[] AudiencePool, string[] PolicyIds,
        string[] Deliverables, string[] ServiceLevels, string[] SpecificClauses, string[] ExitClauses);

    public static IReadOnlyList<Site> Sites { get; } =
    [
        new("milan-hq", "Milan headquarters", "Milan", "IT", "campus", 8, 12400, 1150, "2016-04-11", "two connected buildings around a planted courtyard, with the group's boardroom on the top floor"),
        new("milan-lab", "Milan prototype laboratory", "Milan", "IT", "laboratory", 2, 900, 55, "2021-09-06", "a cleanroom assembly bay and three test benches, opened to the university groups once a quarter"),
        new("rome-office", "Rome office", "Rome", "IT", "office", 3, 2100, 190, "2019-02-18", "the public-sector team, in a rented floor of a building from the 1930s with lifts that are under maintenance"),
        new("turin-plant", "Turin plant and depot", "Turin", "IT", "plant", 1, 6800, 60, "2014-06-30", "the Italian production and distribution site, with four loading docks and a fenced yard"),
        new("amsterdam-office", "Amsterdam office", "Amsterdam", "NL", "office", 4, 3200, 280, "2018-01-08", "the European finance hub, in a canal building without its own car park"),
        new("rotterdam-hub", "Rotterdam hub", "Rotterdam", "NL", "warehouse", 1, 5100, 40, "2020-10-12", "the cross-dock for the Benelux and the Nordic orders, two minutes from the ring road"),
        new("munich-office", "Munich office", "Munich", "DE", "office", 5, 4100, 360, "2015-03-23", "the German engineering team and the works council room"),
        new("berlin-lab", "Berlin laboratory", "Berlin", "DE", "laboratory", 3, 1400, 95, "2017-08-14", "materials and electronics testing, with fume cupboards on the second floor and gas cylinders in a cage outside"),
        new("hamburg-office", "Hamburg office", "Hamburg", "DE", "office", 2, 1100, 90, "2022-05-02", "the shipping and customs desk near the port, shared with two other companies"),
        new("london-office", "London office", "London", "UK", "office", 6, 4800, 420, "2013-11-04", "the largest European floor outside Italy, with the trading desk on the third"),
        new("manchester-service-centre", "Manchester service centre", "Manchester", "UK", "office", 2, 1600, 140, "2021-06-21", "the customer support shift floor, open from six in the morning to ten at night"),
        new("paris-office", "Paris office", "Paris", "FR", "office", 4, 2900, 250, "2018-09-17", "the Southern Europe sales team and the translation desk"),
        new("lyon-lab", "Lyon laboratory", "Lyon", "FR", "laboratory", 2, 1200, 70, "2020-02-03", "chemical analysis and stability chambers, run with a French research institute"),
        new("zurich-office", "Zurich office", "Zurich", "CH", "office", 3, 2200, 175, "2016-07-19", "the treasury and the insurance register of the group"),
        new("bern-datacenter", "Bern data centre", "Bern", "CH", "data_center", 1, 800, 12, "2019-12-09", "the primary data centre of the group, in a converted bunker with N+1 power and its own fire-suppression loop"),
        new("newyork-office", "New York office", "New York", "US", "office", 9, 7300, 610, "2012-10-01", "the North American headquarters, one floor of a tower with a single goods lift shared with the landlord"),
        new("chicago-office", "Chicago office", "Chicago", "US", "office", 4, 3400, 300, "2017-04-24", "the Midwest sales and service team, in a Class A building with a controlled lobby"),
        new("austin-campus", "Austin campus", "Austin", "US", "campus", 5, 9200, 700, "2022-08-15", "the newest site of the group: three low buildings, a data hall and a large car park with charging points"),
        new("tokyo-office", "Tokyo office", "Tokyo", "JP", "office", 7, 5100, 430, "2015-01-19", "the Asia-Pacific hub, with the Japanese support desk and a guest floor for visitors from the group"),
        new("osaka-lab", "Osaka laboratory", "Osaka", "JP", "laboratory", 2, 1000, 65, "2023-03-27", "the Asia-Pacific test and certification laboratory, accredited for electrical safety"),
        new("madrid-office", "Madrid office", "Madrid", "ES", "office", 3, 2400, 210, "2019-11-11", "the Iberian sales team and the shared service desk for Spanish-speaking countries"),
        new("barcelona-hub", "Barcelona hub", "Barcelona", "ES", "warehouse", 1, 4300, 35, "2021-02-08", "the Iberian and Mediterranean stock, on two temperature-controlled racks and an open yard"),
    ];

    public static Site SiteOf(string id) => Sites.Single(site => site.Id == id);

    /// <summary>Audience tokens shared with the handbook: the lists a lookup can compute over both domains.</summary>
    public static IReadOnlyList<string> Audiences { get; } =
        ["all", "it", "managers", "hr", "finance", "legal", "security", "facilities", "executives", "new_hires", "procurement", "marketing", "operations", "research"];

    public static IReadOnlyList<ContractKind> Kinds { get; } =
    [
        new("cleaning", "Cleaning services contract", "facilities", ["facilities", "cleaning"], ["office", "campus", "laboratory", "data_center", "plant", "warehouse"],
            "monthly", 4600, 12, 48, false, ["all", "facilities", "managers"], ["workplace-safety", "code-of-conduct"],
            [
                "daily cleaning of the @FLOORS@ floors of @S@ before the opening hours and a weekly deep clean of the kitchens and the restrooms",
                "cleaning of the working areas of @S@ five days a week, with the meeting rooms and the phone booths done twice a day",
                "the cleaning of @S@ (@AREA@ square metres on @FLOORS@ floors) with a night shift from @H@ onwards and a supervisor on site",
                "floor and textile care at @S@, including the shampoo of the carpets once a quarter and the polishing of the stone in the lobby",
                "cleaning at @S@ plus the windows of the whole facade, inside and outside, once every @M@ months with a rope access team",
                "the sanitisation of restrooms, kitchens and shared equipment at @S@ with the products on the approved list of the group",
                "cleaning of the production areas of @S@ outside the working shifts, with the machines covered and the waste taken out of the line",
            ],
            [
                "a missed service is redone within @H@ hours at no cost and costs @VENDOR@ a service credit of @PCT@% of the monthly fee after the third one in a quarter",
                "the monthly quality inspection scores @S@ on cleanliness with photographs of the sampled rooms; below @SCORE@/100 the fee of the month is cut by @PCT@%",
                "attendance is proved by the badge reads of the cleaning staff, checked @Q@ by the facility manager on site",
                "the supervisor reports incidents and shortages of consumables the same day, and the group's dashboard shows the open items in @H@ hours",
            ],
            [
                "consumables, machines and the products used are supplied by @VENDOR@, which keeps their safety data sheets at @S@",
                "the waste sorted at the source is taken to the@H2@ collection points by the cleaning team, in line with the sorting rules of @C@",
                "keys and badges for @S@ are registered, never copied, and returned at the end of every shift",
                "the staff on @S@ are the employees of @VENDOR@ named in the annex; substitutions are notified before the shift starts",
            ],
            [
                "the outgoing cleaner hands over the keys, the alarm codes and the register of the products at the end of the term, and the incoming one gets a week to walk the floors first",
                "the machines used at @S@ belong to @VENDOR@ and leave the building with them, except the industrial dishwasher installed in @Y@",
            ]),

        new("waste-and-recycling", "Waste collection and recycling agreement", "facilities", ["facilities", "cleaning", "logistics"], ["office", "campus", "plant", "warehouse", "laboratory"],
            "monthly", 2300, 12, 36, false, ["all", "facilities"], ["workplace-safety", "code-of-conduct"],
            [
                "collection of the mixed and sorted waste of @S@ @M@ times a week, with the paper and cardboard taken from the loading area",
                "the recycling of paper, plastic, glass, metal and WEEE from @S@, with a certificate of correct treatment for every load",
                "the collection of the used coffee grounds and organic waste from the canteen of @S@, twice a week, in sealed containers",
                "removal of bulky waste and of the furniture taken out of the floors of @S@, on request within @D@ days",
                "the hazardous fraction of @S@ is collected separately by a licensed carrier, with the consignment note filed the same week",
            ],
            [
                "a missed collection is recovered within one working day; after the second one in a month the fee is cut by @PCT@%",
                "@VENDOR@ reports the tonnage and the recycled share of every site each quarter; the group targets @SLA@% of the total waste",
                "the collection windows of @S@ are kept, or the dock is left free for @H@ hours as an agreed penalty of @SMALL@ euros",
            ],
            [
                "the containers belong to @VENDOR@ and are washed on site every @M@ weeks",
                "the carrier is registered with the national waste operator list and the number is on every consignment note",
                "@VENDOR@ reports the recycled share by fraction so that the sustainability statement of the group can use it",
            ],
            [
                "the containers and the compactor at @S@ are emptied completely on the last day, and the site is swept clean",
                "the registers of the loads are handed to the site manager at exit, since the group must keep them for @Y2@ years",
            ]),

        new("building-maintenance", "Building maintenance and servicing contract", "facilities", ["facilities", "hardware"], ["office", "campus", "laboratory", "data_center", "plant", "warehouse"],
            "monthly", 5800, 24, 60, false, ["facilities", "managers", "all"], ["workplace-safety", "datacenter-access"],
            [
                "preventive maintenance of the HVAC plant, the chillers and the air handling units of @S@, with the schedule of the manufacturer",
                "the routine and the breakdown service of the lifts, the doors and the shutters at @S@, with a certified technician for the yearly test",
                "maintenance of the electrical distribution, the emergency lighting and the generators of @S@, tested @Q@ under load",
                "the plumbing, the sprinkler loop and the leak detection of @S@, with the small repairs up to @SMALL@ euros done without a new order",
                "the roof, the gutters and the facade of @S@ inspected twice a year, with the findings in a written report and a photo annex",
            ],
            [
                "a breakdown that stops the air conditioning in working hours is answered on site within @H@ hours and solved within @D@ hours",
                "the planned maintenance is notified @D@ days ahead and done outside the working hours of @S@ when the floor stays in use",
                "the asset register of @S@ is updated by @VENDOR@ after every intervention, with the readings and the parts replaced",
                "the first-time-fix rate on the tickets of @S@ is measured each month and must stay above @SLA@%",
            ],
            [
                "spares are original or equivalent, and the ones taken out of @S@ stay with the company if they can be repaired",
                "work that needs a hot permit is authorised by the site manager and supervised for the whole duration",
                "the technicians of @VENDOR@ wear the badge of the company for the whole intervention at @S@, since they are counted as visitors",
                "the contractor keeps @N@ spare compressors and the whole set of drive belts at its depot of@C2@, so an intervention does not wait for a delivery",
            ],
            [
                "the maintenance logs, the certificates and the drawings marked as built are handed over complete at the end of the term",
                "@VENDOR@ removes its equipment from @S@ within @D@ days of the end date and leaves the plant in the state the schedule requires",
            ]),

        new("catering", "Catering contract", "facilities", ["catering"], ["office", "campus", "plant", "warehouse"],
            "monthly", 11400, 12, 48, false, ["all", "new_hires", "facilities"], ["workplace-safety", "code-of-conduct"],
            [
                "lunch for up to @BIG@ employees on every working day in the canteen of @S@, from a weekly menu with vegetarian, vegan and gluten-free dishes",
                "the breakfast and lunch service at @S@, with a salad bar, a hot dish of the day and the coffee machines on the floors",
                "the canteen of @S@ (@D@ seats) served at two sittings to keep the queue under @H@ minutes, with a takeaway counter for the shift workers",
                "the working lunches and the visitor catering of @S@, ordered on the company portal by @D@ hours ahead",
                "vending machines on every floor of @S@, restocked @M@ times a week, with the free-of-charge water fountains serviced",
            ],
            [
                "the menu is agreed one month ahead with the site committee, which can send a dish back twice a quarter without a charge",
                "the temperature of every dish is logged twice a day and kept for @Y2@ years, as the food-safety rules of @C@ require",
                "the kitchen at @S@ can be audited with @D@ hours' notice; the last audit of @Y@ gave @SCORE@/100",
                "the average waiting time at the counter is under @H@ minutes, measured by the reader on the turnstile",
            ],
            [
                "@VENDOR@ takes back and reworks the unserved food and donates what is still fit through the agreement the group has with a local charity",
                "the allergens of every dish are shown on the menu board and in the app, updated whenever the recipe changes",
                "the canteen at @S@ keeps @N@ vegetarian options and the produce of the @C@ area for at least @SLA@% of the spend",
            ],
            [
                "the kitchen equipment owned by the company at @S@ is left clean and working; @VENDOR@ takes away its own small appliances",
                "the staff of @VENDOR@ employed at @S@ are offered a position with the incoming contractor for at least@M@ of them, as the social clause requires",
            ]),

        new("office-supplies", "Office supplies agreement", "procurement", ["office supplies"], ["office", "campus", "plant", "warehouse", "laboratory", "data_center"],
            "monthly", 3300, 12, 36, false, ["procurement", "all", "managers"], ["purchasing-thresholds", "vendor-onboarding"],
            [
                "paper, stationery and small office items from the catalogue of@VENDOR@, delivered to the mailroom of @S@",
                "the consumables of the @BIG@ workstations of @S@ — ink, toner, notebooks, cable sets and the like — reordered automatically when the stock falls below the agreed level",
                "a stock of@VENDOR@ goods kept at @S@ for @D@ weeks of demand, replenished once a month against the consumption",
                "furniture and equipment for the meeting rooms of @S@, with assembly by the supplier and removal of the packing material",
            ],
            [
                "orders placed before @H@ are delivered the next working day; the on-time rate on the orders of @S@ is measured monthly",
                "the catalogue prices are fixed for @M@ months and cannot rise more than @PCT@% in a year",
                "the returns of unused goods are accepted for @D@ days with a credit note in the following invoice",
            ],
            [
                "the minimum order for free delivery at @S@ is @SMALL@ euros, and the freight is not charged above it",
                "the consumption of @S@ is reported by cost centre each month, so the departments can see their own spending",
                "the paper bought under this agreement is at least@PCT@% recycled and carries the group's preferred label",
            ],
            [
                "the remaining stock at @S@ is taken back by@VENDOR@ against a credit of the catalogue price",
                "the standing orders of @S@ are cancelled by the company at no cost on the end date, and the invoicing profile is closed within @D@ days",
            ]),

        new("managed-print", "Managed print services agreement", "procurement", ["office supplies", "hardware"], ["office", "campus", "plant", "warehouse"],
            "monthly", 2600, 24, 48, false, ["procurement", "it", "facilities"], ["purchasing-thresholds", "personal-data"],
            [
                "@M@ multifunction devices on the floors of @S@, with the paper, the toner and the preventive service included in the per-page rate",
                "the print fleet of @S@ managed by@VENDOR@: devices, pull-print queues, secure release and the mobile printing app",
                "the printers and the label printers of @S@ supplied as a service, with the devices replaced when they pass@M@ thousand pages",
            ],
            [
                "the monthly volume of @BIG@ pages is included and the extra pages are billed at @CENTS@ cents each",
                "a device down for more than @H@ hours is replaced; the availability of the fleet of @S@ is measured at @UPTIME@%",
                "@VENDOR@ collects the counters and the job logs each month, and the company cuts the devices nobody uses",
            ],
            [
                "the hard disks of the retired devices are wiped with a certificate before they leave@S@",
                "print jobs are held for release by badge and never sit unattended on the machine",
                "the default of every driver at @S@ is duplex and black and white, as the group's print rule sets",
            ],
            [
                "the devices are removed from @S@ within @D@ days and the remaining consumables are taken back",
                "the print queues of @S@ are migrated to the new provider by@VENDOR@ without a downtime on the release terminals",
            ]),

        new("laptops-and-workstations", "Device supply and lifecycle agreement", "it", ["hardware"], ["office", "campus", "laboratory", "data_center", "plant", "warehouse"],
            "monthly", 18500, 12, 36, false, ["it", "all", "managers"], ["security-awareness", "personal-data", "purchasing-thresholds"],
            [
                "business laptops for the @BIG@ seats of @S@ in the models chosen by the IT department, each with a three-year on-site warranty",
                "the workstations, monitors and docks of the engineering team at @S@, delivered imaged to the company standard",
                "the replacement cycle of the devices of @S@: every machine is exchanged after @M@ months, so the fleet never goes past @Y2@ years of age",
                "@BIG@ tablets and @N@ shared conference kits for @S@, with the cases and the charging trolleys",
            ],
            [
                "a failed device at @S@ is replaced by the next working day, with the old one collected at the same time",
                "the order is delivered to the IT warehouse of @S@ within @D@ working days, and a missing delivery costs @PCT@% of the line",
                "the stock at @S@ keeps @N@ spare machines per @BIG@ seats, so a starter is never left without a device",
            ],
            [
                "the returned devices are wiped under the company's data-erasure procedure and a certificate is issued for each serial number",
                "the devices are registered in the asset inventory of @S@ with the cost centre of the holder",
                "@VENDOR@ takes back the machines at the end of their life and reports the share of parts it reuses",
            ],
            [
                "the remaining stock and the spare devices of @S@ are bought by the company at the book value at the end date",
                "@VENDOR@ hands over the image files, the firmware levels and the erasure certificates of every device before the final invoice",
            ]),

        new("network-connectivity", "Connectivity and network services agreement", "it", ["technology", "software"], ["office", "campus", "laboratory", "warehouse", "plant", "data_center"],
            "monthly", 7200, 24, 60, true, ["it", "managers"], ["incident-response", "datacenter-access", "security-awareness"],
            [
                "two diverse fibre circuits of@G@ Mbps at @S@ with automatic failover, and the routers managed by@VENDOR@",
                "the @M@ network access points of @S@, the switches of the server room and the guest wireless SSID, all under one maintenance contract",
                "the site-to-site VPN between @S@ and the other locations of the group, with the private lines sized at@G@ Mbps",
                "the SD-WAN edges of @S@ with the traffic policies of the group and the failover tested every @M@ weeks",
            ],
            [
                "the availability of the circuits of @S@ is @UPTIME@% measured monthly; a minute below the threshold is credited back at @RATE@ euros",
                "a priority-one outage is worked by the vendor's on-call team within @H@ hours, with a bridge open to the company every hour",
                "@VENDOR@ gives the planned maintenance @D@ days ahead and never inside the working hours of @S@",
            ],
            [
                "the network diagrams and the credentials of the equipment at @S@ are kept in the company's vault and updated after every change",
                "the traffic and the device health of @S@ are visible to the company on the vendor's portal, which also sends the weekly report",
                "the radio survey of @S@ is repeated every @M@ months, since the floors change layout twice a year",
            ],
            [
                "@VENDOR@ hands the IP plan, the circuit identifiers and the configuration backups to the company within @D@ days of the end date",
                "the circuits of @S@ are ported to the new carrier with a parallel period of @M@ days paid by@VENDOR@",
            ]),

        new("cloud-hosting", "Cloud hosting agreement", "it", ["technology", "software"], ["office", "campus", "data_center", "laboratory"],
            "monthly", 9400, 12, 48, true, ["it", "managers", "security"], ["incident-response", "personal-data", "datacenter-access", "export-controls"],
            [
                "@VENDOR@ hosts the internal applications of the company in two European regions, on dedicated infrastructure, with the@BIG@ virtual machines the company runs today",
                "the managed Kubernetes platform at @S@ and the object storage of the archive, sized at@T@ terabytes",
                "the staging and test environments of the group, with the environments created on request and removed after @D@ days of idleness",
                "the disaster-recovery copy of the production systems of @S@, with the replication tested twice a year",
            ],
            [
                "the availability of the platform is @UPTIME@% per month; below it the company gets a credit of @CREDIT@% of the monthly fee",
                "a security incident that touches the company's data is reported within @H@ hours, with the written report in @D@ days",
                "the recovery point of the workloads listed in the annex is @H@ hours and the recovery time is @D@ hours, tested in @Y@",
            ],
            [
                "the data stays the property of the company and is stored in @C2@ regions; the support staff can see the configuration but not the content",
                "the daily backups are kept encrypted for @M@ days and the archive for @Y2@ years, with a restore test each quarter",
                "usage above the agreed quota is billed at the rate card and warned at @PCT@% of the monthly allowance",
                "the platform is scanned for the vulnerabilities of the group's baseline, with the patch windows agreed one month ahead",
            ],
            [
                "the data is exported in an open format within @M@ days at no cost, and @VENDOR@ confirms the deletion with a signed statement",
                "the company keeps @M@ % of the environment running with@VENDOR@ during the migration to the next provider, at the same rate",
            ]),

        new("software-licences", "Software licence and subscription agreement", "it", ["software", "technology"], ["office", "campus", "laboratory", "data_center", "plant", "warehouse"],
            "monthly", 7600, 12, 36, false, ["it", "all", "managers"], ["personal-data", "purchasing-thresholds", "security-awareness"],
            [
                "the non-exclusive licence of the@VENDOR@ platform for@BIG@ named users of the group, with the modules of design, automation and reporting",
                "the subscriptions of the design tools used by @M@ people, mostly at @S@, with the shared seats the marketing team can book",
                "the site licence of the office and collaboration suite for every employee, including the home use allowed by the terms",
                "the analysis and observability stack of the IT department, metered on the ingested volume with a ceiling of@T@ terabytes a month",
            ],
            [
                "a critical defect has a response within @H@ hours and a workaround within @D@ days; the support line answers on working days",
                "the number of seats is reviewed once a year, and the unused seats are released before the renewal of @M@ months",
                "the updates are included; a major version is announced @M@ months ahead and the company chooses when to move",
            ],
            [
                "the company can export all of its data at any time in a documented open format, without a fee",
                "the licence keys and the tenant identifiers of @S@ are held by the company, not by the reseller",
                "the vendor's subprocessors are listed and a change is notified @D@ days ahead, as the personal-data rule requires",
            ],
            [
                "at the end date the company keeps a read-only export of the tenant for @M@ days, then@VENDOR@ deletes it",
                "the seats not used in the last term are not billed again if the company renews within @M@ months",
            ]),

        new("helpdesk-support", "IT service desk and end-user support agreement", "it", ["technology", "consulting"], ["office", "campus", "plant", "warehouse"],
            "monthly", 6800, 24, 48, false, ["it", "all", "new_hires"], ["security-awareness", "onboarding", "incident-response"],
            [
                "the first-line support of the @BIG@ users of @S@, from @H@ to @H2@ on working days, in the language of the site",
                "the desk that opens the tickets, resets the passwords and ships the replacements for the whole group, with a second line for the network and the identity systems",
                "the white-glove support of the executives and of the visitors at @S@, with the setup of the room before the meeting",
            ],
            [
                "the first response is within @H@ minutes and @SLA@% of the tickets are closed in the same day",
                "the satisfaction of the users of @S@ is asked on every closed ticket and must average @TEN@ out of 10",
                "the queue is reported weekly, with the ageing of the open tickets and the reasons of the escalations",
            ],
            [
                "the agents work from the company's knowledge base and are trained on the tools of the group every quarter",
                "the access of the support team is through the company's own identity provider, with the joiner-mover-leaver list reviewed each month",
                "@VENDOR@ records every remote session on a device of the company, and the recording is kept @M@ days",
            ],
            [
                "the ticket history, the knowledge base articles written for the company and the reports of @S@ are handed over at the end",
                "the handover to the next provider lasts @M@ weeks, with the old desk still answering until the new one is proven",
            ]),

        new("datacenter-colocation", "Data centre colocation and power contract", "it", ["hardware", "technology", "security"], ["data_center", "campus", "office"],
            "monthly", 13500, 36, 72, true, ["it", "security", "executives", "managers"], ["datacenter-access", "incident-response", "workplace-safety"],
            [
                "@N@ cabinets of@M@ kilowatts each in the hall of @S@, with the dual power feed, the cooling and the raised floor",
                "the cages, the cross-connects and the remote-hands service of @S@ for the hardware the company owns",
                "the power, cooling and space of the storage array and of the backup library at @S@",
            ],
            [
                "the power and cooling availability of @S@ is @UPTIME@%; a cabinet lost for more than @H@ hours is credited for the whole day",
                "remote hands answer within @H@ hours, day or night, and the ticket is closed with the photo of the work done",
                "the environmental readings of the cage at @S@ are visible to the company, with an alarm on the temperature and on the humidity",
            ],
            [
                "the entry to the hall of @S@ is by badge with an escort list, and every visit is logged and reviewed by the company monthly",
                "the media in and out of @S@ are registered and never leave without an authorisation of the IT security officer",
                "the fire-suppression and the generator tests of @S@ are notified to the company with the report of the contractor",
            ],
            [
                "the company's equipment is inventoried and labeled before it leaves@S@, and the racks are swept and returned as found",
                "@VENDOR@ keeps the cross-connects live for @M@ days after the end date so the migration can be cut over in a weekend",
            ]),

        new("managed-detection", "Managed detection and response services contract", "security", ["security"], ["office", "campus", "data_center", "laboratory", "warehouse"],
            "monthly", 8900, 12, 36, true, ["security", "it", "managers"], ["incident-response", "security-awareness", "personal-data"],
            [
                "24-hour monitoring of the endpoints, the identity systems and the cloud tenants of the group from the@VENDOR@ operations centre",
                "the detection rules tuned on the software the company actually runs at @S@, with the tuning reviewed every quarter",
                "threat hunting on the network of @S@ twice a month, with the findings written up and the follow-up tickets opened",
            ],
            [
                "a high-severity alert reaches the company's on-call within @H@ minutes, with the context and the recommended containment",
                "the analysts work containment on the company's own console, and every action is recorded in the case file",
                "the tabletop exercise with the incident manager of the group is run twice a year, at @S@ and remotely",
            ],
            [
                "the telemetry the vendor keeps is limited to the security events, and the retention is @M@ days, as the personal-data rule sets",
                "@VENDOR@ has no access to the payroll or the customer systems unless the company grants it for one case, time-boxed to@H@ hours",
                "the indicators of compromise found on the company's estate are shared back so the team at @S@ can act with them",
            ],
            [
                "the case files, the rules and the history of the alerts are exported to the company before the access is closed",
                "the sensors of @VENDOR@ are removed from the machines of @S@ by the company's own tool, with the report of the removal",
            ]),

        new("security-guarding", "Security guarding contract", "security", ["security"], ["office", "campus", "plant", "warehouse", "data_center"],
            "monthly", 15600, 12, 48, false, ["security", "facilities", "all"], ["workplace-safety", "incident-response", "datacenter-access"],
            [
                "guarding of the main entrance, the yard and the car park of @S@ twenty-four hours a day, with a patrol of every floor twice a night",
                "the manned reception of @S@ during the working hours and a mobile patrol that checks the perimeter @M@ times a night",
                "the control of the access badges and of the visitor register at @S@, with the escort of the visitors to the meeting rooms",
            ],
            [
                "the guards hold the licence required in @C@ and finish the company's safety induction before the first shift",
                "an incident is written up in @H@ hours and escalated to the head of security the same day",
                "the patrol checkpoints of @S@ are read by the guard's wand, and a missed round costs a credit of @SMALL@ euros",
            ],
            [
                "the guards do not search, detain or use force; anything beyond the standing instructions goes to the police contact of the group",
                "the keys and the alarms of @S@ are handed over against a register, and the codes change when a guard leaves the site",
                "@VENDOR@ staffs @S@ with a fixed team, so the company is not served by whoever is free on the day",
            ],
            [
                "the incident register, the patrol logs and the visitor lists of @S@ are delivered to the company at the end of the term",
                "the transition to the next provider is done with both teams on site for @M@ shifts",
            ]),

        new("alarm-monitoring", "Alarm monitoring and response contract", "security", ["security"], ["office", "campus", "warehouse", "plant", "laboratory", "data_center"],
            "monthly", 2900, 12, 48, false, ["security", "facilities"], ["incident-response", "workplace-safety"],
            [
                "monitoring of the intrusion and fire alarms of @S@ from the@VENDOR@ station, with the verification by camera before the dispatch",
                "the @M@ detectors, the sirens and the panic buttons of @S@ connected to the station, tested @Q@",
                "the response to an alarm at @S@: the keyholder called, the mobile guard sent and the authorities notified if the site is really open",
            ],
            [
                "the alarm is acknowledged within @N@ minutes, and the response time of the mobile guard at @S@ is under @H@ minutes",
                "the weekly line test proves that the signal from @S@ still reaches the station, and the result is filed",
                "a false alarm caused by the equipment of@VENDOR@ is not charged to the company",
            ],
            [
                "the call-out of the@M@ out of hours is logged, with who was called, what was seen and what was done",
                "the camera verification images are kept @M@ days and released only to the company and to the authorities",
            ],
            [
                "the panel codes, the zone map and the keyholder list of @S@ are transferred within @M@ days of the end date",
                "@VENDOR@ supports the commissioning of the new system for@D@ days free of charge",
            ]),

        new("legal-retainer", "Legal retainer", "legal", ["legal"], ["office", "campus", "laboratory", "warehouse", "plant", "data_center"],
            "monthly", 5800, 12, 36, true, ["legal", "managers", "executives"], ["code-of-conduct", "personal-data", "export-controls"],
            [
                "advice on commercial contracts, employment matters and data protection for the group, within @M@ hours a month of fixed retainer",
                "the review of the vendor contracts the procurement team signs at @S@, with the redlines back in @D@ working days",
                "the standing counsel of the company on privacy in@C@, including the answers to the requests of the data subjects",
            ],
            [
                "a request is answered within @D@ working days, and an urgent one within @H@ hours with the responsible lawyer named",
                "the hours beyond the retainer are billed at @RATE@ euros an hour and only after a written authorisation",
                "the matters are recorded in the company's register with the outcome, so the next review can compare",
            ],
            [
                "the advice is confidential and the file is kept by@VENDOR@ for @Y2@ years, then returned or destroyed on instruction",
                "the firm cannot act for a party adverse to the company in @C@ without a written waiver of the conflict check",
                "@VENDOR@ keeps its own advice out of the invoices of the group's vendors it also serves",
            ],
            [
                "the open files, the templates and the register of the matters are handed to the successor firm with a handover memo",
                "the matters still open at the end date are finished by@VENDOR@ at the rates of this agreement, up to@SMALL@ hours",
            ]),

        new("ip-and-patents", "Intellectual property and filing instructions", "legal", ["legal"], ["laboratory", "office", "campus"],
            "monthly", 4400, 24, 60, true, ["legal", "research", "executives"], ["export-controls", "code-of-conduct", "personal-data"],
            [
                "@VENDOR@ drafts and files the patent applications of the inventions disclosed by the teams at @S@, in @C@ and with the European route",
                "the trade mark portfolio of the group — @M@ registrations in @N@ countries — renewed, watched and defended by@VENDOR@",
                "the freedom-to-operate opinions the research team asks before a new product line, with the search report on the@BIG@ patents read",
            ],
            [
                "a filing deadline is never missed: the docketing of @VENDOR@ is checked by the company twice a year",
                "the prior-art search is delivered in @M@ weeks, and an opinion on@D@ days",
                "the annuities are paid on the instruction of the company, with the receipt of the office attached to each entry",
            ],
            [
                "the inventions are disclosed on the company's form by the engineers of @S@ and the assignment is recorded before the filing",
                "@VENDOR@ keeps the technical content out of any publication and uses no outside search firm without a written consent",
                "the prosecution files are the property of the company from the first draft",
            ],
            [
                "the docket, the deadlines and the originals are transferred to the successor firm with the certificates of filing",
                "the applications already filed stay with@VENDOR@ until the grant, if the company asks for it, at the same rates",
            ]),

        new("compliance-consulting", "Compliance and regulatory advisory contract", "legal", ["legal", "consulting"], ["office", "campus", "laboratory"],
            "monthly", 6900, 12, 36, true, ["legal", "finance", "managers", "executives"], ["code-of-conduct", "export-controls", "purchasing-thresholds"],
            [
                "@VENDOR@ keeps the compliance register of the group, maps the new rules that apply in @C@ and writes the internal policy for them",
                "the periodic review of the anti-bribery, sanctions and export controls of the company, with the testing of the process at @S@",
                "the support of the company in the audit of the regulator in@C@, including the preparation of the answers and the document room",
            ],
            [
                "the gap analysis of a new rule is delivered in @M@ weeks, with an owner and a date for every finding",
                "the findings of the@Y@ review were closed in @D@ days, one point still open and owned by legal",
                "the consultants do not implement what they audit, so the next review stays independent",
            ],
            [
                "the interviews with the staff of @S@ are anonymous unless the person agrees to be named",
                "the working papers stay in the company's repository, not in the consultant's cloud",
            ],
            [
                "the register, the templates and the open corrective actions are handed over with a memo on each one",
                "@VENDOR@ supports the first review after the exit for@D@ days at no cost",
            ]),

        new("statutory-audit", "Statutory audit engagement", "finance", ["audit", "finance"], ["office", "campus"],
            "annual", 84000, 12, 36, true, ["finance", "executives", "managers"], ["code-of-conduct", "purchasing-thresholds", "personal-data"],
            [
                "the audit of the statutory accounts of the group and of the subsidiaries in@C@, with the report to the board and the interim review each half year",
                "the assurance on the sustainability statement of the group, extended in @Y@ to the supply-chain figures",
                "the audit of the payroll and of the related-party balances of the @BIG@ employees of the company",
            ],
            [
                "the final report is delivered @M@ days before the board meeting, with the findings graded by risk",
                "the audit committee approves the plan and the fee; the engagement partner attends the meeting of @M@ ",
                "the time between the field work and the signed report never exceeds @D@ days",
            ],
            [
                "the auditor stays independent and cannot take consulting work from the company for @Y2@ years after the last signed report",
                "the audit fee is above the threshold of the executive committee, so the approval is minuted before the signature",
                "the working papers of @VENDOR@ are accessible to the company's lawyers in a dispute",
            ],
            [
                "the transition report to the incoming auditor is delivered within @M@ days of the end date, with no fee",
                "the prior-year files stay available to the company for the statutory retention of @Y2@ years",
            ]),

        new("insurance-programme", "Insurance programme and brokerage contract", "finance", ["insurance", "finance"], ["office", "campus", "plant", "warehouse", "laboratory"],
            "annual", 61000, 12, 36, false, ["finance", "executives", "legal", "managers"], ["purchasing-thresholds", "workplace-safety", "incident-response", "export-controls"],
            [
                "the property cover of the sites of the group, including @S@, against fire, flood, theft and business interruption for@BIG@ days",
                "the liability of the company towards third parties up to @BIG@ million euros, with the products and the completed-operations extension",
                "the cyber cover of the group, with the incident response costs, the business interruption and the regulatory defence, less a deductible of@SMALL@ euros",
                "the directors' and officers' cover for the board of the company and of the subsidiaries in@C@",
            ],
            [
                "a claim is reported within @D@ days and settled within @M@ days of the file being complete",
                "@VENDOR@ renews the programme one month before the expiry and brings the market quotes, with the recommendation written down",
                "the register of the policies, the deductibles and the open claims is kept current at all times and reviewed by the treasury of @S@",
            ],
            [
                "the premiums of this programme are booked on the cost centre of finance and approved by the executive committee, above the threshold of the purchasing rule",
                "@VENDOR@ discloses the commission it takes on every policy, and the company can ask for a fee instead",
                "the warranties and the conditions that would void a claim are listed for each site, including @S@",
            ],
            [
                "the broker hands the policies, the claims history and the open negotiations to the successor at the end of the term",
                "the claims already reported stay with@VENDOR@ until they are closed, at the rates of this agreement",
            ]),

        new("payroll-services", "Payroll bureau agreement", "hr", ["finance", "consulting"], ["office", "campus", "plant", "warehouse"],
            "monthly", 4700, 24, 60, true, ["hr", "finance"], ["personal-data", "security-awareness", "onboarding"],
            [
                "the payroll of the @BIG@ employees on the Italian books, run at @S@, with the payslips, the social contributions and the year-end certificate",
                "the payroll of the group in @C@, including the bonuses, the allowances and the garnishments",
                "the social reports, the declarations to the institutions of @C@ and the answering of the employee questions on the payslip",
            ],
            [
                "the payroll is closed on @D@ of the month and the payslips are published @N@ working days before the payment",
                "an error corrected after the payment is fixed in the next run, and the fine or the interest is borne by@VENDOR@",
                "the reconciliation with the general ledger of @S@ is delivered with every run",
            ],
            [
                "the payroll data are personal data: the processing is in @C@ only, the access is named, and the extract is never sent by ordinary mail",
                "@VENDOR@ notifies a breach that touches the company's people within @H@ hours, as the personal-data rule of the handbook requires",
                "the transfer of the payroll file to the bureau happens on the company's own secure channel",
            ],
            [
                "the archive of the payslips and the declarations is exported to the company, complete for @Y2@ years, before the accounts are closed",
                "the bureau runs one parallel payroll with the successor at no charge, so the people are paid on time",
            ]),

        new("banking-payments", "Banking and payment services agreement", "finance", ["finance"], ["office", "campus"],
            "monthly", 2800, 12, 48, false, ["finance", "executives", "managers"], ["purchasing-thresholds", "code-of-conduct", "export-controls"],
            [
                "the current accounts of the group at@VENDOR@, the cash pooling between the entities and the sweep of the surplus overnight",
                "the processing of @BIG@ supplier payments a month from @S@, with the file in the format the company's ERP exports",
                "the foreign exchange cover of the exposures in dollars and yen, booked by the treasury of @S@",
                "the merchant acquiring of the online store and the point of sale at the reception of @S@",
            ],
            [
                "the payment file is executed the same working day if it is received before @H@, with the status of every line visible",
                "a duplicate or a mistaken debit is recalled by@VENDOR@ within @D@ days, with the interest at the contractual rate",
                "the bank statements are delivered in the open format the company reconciles automatically",
            ],
            [
                "the maker and checker rights of @S@ are held by the company and reviewed every quarter with the list of the account signatories",
                "the sanctions screening of every counterparty is run before the payment, and a hit stops the line and opens a case",
                "@VENDOR@ reports the fees per line, so the finance team can see what the group actually pays",
            ],
            [
                "the mandates, the bank-data connectors and the templates of the payments are transferred to the successor bank with the company's instruction",
                "the accounts are closed with a final balance statement within @M@ days of the end date",
            ]),

        new("travel-management", "Travel management agreement", "operations", ["logistics", "consulting"], ["office", "campus", "plant", "warehouse"],
            "monthly", 2400, 12, 36, false, ["all", "managers", "operations", "finance"], ["business-travel", "expenses", "purchasing-thresholds", "incident-response"],
            [
                "@VENDOR@ books the flights, the hotels and the rail tickets of the people travelling from @S@, at the negotiated corporate rates",
                "the online booking tool of the group with the travel policy of @C@ enforced in the interface, plus the offline desk for the complex trips",
                "the visa letters, the insurance cards and the airport transfers of the @BIG@ trips a year the company makes",
            ],
            [
                "a booking changed or cancelled by the traveller is reissued by the desk within @H@ hours",
                "the report of the travel spend of @S@ reaches the finance team @D@ days after the month end, with the leaks named",
                "the duty-of-care locator shows where the people of the company are during a disruption, refreshed every@H@ hours",
            ],
            [
                "the trips above @SMALL@ euros need the approval of the manager before the booking, as the business-travel rule sets",
                "@VENDOR@ blocks the hotels that are off the preferred list unless the traveller has the approval of the department head",
                "the carbon figure of every trip is in the booking record, since the group reports it in the sustainability statement",
            ],
            [
                "the traveller profiles, the approved itineraries and the outstanding refunds are transferred to the successor agency within @M@ days",
                "the trips already booked stay serviced by@VENDOR@ until they are completed",
            ]),

        new("logistics-delivery", "Freight and delivery contract", "operations", ["logistics"], ["warehouse", "plant", "office", "campus"],
            "monthly", 9200, 12, 48, false, ["operations", "procurement", "managers"], ["workplace-safety", "export-controls"],
            [
                "the collection and delivery of parcels and pallets between @S@ and the customers of the group, with two working days in Europe and tracking on every shipment",
                "the@M@ outbound shipments a day of @S@, the groupage to @C2@ and the express envelope for the spare parts",
                "the customs paperwork of the goods that leave @S@ for @C2@, prepared by the broker inside@VENDOR@ ",
                "the return flows from the customers back to @S@, with the goods checked and put away or scrapped",
            ],
            [
                "@SLA@% of the parcels delivered to @C2@ arrive on the promised day, measured on the scans and reported monthly",
                "a shipment lost or damaged is refunded at the declared value within @D@ days, with no argument on the small ones under @SMALL@ euros",
                "the dock at @S@ keeps a slot for @VENDOR@ at @H@ hours, and a missed slot costs the carrier @SMALL@ euros",
            ],
            [
                "the drivers entering @S@ wear the badge, follow the marked routes and do not use the lift of the office floors",
                "the dangerous goods the company ships move only with the carrier's ADR-trained drivers and the placarded vehicles",
                "the temperature of the@T@ reefer pallets is logged from collection to delivery at @S@",
            ],
            [
                "the tracking history, the proof-of-delivery images and the open claims of @S@ are exported to the company before the accounts close",
                "@VENDOR@ moves its vehicles and the parked trailers out of the yard of @S@ within @M@ days",
            ]),

        new("warehousing", "Third-party warehousing contract", "operations", ["logistics"], ["warehouse", "plant"],
            "monthly", 11800, 24, 60, false, ["operations", "procurement"], ["workplace-safety", "export-controls", "purchasing-thresholds"],
            [
                "storage of @BIG@ pallet positions at @S@ for the spare parts of the group, with the picking and packing of the orders",
                "the bonded area of @S@ where the goods in suspension of duty are kept until the customs release",
                "the fulfilment of the online orders from @S@, including the kitting of the promotional boxes and the returns",
            ],
            [
                "the stock count matches the system of the company at @UPTIME@%, and the annual physical inventory is witnessed by finance",
                "an order received before @H@ leaves the same day; the on-time despatch of @S@ is measured weekly",
                "a mis-pick is corrected and reshipped at the cost of@VENDOR@ , and the pattern is reported at the monthly review",
            ],
            [
                "the racking, the sprinkler and the temperature of @S@ are maintained by@VENDOR@ with the certificates shown to the company",
                "the goods of the company are insured while they are stored at @S@, and the risk of the warehouse stays with@VENDOR@ ",
                "the company can send its own people into @S@ during the opening hours without notice to the carrier",
            ],
            [
                "the stock is picked out of @S@ within @M@ weeks at the exit, and the warehouse certificate for the last count is issued",
                "the pallets, the cages and the labels of@VENDOR@ are removed once the company's goods have left",
            ]),

        new("fleet-mobility", "Company fleet and mobility contract", "operations", ["hardware", "logistics"], ["office", "campus", "plant", "warehouse"],
            "monthly", 7400, 24, 48, false, ["operations", "facilities", "managers", "all"], ["workplace-safety", "expenses", "purchasing-thresholds"],
            [
                "@M@ cars and @N@ vans on full-service lease for the field people of @S@, with the tyre changes, the servicing and the replacement vehicle",
                "the pool cars of @S@ booked from the app, with the charge cards and the toll device on every vehicle",
                "the charging points in the car park of @S@ installed, maintained and billed to the users who need them",
                "the mobility budget that the people of the group can spend on trains, bikes and taxis instead of a car",
            ],
            [
                "a vehicle out of service for more than @D@ days is replaced by@VENDOR@ , and the accident file is closed within @M@ days",
                "the fuel and toll data of @S@ reach the finance team in the file format of the ERP, monthly",
                "the consumption and the CO2 of every car are reported; the fleet average must fall below @CO2@ grams a kilometre by @Y2@",
            ],
            [
                "the drivers of @S@ are covered by the policy of@VENDOR@ and follow the licence check the company runs each year",
                "the vehicles are washed and the safety equipment — warning triangle, high-visibility vest, first-aid kit — is verified @Q@",
            ],
            [
                "the cars are returned to@VENDOR@ within @M@ days of the end date, and the ones the company buys are re-registered by its own agent",
                "the charging points at @S@ stay with the building and are transferred to the company's energy contract",
            ]),

        new("recruitment-search", "Executive and specialist search agreement", "hr", ["recruitment", "consulting"], ["office", "campus", "laboratory"],
            "per_work_order", 2100, 12, 36, false, ["hr", "managers", "new_hires"], ["onboarding", "code-of-conduct", "purchasing-thresholds", "security-awareness"],
            [
                "@VENDOR@ presents candidates for the @M@ positions a year the company opens at @S@, with a shortlist in @D@ working days and a screening already done",
                "the retained search for the head of @DEPT@ , with the market map, the long list and the assessment of the final two",
                "the sourcing of the rare skills the laboratory at @S@ needs, in @C@ and in the neighbouring countries",
            ],
            [
                "the fee is @PCT@% of the first year's gross salary of the person hired, with a replacement at no cost if they leave within @M@ months",
                "a retainer of @SMALL@ euros a month covers priority handling and is offset against the success fee",
                "the feedback to every candidate is given within @D@ days of the decision, as the company's candidate-experience rule asks",
            ],
            [
                "the personal data of the candidates are held in the company's applicant system once the profile arrives, and @VENDOR@ deletes its copy after @M@ months",
                "the search firm cannot present the same candidate to another employer for the same role while the process is open",
                "@VENDOR@ uses only the job boards and the databases the company approved for@C@",
            ],
            [
                "the candidate files of the open processes are transferred to the company or to the successor firm within @M@ days",
                "the placement made before the end date is invoiced at the rate of this agreement even if the person starts later",
            ]),

        new("interim-staffing", "Interim staffing and secondment agreement", "hr", ["recruitment"], ["office", "campus", "plant", "warehouse", "laboratory"],
            "monthly", 9600, 6, 24, false, ["hr", "operations", "it", "managers"], ["onboarding", "security-awareness", "code-of-conduct", "personal-data"],
            [
                "the seconded staff that covers the peaks at @S@: @M@ people a month, from the desk of the@VENDOR@ framework, at the rates of the annex",
                "the temporary engineers who join the team at @S@ for a project of @M@ months, with the knowledge transfer at the end",
                "the seasonal staffing of the@S@ operation between @Y@ and @Y2@ , with the shift pattern agreed one month ahead",
            ],
            [
                "an approved requisition is filled with a named person within @D@ days, or the rate is cut by @PCT@%",
                "the time sheets of the seconded people are confirmed by the manager at @S@ weekly and invoiced once",
                "the absence of a seconded person is covered within the same shift for the roles listed as critical",
            ],
            [
                "the seconded people get the company's badge, follow the induction of @S@ and are covered by the group's insurance while working there",
                "the rate card of @VENDOR@ is fixed for @M@ months and cannot rise by more than @PCT@% a year",
                "no seconded person works on the payroll data of the company without the access approval of HR",
            ],
            [
                "the people still on assignment at the end date finish their work or move to another supplier without a gap",
                "@VENDOR@ collects its own equipment and badges from @S@ within @M@ days",
            ]),

        new("training-programmes", "Training and learning agreement", "hr", ["training", "consulting"], ["office", "campus", "plant", "warehouse", "laboratory"],
            "monthly", 3900, 12, 36, false, ["new_hires", "hr", "managers", "all"], ["onboarding", "security-awareness", "lab-safety", "code-of-conduct"],
            [
                "@VENDOR@ delivers the technical and soft-skills courses for the people at @S@, in person or online, in groups of up to @N@ ",
                "the leadership programme of @M@ managers of the group, in @D@ modules with an individual coach at the end",
                "the safety training required for the people who enter the laboratory at @S@, renewed every year with the attendance register",
                "the language courses for the teams that work across @C@ and @C2@, two hours a week for @M@ weeks",
            ],
            [
                "the satisfaction of the participants is collected after every course and must average @SCORE@ out of 10",
                "a course cancelled by@VENDOR@ inside @D@ days is rescheduled free of charge, and the second time is not billed",
                "the learning hours of @S@ are reported by department each quarter, so the training plan can be adjusted",
            ],
            [
                "the materials of the courses the company pays for are licensed to the group for internal use after the contract",
                "@VENDOR@ uses the company's own platform for the invitations, the attendance and the certificates",
                "the trainers of @VENDOR@ follow the induction of @S@ and are escorted where the badge is not enough",
            ],
            [
                "the attendance registers, the evaluations and the recorded sessions are handed over at the end date",
                "the courses already booked are delivered or refunded, at the company's choice",
            ]),

        new("marketing-services", "Marketing services agreement", "marketing", ["marketing", "consulting"], ["office", "campus"],
            "monthly", 12400, 12, 36, false, ["marketing", "managers", "all"], ["brand-guidelines", "code-of-conduct", "purchasing-thresholds", "personal-data"],
            [
                "@VENDOR@ plans and runs the campaigns of the group in @C@ : the creative, the media plan, the landing pages and the monthly report on the results",
                "the content production for the product lines of @S@ — the case studies, the videos, the newsletter that @BIG@ customers read",
                "the management of the paid channels and of the affiliate programme, with the budget of @SMALL@ euros a month decided together",
                "the rebrand of the@C@ range, from the naming check to the launch, with the guidelines rewritten",
            ],
            [
                "the campaign plan is agreed @M@ days before the launch and the results are reported @D@ days after the month end",
                "the creative is delivered with the source files, and the company owns everything it pays for",
                "@VENDOR@ does not publish or use the company's name or marks without the written approval of the marketing director",
            ],
            [
                "the media spend is invoiced at cost with the fee shown separately, and the company sees the platform receipts",
                "the customer lists used for the campaigns are the company's own, and @VENDOR@ runs no audience of its own from them",
                "the claims made in an advertisement are checked by legal before the launch, as the code of conduct and the local rules require",
            ],
            [
                "the accounts, the pixels, the audiences and the ad libraries are transferred to the company before they are closed",
                "the campaigns still running at the end date are finished by@VENDOR@ within @M@ days at the rates of this agreement",
            ]),

        new("events-and-trade-shows", "Events and trade show organisation contract", "marketing", ["marketing", "catering"], ["office", "campus", "warehouse"],
            "per_work_order", 8200, 6, 24, false, ["marketing", "operations", "all", "managers"], ["brand-guidelines", "purchasing-thresholds", "workplace-safety", "business-travel"],
            [
                "the stand, the build and the staffing of the@VENDOR@ shows the company attends each year, starting with the@C@ edition of @Y@",
                "the customer day at @S@ for @BIG@ guests: the venue, the catering, the agenda, the registration and the follow-up report",
                "the roadshow of @N@ cities in @C@ , with the room, the equipment and the travel of the speakers",
                "the partner summit of @M@ people in @C2@ , including the interpretation and the recorded sessions",
            ],
            [
                "the event brief is answered with a plan and a fixed price in @D@ days, and the price does not change unless the brief does",
                "the build of the stand at @S@ finishes before the doors open, or @VENDOR@ pays the overtime of the company's staff",
                "the attendee numbers, the leads captured and the cost per lead are reported within @M@ days of the event",
            ],
            [
                "the supplier's staff on the stand follow the venue's safety rules and the evacuation briefing of @S@",
                "the food and drink offered at the events respect the group's code of conduct on gifts and hospitality",
                "@VENDOR@ takes back and recycles the stand material, and donates what is left over to a local organisation",
            ],
            [
                "the files of the stand, the media and the registrations of @S@ are handed to the company after the last event",
                "the deposits paid on the venue of @C2@ are recovered by@VENDOR@ and credited to the company",
            ]),

        new("research-partnership", "Research partnership agreement", "research", ["research"], ["laboratory", "campus", "office"],
            "monthly", 22500, 24, 60, true, ["research", "executives", "legal"], ["export-controls", "lab-safety", "personal-data", "code-of-conduct"],
            [
                "a joint project with the @VENDOR@ team on the battery pack of the next product generation, at @S@ and in their laboratory in @C2@, with @N@ researchers each side",
                "the long-term study of the materials the company uses at @S@, with the test plan reviewed every six months by both parties",
                "the doctorate co-supervised with the @VENDOR@ institute, funded for @M@ months, with the thesis published in the open after the patent filing",
                "the exploratory work on the thermal ageing problem, in phases, with a go or no-go decision after each @M@ months",
            ],
            [
                "the results and the inventions belong to the company and the partner in equal parts, and each can use them inside its own business",
                "the publications are cleared by legal @M@ weeks before submission, so a patent is filed first",
                "the milestones are paid against the written deliverable; @Y@ had @N@ of @M@ met on time",
            ],
            [
                "the background of each party stays separate and is listed in the annex before the work starts",
                "the data the researchers of @S@ produce are kept in the company's repository, with the partner reading them",
                "the exchange of technical information follows the export-control classification of @C@ , and the screening of the researchers is done before the visit",
            ],
            [
                "the lab notebooks, the raw measurements and the source code of the project at @S@ are delivered to the company at the end",
                "@VENDOR@ keeps the samples the company left in @C2@ for @M@ months after the end date, then returns or destroys them",
            ]),

        new("laboratory-services", "Laboratory analysis and testing services contract", "research", ["laboratory", "research"], ["laboratory", "plant", "campus"],
            "per_work_order", 5400, 12, 48, false, ["research", "operations", "managers"], ["lab-safety", "export-controls", "personal-data"],
            [
                "@VENDOR@ runs the chemical and mechanical tests the team at @S@ requests, with the method of the annex and the report in @D@ working days",
                "the stability and ageing studies of the samples produced at @S@, kept in the chambers of the laboratory in@C2@ for @Y2@ months",
                "the failure analysis of the parts that came back from the field: the microscopy, the cross-section and the written conclusion",
                "the accreditation tests that let the company sell in @C2@ , prepared and witnessed by@VENDOR@ ",
            ],
            [
                "the turnaround is @D@ working days from the receipt of the samples at @S@ , measured on the last @BIG@ requests",
                "a test that has to be repeated because of the laboratory is redone at no cost",
                "the raw data of every test stay available to the company for @Y2@ years and are exported on request",
            ],
            [
                "the samples leave @S@ with the safety data sheet and the tracking note, and the hazardous ones are handled by the trained staff of@VENDOR@ ",
                "the laboratory is accredited to the standard the regulators of @C@ accept, and the certificate is shown at every review",
                "the results are reported with the measurement uncertainty and the identity of the analyst",
            ],
            [
                "the samples still held for the company are returned or destroyed on instruction at the end date",
                "the methods developed for @S@ during the agreement are handed over with the validation file",
            ]),

        new("calibration-and-instruments", "Instrument calibration and maintenance contract", "research", ["laboratory", "hardware"], ["laboratory", "plant", "warehouse"],
            "annual", 14800, 12, 36, false, ["research", "facilities", "managers"], ["lab-safety", "purchasing-thresholds"],
            [
                "the calibration of the@M@ instruments of @S@ on the traceability chain of the national institute, on site or at the laboratory of@VENDOR@ ",
                "the annual verification of the scales, the chambers and the test benches of @S@, with the certificates attached to each serial number",
                "the adjustment and the repair of the measurement equipment the production line at @S@ depends on",
            ],
            [
                "an instrument that fails the calibration is replaced with a loaned one within @D@ days, so the line at @S@ does not stop",
                "the due dates of the certificates of @S@ are in the company's scheduler, and @VENDOR@ sends the reminder @M@ weeks ahead",
                "the accreditation of the calibration laboratory is renewed by@VENDOR@ without a gap in the chain",
            ],
            [
                "the tolerance band of every instrument at @S@ is set with the engineers of the company and recorded on the certificate",
                "the instruments taken out of @S@ are insured by@VENDOR@ while they are in the workshop",
            ],
            [
                "the register of the instruments, the certificates and the history of the drifts are exported to the company before the end date",
                "the loaned instruments are returned within @M@ days of the last calibration",
            ]),

        new("management-consulting", "Management consulting framework", "operations", ["consulting", "research"], ["office", "campus", "plant", "warehouse"],
            "per_work_order", 16500, 6, 36, true, ["managers", "executives", "operations", "finance"], ["purchasing-thresholds", "code-of-conduct", "personal-data", "export-controls"],
            [
                "the diagnostic of the order-to-cash process that runs through @S@ , with the map, the benchmark and the business case of the fixes",
                "the support of the company in the reorganisation of the@DEPT@ function, for @M@ weeks with @N@ consultants on site at @S@",
                "the study of the@C@ network: @M@ candidate layouts, the service and cost trade-off, and the recommendation for the board",
                "the post-merger integration plan of the@C2@ business bought in @Y@, with the workstreams and the first @M@ days of the transition",
            ],
            [
                "the engagement letter fixes the deliverables and the fee before the work starts, and no extension is billable without a written order",
                "the consultants of @VENDOR@ are named in the order, and a replacement needs the agreement of the sponsor at @S@",
                "the weekly steering and the final report are contractual deliverables, with the data behind the charts",
            ],
            [
                "the fee is above the threshold of the executive committee, so the approval is minuted before the signature of the order",
                "@VENDOR@ keeps what it learns at @S@ out of any other mandate in the same market for @Y2@ months",
                "the interviews with the people of the company are anonymous unless the person agrees to be named",
            ],
            [
                "the models, the data extracts and the decks produced for @S@ belong to the company and are handed over in the editable format",
                "@VENDOR@ supports the first review of the recommendations @M@ months after the end of the mandate, at no fee",
            ]),

        new("data-and-analytics", "Data platform and analytics services contract", "it", ["technology", "software", "research"], ["office", "campus", "data_center", "laboratory"],
            "monthly", 10800, 12, 48, true, ["it", "managers", "finance", "operations"], ["personal-data", "incident-response", "datacenter-access", "export-controls"],
            [
                "the data platform that serves the reports of @S@: the ingestion of @M@ sources, the warehouse and the models, run by@VENDOR@ ",
                "the forecasting models of the demand the operations team uses for @S@ , retrained every quarter and validated against the actuals",
                "the data-quality service that watches the key tables of the group, with the findings routed to the owner of the field",
                "the self-service reporting estate of @BIG@ users, with the governance of the folders and of the access",
            ],
            [
                "the nightly load of @S@ finishes before @H@ in @PCT@% of the mornings, and a miss is reported with the reason before the stand-up",
                "the access to the personal data in the platform is logged, reviewed monthly and never exported outside@C@ ",
                "the models are delivered with the documentation, the feature list and the last backtest, so the company can run them alone",
            ],
            [
                "@VENDOR@ processes the data only on the company's instruction, as the personal-data rule of the handbook requires, and the subprocessors are listed",
                "the tenancy of @S@ is separated from the other clients of@VENDOR@ , with no shared notebook or shared storage",
                "the retention of the raw events of @S@ is @M@ months, then the aggregate stays for @Y2@ years",
            ],
            [
                "the warehouse, the notebooks, the pipelines and the credentials of @S@ are handed over before the tenant is closed",
                "@VENDOR@ keeps the platform running for the migration for @M@ days at the rate of the last month",
            ]),
    ];

    public static ContractKind KindOf(string id) => Kinds.Single(kind => kind.Id == id);

    /// <summary>
    /// The two article names a real agreement of that kind would carry: the one of the object and the one of what is measured. The other
    /// articles keep a name shared between contracts (fees, confidentiality, term), which is what they are called in practice as well.
    /// </summary>
    public static (string Scope, string Levels) ArticlesOf(string kindId) =>
        KindArticles.TryGetValue(kindId, out var names) ? names : ("Services and scope", "Service levels");

    private static readonly Dictionary<string, (string Scope, string Levels)> KindArticles =
        new(StringComparer.Ordinal)
        {
            ["cleaning"] = ("Services at the site", "Cleanliness standards and credits"),
            ["waste-and-recycling"] = ("Waste fractions and collection", "Collection reliability and recycled share"),
            ["building-maintenance"] = ("Plant and equipment covered", "Response times and asset register"),
            ["catering"] = ("The catering service", "Menu, temperatures and waiting time"),
            ["office-supplies"] = ("Goods and catalogue", "Delivery and catalogue prices"),
            ["managed-print"] = ("The print fleet", "Fleet availability and page counts"),
            ["laptops-and-workstations"] = ("Devices and lifecycle", "Replacement times and spares"),
            ["network-connectivity"] = ("Circuits and network equipment", "Circuit availability and outage handling"),
            ["cloud-hosting"] = ("Platform, regions and workloads", "Availability, downtime credits and recovery"),
            ["software-licences"] = ("Seats, modules and licence keys", "Support response and seat review"),
            ["helpdesk-support"] = ("First and second level support", "Response, closure rate and satisfaction"),
            ["datacenter-colocation"] = ("Cabinets, power and space", "Power, cooling and remote hands"),
            ["managed-detection"] = ("Monitoring scope and telemetry", "Alert times and containment"),
            ["security-guarding"] = ("Posts, patrols and shifts", "Licences, rounds and escalation"),
            ["alarm-monitoring"] = ("Alarm lines and response", "Acknowledgement and call-out"),
            ["legal-retainer"] = ("Instructions and areas of advice", "Response times and retainer hours"),
            ["ip-and-patents"] = ("Matters and filings", "Deadlines, searches and annuities"),
            ["compliance-consulting"] = ("Regulatory scope of the mandate", "Findings and their closure"),
            ["statutory-audit"] = ("Engagement and scope of the audit", "Reporting calendar and independence"),
            ["insurance-programme"] = ("Cover, limits and deductibles", "Claims handling and renewal"),
            ["payroll-services"] = ("Payroll runs and declarations", "Closing dates and corrections"),
            ["banking-payments"] = ("Accounts, payments and limits", "Execution times and recalls"),
            ["travel-management"] = ("Bookings and the travel service", "Reissues, spend report and duty of care"),
            ["logistics-delivery"] = ("Lanes, volumes and transit times", "On-time delivery and losses"),
            ["warehousing"] = ("Storage, picking and stock", "Stock accuracy and despatch times"),
            ["fleet-mobility"] = ("Vehicles, charging and cards", "Replacement, reporting and emissions"),
            ["recruitment-search"] = ("Roles and search mandate", "Shortlist times and replacement"),
            ["interim-staffing"] = ("Seconded staff and profiles", "Fill times and cover of absences"),
            ["training-programmes"] = ("Courses and learning paths", "Attendance, evaluation and materials"),
            ["marketing-services"] = ("Campaigns and deliverables", "Plans, receipts and reporting"),
            ["events-and-trade-shows"] = ("Events and stand services", "Build times and event reporting"),
            ["research-partnership"] = ("Project, phases and team", "Milestones, publications and ownership"),
            ["laboratory-services"] = ("Tests and methods", "Turnaround and repeat tests"),
            ["calibration-and-instruments"] = ("Instruments and traceability", "Calibration dates and loaned instruments"),
            ["management-consulting"] = ("Mandate and workstreams", "Deliverables and named team"),
            ["data-and-analytics"] = ("Platform, sources and models", "Loads, access and model handover"),
        };

    /// <summary>Clause pools that are not specific to a kind: the writers mix them so that two contracts of the same kind never read alike.</summary>
    public static IReadOnlyList<string> PaymentClauses { get; } =
    [
        "the fee of @FEE@ euros is invoiced @CADENCE@ and is paid within @D@ days of the receipt of a correct invoice",
        "@VENDOR@ invoices @CADENCE@ on the actual volumes, and the company checks the invoice against the usage report before it approves it",
        "the total value of the agreement is @TOTAL@ euros, booked on the cost centre @CC@ and approved by @APPROVER@ under the purchasing thresholds of the handbook",
        "no fee is billable for a day on which the service was not available at @S@, and the credit is applied on the next invoice without a claim",
        "the late-payment interest of @PCT@% a year applies under the rules of @C@ , and undisputed amounts are never held back",
    ];

    public static IReadOnlyList<string> IndexationClauses { get; } =
    [
        "the rates are fixed for the first @M@ months and then follow the consumer price index of @C@ , capped at @PCT@% a year",
        "the price is fixed for the whole term, and @VENDOR@ may ask for a revision only if a regulatory cost changes by more than@PCT@%",
        "the rates are reviewed at each anniversary against the market test, and a rise above @PCT@% needs the approval of the category manager",
    ];

    public static IReadOnlyList<string> ConfidentialClauses { get; } =
    [
        "this agreement is marked confidential: the scope, the rates and the reports of @S@ are shared only with the people named in the annex, and the disclosure log is kept by legal",
        "the existence and the terms of this contract are confidential, and @VENDOR@ may not use the company's name in its marketing without the written consent of legal",
        "the work performed for @S@ is confidential to the group's plans; the consultants of@VENDOR@ sign the group's non-disclosure form before the access is granted",
        "the information exchanged under this agreement stays inside the group and in @C2@ ; an onward transfer needs the authorisation of the data protection officer",
    ];

    public static IReadOnlyList<string> OpenClauses { get; } =
    [
        "the agreement is not confidential and @VENDOR@ may list the company as a reference, without naming the volumes of @S@ or the rates",
        "the summary of this contract is in the internal register that every department can read, while the annex with the rates stays with procurement",
    ];

    public static IReadOnlyList<string> DataClauses { get; } =
    [
        "where the work touches personal data, @VENDOR@ acts on the written instruction of the company only, keeps the processing in @C@ and reports a breach within @H@ hours, as the personal-data rule of the handbook requires",
        "the sub-processing of @S@ is listed in the annex and a new sub-processor is notified @D@ days ahead, so the company can object",
        "the data of the company never leaves the @C@ environment, and the extracts used for testing are anonymised before they leave @S@",
        "the records generated at @S@ are the company's, are kept for @Y2@ years and are deleted with a signed confirmation at the end of that period",
    ];

    public static IReadOnlyList<string> SubcontractingClauses { get; } =
    [
        "@VENDOR@ does not subcontract the work of @S@ without the prior written consent of procurement, and it stays responsible for what the subcontractor does",
        "the crew working at @S@ is employed by@VENDOR@ ; labour compliance, insurance and training remain its duty and are proved once a year",
        "the subcontractors used in @C@ are declared with their name and their task, and any of them can be refused by the company without an argument",
    ];

    public static IReadOnlyList<string> InsuranceClauses { get; } =
    [
        "@VENDOR@ holds professional liability of @BIG@ million euros and the cover required by the law of @C@ , with the certificate renewed each year and filed with procurement",
        "the damage @VENDOR@ causes at @S@ is borne by it up to @TOTAL@ euros, and the insurance of the group covers what is left beyond that",
    ];

    public static IReadOnlyList<string> AuditClauses { get; } =
    [
        "the company can audit the records and the processes of @S@ with @D@ days' notice, and the findings are closed with a plan inside @M@ days",
        "@VENDOR@ is audited within the supplier programme of the group: the financial check, the security questionnaire and the references, as the vendor-onboarding rule sets",
        "the internal audit of @Y@ looked at this agreement and asked for the evidence of the service credits, which is now kept with the monthly report",
    ];

    public static IReadOnlyList<string> IpClauses { get; } =
    [
        "what @VENDOR@ produces for @S@ under this agreement belongs to the company, and the vendor keeps only what it already owned, listed in the annex",
        "the company's marks, drawings and data stay its property; @VENDOR@ gets a licence for the term and nothing beyond it",
        "the improvements suggested by the team at @S@ are recorded and become the company's, while the tools of@VENDOR@ stay with it",
    ];

    public static IReadOnlyList<string> ReportingClauses { get; } =
    [
        "the monthly report reaches the owner at @S@ on @D@ of the following month, with the volumes, the exceptions and the credits applied",
        "the service review happens @CADENCE@ with the account manager of@VENDOR@ and the department, and the minutes track every open action to a date",
        "the dashboard of the agreement is open to the department, and the figures of @S@ are reconciled with the finance report each quarter",
    ];

    public static IReadOnlyList<string> ExitClauses { get; } =
    [
        "either party can end the agreement with @NOTICE@ days' written notice, and the company can end it sooner without a charge if the service fails twice in a quarter",
        "the company can end this agreement for convenience at the end of any term, with @NOTICE@ days' notice and no early-termination fee",
        "if @VENDOR@ is bought or its service is taken over, the company can end the agreement at the next month end, and the transition is delivered at cost",
    ];

    public static IReadOnlyList<string> HandoverClauses { get; } =
    [
        "the exit plan is written in the first @M@ months and is tested once, so the handover to another provider does not depend on goodwill",
        "@VENDOR@ cooperates with the successor at no fee for @M@ days, answers their questions and joins the two transition calls",
    ];

    public static IReadOnlyList<string> SustainabilityClauses { get; } =
    [
        "@VENDOR@ reports the energy and the emissions of the work at @S@ each year, in the format the group uses for its sustainability statement",
        "the crews come to @S@ by train or by the shared van where the timetable allows, and the diesel equipment on the yard is replaced at the end of its life",
        "the material and the packaging taken away from @S@ are recycled or reused, with the share proved by the weight notes",
        "the supplier declares no conflict with the group's code of conduct and answers the yearly questionnaire on ethics and on working conditions",
    ];

    public static IReadOnlyList<string> DisputeClauses { get; } =
    [
        "a dispute starts with the account manager and the department head of @S@ , then with the directors, and only after @M@ days goes to the courts of @C@",
        "the parties try mediation in @C@ before any claim, and the contract stays in force while they do",
    ];

    /// <summary>What can have happened on a live contract: the history the writers pick one or two of, with a date inside the term.</summary>
    public static IReadOnlyList<string> HistoryEvents { get; } =
    [
        "the service of @DATE@ at @S@ was missed and redone the next morning at no cost, and @PCT@% was deducted from that month",
        "the escalation of @DATE@ after @M@ open tickets brought a written action plan, closed within @D@ days of the meeting",
        "the price revision of @DATE@ raised the fee by @PCT@% with the consumer price index of @C@ , and the category manager approved it",
        "the audit of @DATE@ found the certificates of the staff at @S@ incomplete; they were collected in @D@ days and are now checked @Q@",
        "a security event touching @S@ was notified on @DATE@ within @H@ hours, with the written report of@VENDOR@ and the follow-up review",
        "the disputed invoice of @DATE@, a duplicated line of @SMALL@ euros, was credited in the next one without an argument",
        "the delivery of @DATE@ arrived @D@ days late at @S@ and the service credit of @PCT@% was applied on the monthly fee",
        "the renewal of @DATE@ was negotiated with the market test: three quotes, and @VENDOR@ stayed on the scope of @S@ on price and on the score of the last year",
        "the extension of @M@ months signed on @DATE@ kept the rates of this agreement, because the works of @S@ are not finished",
        "the review of @DATE@ scored @VENDOR@ at @SCORE@ out of 100 on @S@ , with the responsiveness of the night shift as the weak point",
        "the bad-weather call of @DATE@ brought the crew to @S@ outside the schedule at no extra charge",
        "the change of the account manager on @DATE@ was followed by a handover memo and a visit to @S@ within @D@ days",
        "the claim of @DATE@ for the damage in the yard of @S@ was settled at the declared value in @D@ days",
        "the extra volume requested for @DATE@, @M@ more pallets a week at @S@, was absorbed without a change of fee until the anniversary",
        "the termination notice of the parallel agreement at @S@ on @DATE@ moved one line of the scope inside this contract, priced on the annex rates",
    ];

    /// <summary>Why an agreement was ended before its date: quoted only by the contracts whose status is terminated.</summary>
    public static IReadOnlyList<string> TerminationReasons { get; } =
    [
        "the site of @S@ was sold and the requirement stopped on @DATE@",
        "the scope was brought in house with the people the agreement already had at @S@",
        "@VENDOR@ lost the certification the work at @S@ needs, and procurement ended the agreement rather than wait",
        "the restructuring of @DEPT@ removed the budget that carried this agreement",
        "two quarters below the service levels of @S@ gave the company the right to end it without a charge",
        "the parent of @VENDOR@ was bought and the new owner would not keep the rates of the annex",
    ];

    /// <summary>Why the company is working with this vendor, or why it changed: the rationale the history section quotes.</summary>
    public static IReadOnlyList<string> RationaleClauses { get; } =
    [
        "the choice of @VENDOR@ rests on the market test of @DATE@ and on the fact that it already works at @S@ , so no new crew needed the induction",
        "the incumbent missed the response times for two quarters and the supplier review scored it below the threshold, so this scope moved to@VENDOR@ ",
        "@VENDOR@ was preferred because of its rating of @RATE@ in the last supplier review and the lower rate on the @S@ scope",
        "procurement keeps this scope with a second supplier on purpose: the single-vendor dependency found in the @Y@ audit must not repeat",
        "the scope was bundled with the agreement @RELATED@ at @S@ to get a better rate and one report to read instead of two",
        "@VENDOR@ was added to the approved list after the due diligence of vendor onboarding: the financial check, the security questionnaire and two references",
        "the team of @S@ asked for @VENDOR@ after the pilot of @DATE@ , and the pilot's findings are in the annex of service levels",
        "the entity in @C@ needed a local supplier for the rules of @C@ , and @VENDOR@ is the one already registered with the institutions",
    ];
}
