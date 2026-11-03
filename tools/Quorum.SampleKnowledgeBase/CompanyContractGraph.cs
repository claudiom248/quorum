using System.Globalization;

namespace Quorum.SampleKnowledgeBase;

/// <summary>
/// The shape of the contracts of <see cref="CompanySampleData"/>: which need each agreement answers, at which site, with which vendor,
/// who owns it inside the company, what came before it and what happened while it was live. The graph is decided once from a fixed seed,
/// so the same agreement reads the same in every run and on every machine, and two agreements of the same kind never read alike.
/// The references are real: the vendor of a kind always fits its category, the owner is a person of <see cref="PeopleSampleData"/> who
/// really deals with that vendor, the policies are the ones of the handbook, and every identifier quoted in the text is an agreement
/// that exists in the same collection.
/// </summary>
internal static class CompanyContractGraph
{
    internal const int ContractCount = 622;

    /// <summary>The day the sample thinks it is: what has ended and what is still live is measured against it.</summary>
    internal static readonly DateOnly AsOf = new(2025, 10, 31);

    /// <summary>One agreement, with everything the writers need. The links are objects until the identifiers are assigned.</summary>
    internal sealed class Contract
    {
        public string FamilyId { get; set; } = "";

        public required CompanyFacts.ContractKind Kind { get; init; }

        public required CompanyFacts.Vendor Vendor { get; init; }

        public required CompanyFacts.Site Site { get; set; }

        public CompanyFacts.Site[] OtherSites { get; set; } = [];

        public string FamilyName { get; set; } = "";

        public int FamilyIndex { get; set; }

        public int FamilySize { get; set; }

        public string Role { get; set; } = "framework";

        public int Year { get; set; }

        public DateOnly Signed { get; set; }

        public DateOnly Ends { get; set; }

        public int TermMonths { get; set; }

        public int Fee { get; set; }

        public int ValueEur { get; set; }

        public string Status { get; set; } = "active";

        public bool Confidential { get; set; }

        public string[] Audience { get; set; } = [];

        public CompanyFacts.Vendor? HandoverFrom { get; set; }

        public Contract? Framework { get; set; }

        public Contract? Predecessor { get; set; }

        public Contract? Companion { get; set; }

        public Contract? Rival { get; set; }

        public Contract[] Siblings { get; set; } = [];

        public string Id { get; set; } = "";

        public string[] RelatedIds { get; set; } = [];

        public PeopleSampleData.Employee Requester { get; set; } = null!;

        public PeopleSampleData.Employee Owner { get; set; } = null!;

        public bool OwnerHandlesVendor { get; set; }

        public PeopleSampleData.Policy[] Policies { get; set; } = [];

        public string CostCenter { get; set; } = "";

        public string Approver { get; set; } = "";

        public string[] History { get; set; } = [];

        public string? Termination { get; set; }

        public Dictionary<string, string> Tokens { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<(string Heading, string Text)> Sections { get; set; } = [];

        public string SiteNames => OtherSites.Length == 0
            ? Site.Name
            : $"{Site.Name} and {string.Join(", ", OtherSites.Select(site => site.Name))}";

        public string[] SiteNameList => [Site.Name, .. OtherSites.Select(site => site.Name)];
    }

    /// <summary>Every agreement of the company, in identifier order, with the tokens of its facts and the text of its sections.</summary>
    internal static IReadOnlyList<Contract> Build()
    {
        var rng = new Deterministic("quorum-company-contract-graph-1");
        var drafts = new List<Contract>();

        // Pass 1: one family per vendor, so every vendor of the company has agreements of its own.
        foreach (var (vendor, index) in CompanyFacts.Vendors.Select((vendor, index) => (vendor, index)))
        {
            var kind = KindOf(vendor, index);
            AddFamily(drafts, kind, SiteFor(kind, index, rng), vendor, rng, ChainLength(kind, rng));
        }

        // Pass 2: the needs of the company, kind by kind and site by site, until the agreements are the agreed number.
        var stream = CandidateFamilies();
        for (var n = 0; drafts.Count < ContractCount; n++)
        {
            var (kind, site) = stream[n % stream.Count];
            AddFamily(drafts, kind, site, LeastLoaded(kind, drafts), rng, Math.Min(ChainLength(kind, rng), ContractCount - drafts.Count));
        }

        // No vendor is left with fewer than five agreements: the short ones gain a work order, the busiest give up the same number.
        foreach (var vendor in CompanyFacts.Vendors.Where(vendor => CountOf(drafts, vendor) < 5).ToList())
        {
            AddWorkOrder(drafts, vendor, rng);
        }

        // No site is left out of the story: a contract of a kind that fits it is moved there.
        foreach (var site in CompanyFacts.Sites.Where(site => drafts.All(contract => contract.Site != site)).ToList())
        {
            var movable = drafts
                .Where(contract => contract.Kind.SiteKinds.Contains(site.Kind, StringComparer.Ordinal) && contract.Role != "framework")
                .GroupBy(contract => contract.Site)
                .OrderByDescending(group => group.Count())
                .SelectMany(group => group)
                .FirstOrDefault();
            if (movable is null)
            {
                continue;
            }

            movable.Site = site;
            movable.FamilyId = $"{movable.Kind.Id}@{site.Id}";
            movable.FamilyName = $"{movable.Kind.Id.Replace('-', ' ')} requirement at {site.Name}";
            movable.OtherSites = [.. movable.OtherSites.Where(other => other != site)];
        }

        for (var guard = 0; drafts.Count > ContractCount && guard < 400; guard++)
        {
            var busiest = CompanyFacts.Vendors
                .Where(vendor => CountOf(drafts, vendor) > 5)
                .OrderByDescending(vendor => CountOf(drafts, vendor))
                .ThenBy(vendor => vendor.Name, StringComparer.Ordinal)
                .FirstOrDefault();
            var drop = busiest is null
                ? null
                : drafts.LastOrDefault(contract => contract.Vendor == busiest && contract.Role != "framework" && contract.FamilySize > 1);
            if (drop is null)
            {
                break;
            }

            drafts.Remove(drop);
        }

        while (drafts.Count < ContractCount)
        {
            var (kind, site) = stream[drafts.Count % stream.Count];
            AddFamily(drafts, kind, site, LeastLoaded(kind, drafts), rng, 1);
        }

        Reindex(drafts);
        return Materialise(drafts);
    }

    private static CompanyFacts.ContractKind KindOf(CompanyFacts.Vendor vendor, int index)
    {
        var kinds = CompanyFacts.Kinds.Where(kind => kind.VendorCategories.Contains(vendor.Category, StringComparer.Ordinal)).ToArray();
        return kinds[index % kinds.Length];
    }

    private static int CountOf(List<Contract> drafts, CompanyFacts.Vendor vendor) => drafts.Count(contract => contract.Vendor == vendor);

    private static List<(CompanyFacts.ContractKind Kind, CompanyFacts.Site Site)> CandidateFamilies()
    {
        var perKind = CompanyFacts.Kinds
            .Select(kind => (kind, sites: CompanyFacts.Sites.Where(site => kind.SiteKinds.Contains(site.Kind, StringComparer.Ordinal)).ToArray()))
            .ToList();
        var stream = new List<(CompanyFacts.ContractKind, CompanyFacts.Site)>();
        for (var slot = 0; slot < perKind.Max(pair => pair.sites.Length); slot++)
        {
            foreach (var (kind, sites) in perKind)
            {
                if (slot < sites.Length)
                {
                    stream.Add((kind, sites[slot]));
                }
            }
        }

        return stream;
    }

    private static void AddFamily(List<Contract> drafts, CompanyFacts.ContractKind kind, CompanyFacts.Site site, CompanyFacts.Vendor vendor, Deterministic rng, int length)
    {
        var familyId = $"{kind.Id}@{site.Id}";
        for (var suffix = 2; drafts.Any(contract => contract.FamilyId == familyId); suffix++)
        {
            familyId = $"{kind.Id}@{site.Id}-{suffix}";
        }

        var pattern = Pattern(kind, rng, length);

        // A quarter of the requirements are recent, so the collection has live agreements as well as papers that ended years ago.
        var startYear = rng.Chance(25) ? 2024 + rng.Next(0, 2) : 2018 + rng.Next(0, 5);
        var elsewhere = CompanyFacts.Sites.Where(other => other.Id != site.Id && kind.SiteKinds.Contains(other.Kind, StringComparer.Ordinal)).ToArray();
        CompanyFacts.Vendor current = vendor;
        Contract? previous = null;

        for (var index = 0; index < pattern.Length; index++)
        {
            var role = pattern[index];
            if (previous is not null && role == "renewal" && rng.Chance(25))
            {
                current = OtherVendor(kind, current, rng);
            }

            var contract = Shape(familyId, kind, site, current, rng, startYear, previous, role);
            if (previous is not null && contract.Vendor != previous.Vendor)
            {
                contract.HandoverFrom = previous.Vendor;
            }

            if (elsewhere.Length > 0 && rng.Chance(30))
            {
                contract.OtherSites = [.. elsewhere.Take(rng.Next(1, Math.Min(3, elsewhere.Length) + 1))];
            }

            previous = contract;
            drafts.Add(contract);
        }
    }

    /// <summary>One more work order on the family of a vendor that has too few agreements.</summary>
    private static void AddWorkOrder(List<Contract> drafts, CompanyFacts.Vendor vendor, Deterministic rng)
    {
        var family = drafts.Where(contract => contract.Vendor == vendor)
            .GroupBy(contract => contract.FamilyId)
            .OrderByDescending(group => group.Count())
            .First()
            .Key;
        var parent = drafts.Where(contract => contract.FamilyId == family).OrderBy(contract => contract.Signed).Last();
        drafts.Add(Shape(family, parent.Kind, parent.Site, vendor, rng, parent.Year, parent, "work_order"));
    }

    /// <summary>The dates, the money, the state and the people of one agreement, from the place it holds in its family.</summary>
    private static Contract Shape(
        string familyId, CompanyFacts.ContractKind kind, CompanyFacts.Site site, CompanyFacts.Vendor vendor,
        Deterministic rng, int startYear, Contract? previous, string role)
    {
        var longest = Math.Max(24, kind.MaxTermMonths);
        var term = role switch
        {
            "framework" or "renewal" => 12 * rng.Next(Math.Max(1, kind.MinTermMonths / 12), longest / 12),
            "amendment" or "extension" => 3 + rng.Next(0, 10),
            _ => 4 + rng.Next(0, 9),
        };

        var signed = SignedOn(rng, startYear, previous, role, term);
        var ends = signed.AddMonths(term);
        var status = StatusOf(signed, ends, rng, role);
        if (status == "terminated")
        {
            ends = signed.AddMonths(Math.Max(3, term / 2));
        }

        var fee = FeeFor(kind, site, rng, role);
        var draft = new Contract
        {
            FamilyId = familyId,
            Kind = kind,
            Site = site,
            Vendor = vendor,
            Role = role,
            Year = signed.Year,
            Signed = signed,
            Ends = ends,
            TermMonths = term,
            Fee = fee,
            ValueEur = ValueOf(kind, fee, term, role),
            Status = status,
            Confidential = kind.ConfidentialByDefault ? rng.Chance(85) : rng.Chance(18),
        };

        draft.FamilyName = $"{kind.Id.Replace('-', ' ')} requirement at {site.Name}";
        draft.Audience = AudienceFor(kind, rng);
        draft.Policies = PoliciesFor(kind, rng);
        draft.Requester = PeopleOf(draft, kind.Department, $"requester-{kind.Id}", rng);
        draft.Owner = OwnerFor(vendor, kind, draft, rng);
        draft.CostCenter = $"CC-{kind.Department[..Math.Min(3, kind.Department.Length)].ToUpperInvariant()}-{1 + (Hash(site.Id + kind.Department) % 40):00}";
        draft.Approver = draft.ValueEur switch
        {
            <= 10000 => "the budget owner of the department",
            <= 100000 => $"the head of {kind.Department}",
            _ => "the executive committee",
        };
        draft.History = [.. Enumerable.Range(0, 1 + rng.Next(0, 2)).Select(_ => rng.Of(CompanyFacts.HistoryEvents))];
        draft.Termination = status == "terminated" ? rng.Of(CompanyFacts.TerminationReasons) : null;
        return draft;
    }

    /// <summary>When it was signed: a framework opens the family, the rest of the chain lives inside the paper that came before it.</summary>
    private static DateOnly SignedOn(Deterministic rng, int startYear, Contract? previous, string role, int term)
    {
        var signed = role switch
        {
            "framework" => new DateOnly(startYear, rng.Next(1, 10), rng.Next(1, 27)),
            "renewal" => previous!.Ends.AddDays(-rng.Next(15, 75)),
            "amendment" => previous!.Signed.AddDays(rng.Next(45, 200)),
            "extension" => previous!.Ends.AddDays(-rng.Next(10, 60)),
            _ => previous!.Signed.AddDays(rng.Next(30, Math.Max(31, Math.Min(330, previous!.TermMonths * 28)))),
        };

        if (previous is not null && signed > previous.Ends)
        {
            signed = previous.Signed.AddDays(rng.Next(10, Math.Max(11, previous.TermMonths * 27)));
        }

        // Nothing is signed after the day the sample thinks it is: a paper with a future signature would contradict its own state.
        return signed > AsOf ? new DateOnly(2025, rng.Next(1, 10), rng.Next(1, 27)) : signed;
    }

    private static int FeeFor(CompanyFacts.ContractKind kind, CompanyFacts.Site site, Deterministic rng, string role)
    {
        var size = Math.Clamp(site.AreaSqm / 3200.0, 0.55, 2.6);
        var fee = (int)Math.Round(kind.BaseFee * size * (0.85 + rng.Next(0, 35) / 100.0) / 50.0) * 50;
        return role switch
        {
            "amendment" => Math.Max(200, fee / 10),
            "extension" => Math.Max(300, fee / 5),
            "work_order" when kind.FeeBasis == "per_work_order" => Math.Max(400, fee / 2),
            _ => Math.Max(300, fee),
        };
    }

    private static int ValueOf(CompanyFacts.ContractKind kind, int fee, int term, string role) => kind.FeeBasis switch
    {
        "annual" => fee * Math.Max(1, term / 12),
        "per_work_order" => role == "framework" ? fee * (1 + term / 6) : fee,
        _ => fee * term,
    };

    private static string StatusOf(DateOnly signed, DateOnly ends, Deterministic rng, string role)
    {
        if (signed.Year >= 2025 && rng.Chance(58))
        {
            return rng.Chance(50) ? "draft" : "under_review";
        }

        if (ends < AsOf)
        {
            return role is "amendment" or "extension" || !rng.Chance(7) ? "expired" : "terminated";
        }

        return rng.Chance(4) ? "under_review" : "active";
    }

    private static string[] AudienceFor(CompanyFacts.ContractKind kind, Deterministic rng)
    {
        var first = rng.Of(kind.AudiencePool);
        if (!rng.Chance(55) || kind.AudiencePool.Length < 2)
        {
            return [first];
        }

        var second = rng.Of(kind.AudiencePool);
        return second == first ? [first] : [first, second];
    }

    private static PeopleSampleData.Policy[] PoliciesFor(CompanyFacts.ContractKind kind, Deterministic rng)
    {
        var pool = kind.PolicyIds.Select(id => PeopleSampleData.Policies.Single(policy => policy.Id == id)).ToArray();
        return pool.Length == 1 || !rng.Chance(65) ? [pool[0]] : [pool[0], pool[rng.Next(1, pool.Length - 1)]];
    }

    private static PeopleSampleData.Employee PeopleOf(Contract draft, string department, string salt, Deterministic rng)
    {
        var pool = PeopleSampleData.Employees.Where(employee => employee.Department == department).OrderBy(employee => employee.Id).ToArray();
        return pool[(int)((Hash(salt + draft.FamilyId) + (uint)rng.Next(0, 7)) % (uint)pool.Length)];
    }

    /// <summary>
    /// The commercial owner: one of the people who really deal with that vendor in <see cref="PeopleSampleData"/>, so that "who looks
    /// after Acme" and the contract that names them answer the same thing. When nobody does, the buyer of record comes from the
    /// department of the requirement and <see cref="Contract.OwnerHandlesVendor"/> keeps the text from claiming a relation the directory
    /// does not show.
    /// </summary>
    private static PeopleSampleData.Employee OwnerFor(CompanyFacts.Vendor vendor, CompanyFacts.ContractKind kind, Contract draft, Deterministic rng)
    {
        var deals = PeopleSampleData.Employees.Where(employee => employee.VendorContacts.Contains(vendor.Name, StringComparer.Ordinal)).ToArray();
        var commercial = deals.Where(employee => employee.Department is "procurement" or "legal" or "finance" or "hr").ToArray();
        var pool = commercial.Length > 0 ? commercial : deals.Length > 0 ? deals : PeopleSampleData.Employees.Where(employee => employee.Department == kind.Department).ToArray();
        var owner = pool[(int)((Hash(vendor.Name + draft.FamilyId) + (uint)rng.Next(0, 9)) % (uint)pool.Length)];
        draft.OwnerHandlesVendor = owner.VendorContacts.Contains(vendor.Name, StringComparer.Ordinal);
        return owner;
    }

    private static CompanyFacts.Vendor OtherVendor(CompanyFacts.ContractKind kind, CompanyFacts.Vendor vendor, Deterministic rng)
    {
        var others = CompanyFacts.VendorsFor(kind).Where(other => other != vendor).ToArray();
        return others.Length == 0 ? vendor : others[rng.Next(0, others.Length - 1)];
    }

    private static CompanyFacts.Vendor LeastLoaded(CompanyFacts.ContractKind kind, List<Contract> drafts) =>
        CompanyFacts.VendorsFor(kind)
            .Select(vendor => (vendor, count: drafts.Count(contract => contract.Vendor == vendor)))
            .OrderBy(pair => pair.count)
            .ThenBy(pair => pair.vendor.Name, StringComparer.Ordinal)
            .First()
            .vendor;

    private static CompanyFacts.Site SiteFor(CompanyFacts.ContractKind kind, int index, Deterministic rng)
    {
        var sites = CompanyFacts.Sites.Where(site => kind.SiteKinds.Contains(site.Kind, StringComparer.Ordinal)).ToArray();
        return sites[(index * 5 + rng.Next(0, sites.Length)) % sites.Length];
    }

    private static int ChainLength(CompanyFacts.ContractKind kind, Deterministic rng) => kind.FeeBasis switch
    {
        "per_work_order" => 2 + rng.Next(0, 3),
        "annual" => 2 + rng.Next(0, 2),
        _ => 2 + rng.Next(0, 4),
    };

    private static string[] Pattern(CompanyFacts.ContractKind kind, Deterministic rng, int length)
    {
        string[][] shapes =
        [
            ["framework", "work_order", "renewal"],
            ["framework", "work_order", "work_order", "renewal"],
            ["framework", "amendment", "work_order", "extension", "renewal"],
            ["framework", "renewal"],
            ["framework", "work_order", "extension", "renewal"],
            ["framework", "amendment", "renewal", "work_order"],
            ["framework", "work_order", "amendment", "work_order", "renewal"],
        ];

        if (length <= 1)
        {
            return ["framework"];
        }

        var shape = shapes[(Hash(kind.Id) + (uint)rng.Next(0, shapes.Length)) % shapes.Length];
        return length >= shape.Length ? shape : [.. shape.Take(length)];
    }

    /// <summary>The place of every agreement in its family, after the list was repaired: the chain is the order of signature.</summary>
    private static void Reindex(List<Contract> drafts)
    {
        foreach (var family in drafts.GroupBy(contract => contract.FamilyId))
        {
            var chain = family.OrderBy(contract => contract.Signed).ToArray();
            for (var index = 0; index < chain.Length; index++)
            {
                chain[index].FamilyIndex = index;
                chain[index].FamilySize = chain.Length;
                chain[index].Framework = index == 0 ? null : chain[0];
                chain[index].Predecessor = index == 0 ? null : chain[index - 1];
                chain[index].Role = index == 0 ? "framework" : chain[index].Role;
            }
        }
    }

    /// <summary>Identifiers in date order, then the cross references that only make sense once every agreement has one, then the text.</summary>
    private static Contract[] Materialise(List<Contract> drafts)
    {
        var ordered = drafts
            .OrderBy(contract => contract.Signed)
            .ThenBy(contract => contract.FamilyId, StringComparer.Ordinal)
            .ThenBy(contract => contract.FamilyIndex)
            .ToArray();

        for (var index = 0; index < ordered.Length; index++)
        {
            ordered[index].Id = $"ct-{index + 1:000}";
            ordered[index].Siblings = [.. ordered.Where(other => other.FamilyId == ordered[index].FamilyId && other != ordered[index]).Take(3)];
        }

        var bySite = ordered.GroupBy(contract => contract.Site.Id).ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var byKind = ordered.GroupBy(contract => contract.Kind.Id).ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        foreach (var contract in ordered)
        {
            var rng = new Deterministic("links-" + contract.Id);
            contract.Companion = OtherIn(bySite[contract.Site.Id], contract, other => other.Vendor != contract.Vendor, rng);
            contract.Rival = OtherIn(byKind[contract.Kind.Id], contract, other => other.Site.Id != contract.Site.Id, rng);
            contract.RelatedIds =
            [
                .. new[] { contract.Framework?.Id, contract.Predecessor?.Id, contract.Companion?.Id, contract.Rival?.Id }
                    .Concat(contract.Siblings.Select(sibling => sibling.Id))
                    .Where(id => id is not null && id != contract.Id)
                    .Select(id => id!)
                    .Distinct(StringComparer.Ordinal)
                    .Take(5),
            ];

            CompanyContractProse.TokensOf(contract, rng);
            contract.Sections = CompanyContractProse.SectionsOf(contract, rng);
        }

        return ordered;
    }

    private static Contract? OtherIn(List<Contract> pool, Contract self, Func<Contract, bool> fits, Deterministic rng)
    {
        var candidates = pool.Where(other => other != self && fits(other)).ToArray();
        return candidates.Length == 0 ? null : candidates[rng.Next(0, candidates.Length - 1)];
    }

    internal static string Pretty(DateOnly date) => $"{date.Day} {date.ToString("MMMM", CultureInfo.InvariantCulture)} {date.Year}";

    internal static string Euros(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    internal static uint Hash(string text)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var character in text)
            {
                hash ^= character;
                hash *= 16777619u;
            }

            return hash;
        }
    }

    /// <summary>A small deterministic generator: the same seed always gives the same company, in every process and on every machine.</summary>
    internal sealed class Deterministic(string seed)
    {
        private uint state = CompanyContractGraph.Hash(seed) | 1u;

        public int Next(int minInclusive, int maxInclusive) => minInclusive + (int)(NextRaw() % (uint)(maxInclusive - minInclusive + 1));

        public T Of<T>(IReadOnlyList<T> items) => items[(int)(NextRaw() % (uint)items.Count)];

        public bool Chance(int percent) => Next(0, 99) < percent;

        public uint NextRaw()
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }

        private static uint Hash(string seed) => CompanyContractGraph.Hash(seed);
    }
}
