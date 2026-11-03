using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Quorum.SampleKnowledgeBase;

/// <summary>
/// The text of the contracts: the sections of one agreement, written from the facts the graph decided for it. Every section is a
/// paragraph of its own, so a chunk reads as a clause and not as half of one, and no two agreements of the same kind read alike: the
/// numbers, the sites, the people, the dates, the related agreements and the order of the clauses all change from one to the next.
/// </summary>
internal static class CompanyContractProse
{
    private static readonly int[] NoticeDays = [30, 45, 60, 90, 120];

    private static readonly string[] Frequencies = ["monthly", "quarterly", "twice a month", "every two months"];

    private static readonly string[] Uptimes = ["99.5", "99.9", "99.95", "99.99"];

    private static readonly string[] Openings =
    [
        "This @ROLE@ agreement (@ID@) between the company and @VENDOR@ covers the @DEPT@ requirement at @S@.",
        "@VENDOR@ holds this @ROLE@ (@ID@) for @KIND@ at @S@, awarded by @DEPT@.",
        "Agreement @ID@ places the @KIND@ of @S@ with @VENDOR@, on the request of the @DEPT@ function.",
        "The scope of @ID@ is the @KIND@ the company needs at @S@, performed by @VENDOR@ for @DEPT@.",
        "Under this @ROLE@, numbered @ID@, the company buys @KIND@ from @VENDOR@ at @S@ and where the annex says.",
        "The register lists this @ROLE@ as @ID@: @VENDOR@ performs the @KIND@ at @S@ for @DEPT@.",
        "@KINDTITLE@ for @S@: the company signs this @ROLE@ (@ID@) with @VENDOR@ after the requisition opened by @REQ@.",
        "The @SITES@ requirement of @DEPT@ is carried by @VENDOR@ under this @ROLE@, number @ID@ of the register.",
    ];

    private static readonly string[] SiteNotes =
    [
        "@S@ is @FLOORS@ floors of @AREA@ square metres with @DESKS@ seats, opened in @OPENED@: @NOTE@.",
        "The site the work is done at, @S@, has @FLOORS@ floors, @AREA@ square metres and @DESKS@ seats; @NOTE@.",
        "Description of the place, from the site register: @S@, @FLOORS@ floors, @AREA@ square metres, @DESKS@ seats since @OPENED@ — @NOTE@.",
    ];

    private static readonly string[] Exclusions =
    [
        "Out of scope here: the works of the building itself, which stay with the maintenance agreement, and anything the annex of @S@ does not name.",
        "The agreement does not cover the shifts of the weekend at @S@ unless the site manager asks in writing @D@ days ahead.",
        "Excluded: the work of the company's own people, the consumables not in the annex, and the parking areas outside @S@.",
        "The scope stops at the boundary of @S@; the other sites of the group keep their own agreements.",
        "The same scope in @S2@ is not part of this paper: it stays with @VENDOR2@ until that agreement ends.",
    ];

    private static readonly string[] PeopleTemplates =
    [
        "The commercial owner named in the register is @PM@ (@PMROLE@, @PMDEPT@), and the request that opened the file came from @REQ@ (@REQROLE@).",
        "@PM@ (@PMROLE@) administers this agreement; the department contact is @REQ@ (@REQROLE@), who signed the requisition for @S@.",
        "The file lists @PM@ as the owner on the company side and @REQ@ (@REQROLE@) as the requester for @S@.",
    ];

    private static readonly string[] VendorContactTails =
    [
        " @PM@ already deals with @VENDOR@ for the group, which is why the escalation path is short.",
        " @PM@ is the contact of record for @VENDOR@ in the directory of the company.",
        string.Empty,
        string.Empty,
    ];

    private static readonly string[] PolicyTemplates =
    [
        "The rules of the handbook that govern it are @POLICY@ and @POLICY2@.",
        "It is applied with the handbook rules @POLICY@ and @POLICY2@, which the internal audit reads together with this agreement.",
        "Nothing here changes what @POLICY@ requires of the company; @POLICY2@ sets how the papers are kept.",
    ];

    private static readonly string[] TermTemplates =
    [
        "Signed on @START@, the agreement runs for @TERM@ months to @END@ and can be ended by either party with @NOTICE@ days' written notice.",
        "The term is @TERM@ months from @START@ to @END@; @NOTICE@ days' written notice ends it, and the company may end it at any month end after the first @TERM2@ months.",
        "Start @START@, end @END@, @TERM@ months in all, with @NOTICE@ days' notice from either side and no automatic renewal without a written order.",
    ];

    /// <summary>An amendment or an extension does not open a term: it moves the one already running.</summary>
    private static readonly string[] ChangeTermTemplates =
    [
        "This paper runs from @START@ and ends with the agreement it changes on @END@; nothing else in the term of @TERM@ months is moved.",
        "Signed on @START@, it adds @TERM@ months to the term already running and keeps the notice of @NOTICE@ days as it was.",
        "It takes effect on @START@ and expires with the parent agreement on @END@, with the same @NOTICE@ days' notice as that agreement.",
    ];

    private static readonly string[] DraftTemplates =
    [
        "The paper was issued for review on @START@: signature is expected once @POLICY@ is satisfied and legal clears the indemnity clause.",
        "This is a draft circulated on @START@, not yet countersigned; the annex with the rates is still open at @S@.",
        "Issued on @START@ and under review, the agreement is held back by the comment of @PM@ on the service credits.",
    ];

    private static readonly string[] UnderReviewTemplates =
    [
        "The agreement is under review on @START@: procurement is testing the rates against the market before the signature.",
        "Marked under review since @START@, after the @DEPT@ asked for one more quote for @S@.",
    ];

    private static readonly string[] HandoverTemplates =
    [
        "The scope moved from @PREVIOUS@ to @VENDOR@ on @START@: @PREVIOUS@ kept the equipment and the crew did not enter @S@ again.",
        "@VENDOR@ took over from @PREVIOUS@ at @S@; the transition of @TERM2@ weeks was paid by the outgoing supplier, as the exit clause required.",
        "This renewal ends the years of @PREVIOUS@ on the scope, after the review of @DATE@ scored the service below the threshold.",
    ];

    private static readonly string[] TerminationTemplates =
    [
        "The agreement was ended early on @END@: @TERMINATION@.",
        "It did not run to its term; it was terminated on @END@ because @TERMINATION@.",
    ];

    /// <summary>The values the clause pools refer to, decided once for the agreement so every section quotes the same facts.</summary>
    internal static void TokensOf(CompanyContractGraph.Contract contract, CompanyContractGraph.Deterministic rng)
    {
        var kind = contract.Kind;
        var site = contract.Site;
        // The second site the clauses talk about: another place of the group, never the same one spelled twice.
        var other = contract.OtherSites.Length > 0 ? contract.OtherSites[0] : contract.Rival?.Site;
        var companion = contract.Companion?.Vendor.Name ?? contract.Rival?.Vendor.Name ?? OtherVendorName(kind, contract.Vendor, rng);
        var notice = NoticeDays[(int)(CompanyContractGraph.Hash(contract.Id + "notice") % (uint)NoticeDays.Length)];
        var first = contract.Framework?.Id ?? contract.Predecessor?.Id ?? contract.RelatedIds.FirstOrDefault() ?? contract.Id;
        var policies = contract.Policies;

        var tokens = contract.Tokens;
        tokens["ID"] = contract.Id;
        tokens["VENDOR"] = contract.Vendor.Name;
        tokens["VENDOR2"] = companion;
        tokens["PREVIOUS"] = contract.HandoverFrom?.Name ?? companion;
        tokens["S"] = site.Name;
        tokens["SITES"] = contract.SiteNames;
        tokens["S2"] = other?.Name ?? site.Name;
        tokens["C"] = site.City;
        tokens["C2"] = other?.City ?? site.City;
        tokens["FLOORS"] = site.Floors.ToString(CultureInfo.InvariantCulture);
        tokens["AREA"] = CompanyContractGraph.Euros(site.AreaSqm);
        tokens["DESKS"] = CompanyContractGraph.Euros(site.Desks);
        tokens["OPENED"] = site.Opened[..4];
        tokens["NOTE"] = site.Note.TrimEnd('.');
        tokens["KIND"] = Lower(kind.Title);
        tokens["KINDTITLE"] = kind.Title;
        tokens["DEPT"] = kind.Department;
        tokens["ROLE"] = RoleWords(contract);
        tokens["TERM"] = contract.TermMonths.ToString(CultureInfo.InvariantCulture);
        tokens["TERM2"] = Math.Max(3, contract.TermMonths / 3).ToString(CultureInfo.InvariantCulture);
        tokens["NOTICE"] = notice.ToString(CultureInfo.InvariantCulture);
        tokens["START"] = CompanyContractGraph.Pretty(contract.Signed);
        tokens["END"] = CompanyContractGraph.Pretty(contract.Ends);
        tokens["Y"] = contract.Year.ToString(CultureInfo.InvariantCulture);
        tokens["Y2"] = (contract.Year + 1 + (int)(CompanyContractGraph.Hash(contract.Id) % 3u)).ToString(CultureInfo.InvariantCulture);
        tokens["FEE"] = CompanyContractGraph.Euros(contract.Fee);
        tokens["TOTAL"] = CompanyContractGraph.Euros(contract.ValueEur);
        tokens["CADENCE"] = kind.FeeBasis == "annual" ? "quarterly" : kind.FeeBasis == "per_work_order" ? "on the order" : "monthly";
        tokens["Q"] = rng.Of(Frequencies);
        tokens["N"] = rng.Next(2, 9).ToString(CultureInfo.InvariantCulture);
        tokens["M"] = rng.Next(3, 48).ToString(CultureInfo.InvariantCulture);
        tokens["BIG"] = (100 * rng.Next(1, 26)).ToString(CultureInfo.InvariantCulture);
        tokens["PCT"] = rng.Next(2, 18).ToString(CultureInfo.InvariantCulture);
        tokens["UPTIME"] = rng.Of(Uptimes);
        tokens["SLA"] = rng.Next(62, 98).ToString(CultureInfo.InvariantCulture);
        tokens["CREDIT"] = rng.Next(3, 25).ToString(CultureInfo.InvariantCulture);
        tokens["D"] = rng.Next(5, 90).ToString(CultureInfo.InvariantCulture);
        tokens["H"] = rng.Next(1, 24).ToString(CultureInfo.InvariantCulture);
        tokens["H2"] = rng.Next(2, 5).ToString(CultureInfo.InvariantCulture);
        tokens["T"] = rng.Next(5, 95).ToString(CultureInfo.InvariantCulture);
        tokens["G"] = (100 * rng.Next(2, 21)).ToString(CultureInfo.InvariantCulture);
        tokens["CENTS"] = rng.Next(3, 14).ToString(CultureInfo.InvariantCulture);
        tokens["CO2"] = rng.Next(60, 140).ToString(CultureInfo.InvariantCulture);
        tokens["SCORE"] = rng.Next(78, 99).ToString(CultureInfo.InvariantCulture);
        tokens["TEN"] = rng.Next(74, 97).ToString(CultureInfo.InvariantCulture).Insert(1, ".");
        tokens["RATE"] = (10 * rng.Next(9, 39)).ToString(CultureInfo.InvariantCulture);
        tokens["SMALL"] = (50 * rng.Next(5, 190)).ToString(CultureInfo.InvariantCulture);
        tokens["DATE"] = CompanyContractGraph.Pretty(DateInTerm(contract, rng, 0.2));
        tokens["DATE2"] = CompanyContractGraph.Pretty(DateInTerm(contract, rng, 0.55));
        tokens["DATE3"] = CompanyContractGraph.Pretty(DateInTerm(contract, rng, 0.85));
        tokens["CC"] = contract.CostCenter;
        tokens["APPROVER"] = contract.Approver;
        tokens["PM"] = contract.Owner.Name;
        tokens["PMROLE"] = contract.Owner.Role;
        tokens["PMDEPT"] = contract.Owner.Department;
        tokens["REQ"] = contract.Requester.Name;
        tokens["REQROLE"] = contract.Requester.Role;
        tokens["POLICY"] = policies.Length > 0 ? policies[0].Title : "purchasing approval thresholds";
        tokens["POLICYID"] = policies.Length > 0 ? policies[0].Id : "purchasing-thresholds";
        tokens["POLICY2"] = policies.Length > 1 ? policies[1].Title : "vendor onboarding checklist";
        tokens["POLICYID2"] = policies.Length > 1 ? policies[1].Id : "vendor-onboarding";
        tokens["RELATED"] = first;
        tokens["RELATED2"] = contract.Siblings.FirstOrDefault()?.Id ?? contract.Companion?.Id ?? contract.Rival?.Id ?? first;
        tokens["RIVAL"] = contract.Rival?.Id ?? first;
        tokens["RIVALVENDOR"] = contract.Rival?.Vendor.Name ?? companion;
        tokens["RIVALSITE"] = contract.Rival?.Site.Name ?? other?.Name ?? site.Name;
        tokens["COMPANION"] = contract.Companion?.Id ?? first;
        tokens["COMPANIONVENDOR"] = contract.Companion?.Vendor.Name ?? companion;
        tokens["COMPANIONKIND"] = Lower(contract.Companion?.Kind.Title ?? kind.Title);
        tokens["FAMILY"] = contract.FamilyName;
        tokens["FAMILYINDEX"] = (contract.FamilyIndex + 1).ToString(CultureInfo.InvariantCulture);
        tokens["FAMILYSIZE"] = contract.FamilySize.ToString(CultureInfo.InvariantCulture);
        tokens["TERMINATION"] = contract.Termination is null ? "the term ran out" : Fill(contract.Termination, tokens);
    }

    /// <summary>
    /// The articles of one agreement, each one its own chunk: what is bought, the money, who runs the file, what is measured, the
    /// clauses of compliance the kind really has, the term, the exit, the related agreements and what happened. One article is one
    /// topic, so the vector of the chunk is about that topic and not about five of them at once.
    /// </summary>
    internal static IReadOnlyList<(string Heading, string Text)> SectionsOf(CompanyContractGraph.Contract contract, CompanyContractGraph.Deterministic rng)
    {
        var (scope, levels) = CompanyFacts.ArticlesOf(contract.Kind.Id);
        var articles = new List<(string Heading, IReadOnlyList<string> Body)>
        {
            Scope(contract, rng, scope),
            Money(contract, rng),
            Management(contract, rng),
        };

        if (contract.Role is not ("amendment" or "extension"))
        {
            articles.Add(Levels(contract, rng, levels));
            articles.Add(Reporting(contract, rng));
            articles.AddRange(Compliance(contract, rng));
        }

        articles.Add(Term(contract, rng));

        if (contract.Role is not ("amendment" or "extension") || rng.Chance(50))
        {
            articles.Add(Transition(contract, rng));
        }

        articles.Add(Relations(contract, rng));
        articles.Add(History(contract, rng));

        return [.. articles.Select((article, index) => (article.Heading, Numbered(article, contract, index)))];
    }

    /// <summary>The heading of the article, its reference in the agreement and its text: the reference is what makes the chunk unique.</summary>
    private static string Numbered((string Heading, IReadOnlyList<string> Body) article, CompanyContractGraph.Contract contract, int index)
    {
        var body = string.Join(" ", article.Body.Where(sentence => !string.IsNullOrWhiteSpace(sentence)).Select(Sentence));
        return $"{article.Heading} ({contract.Id}, clause {index + 1}). {Cap(body)}";
    }

    private static (string, IReadOnlyList<string>) Scope(CompanyContractGraph.Contract contract, CompanyContractGraph.Deterministic rng, string heading)
    {
        var body = new List<string>(Pick(rng, Openings, 1, contract.Tokens));
        body.AddRange(Pick(rng, contract.Kind.Deliverables, Math.Min(1 + rng.Next(0, 2), contract.Kind.Deliverables.Length), contract.Tokens));
        if (contract.Role == "framework" && rng.Chance(55))
        {
            body.AddRange(Pick(rng, SiteNotes, 1, contract.Tokens));
        }

        return (heading, body);
    }

    private static (string, IReadOnlyList<string>) Money(CompanyContractGraph.Contract contract, CompanyContractGraph.Deterministic rng)
    {
        var body = new List<string>(Pick(rng, CompanyFacts.PaymentClauses, 1, contract.Tokens));
        if (rng.Chance(70))
        {
            body.AddRange(Pick(rng, CompanyFacts.IndexationClauses, 1, contract.Tokens));
        }

        return ("Fees, invoicing and approvals", body);
    }

    private static (string, IReadOnlyList<string>) Management(CompanyContractGraph.Contract contract, CompanyContractGraph.Deterministic rng)
    {
        var body = new List<string>(Pick(rng, PeopleTemplates, 1, contract.Tokens));
        if (contract.OwnerHandlesVendor)
        {
            body.Add(Pick(rng, VendorContactTails, 1, contract.Tokens)[0].Trim());
        }

        if (rng.Chance(45))
        {
            body.Add(Pick(rng, Exclusions, 1, contract.Tokens)[0]);
        }

        return ("Contract management and contacts", body);
    }

    private static (string, IReadOnlyList<string>) Levels(CompanyContractGraph.Contract contract, CompanyContractGraph.Deterministic rng, string heading)
    {
        var body = Pick(rng, contract.Kind.ServiceLevels, Math.Min(1 + rng.Next(0, 2), contract.Kind.ServiceLevels.Length), contract.Tokens);
        return (heading, body);
    }

    private static (string, IReadOnlyList<string>) Reporting(CompanyContractGraph.Contract contract, CompanyContractGraph.Deterministic rng)
    {
        var body = new List<string>(Pick(rng, CompanyFacts.ReportingClauses, 1, contract.Tokens));
        if (rng.Chance(40))
        {
            body.AddRange(Pick(rng, CompanyFacts.AuditClauses, 1, contract.Tokens));
        }

        return ("Reporting and service reviews", body);
    }

    /// <summary>
    /// The clauses that are not about the service, each one an article of its own when the agreement has it: a contract about the
    /// cleaning does not speak of intellectual property, and an insurance programme does not speak of subcontracted crews.
    /// </summary>
    private static IEnumerable<(string, IReadOnlyList<string>)> Compliance(CompanyContractGraph.Contract contract, CompanyContractGraph.Deterministic rng)
    {
        yield return (
            contract.Confidential ? "Confidentiality of the agreement" : "Confidentiality and public references",
            Pick(rng, contract.Confidential ? CompanyFacts.ConfidentialClauses : CompanyFacts.OpenClauses, 1, contract.Tokens));

        if (rng.Chance(75))
        {
            yield return ("Personal data and security", Pick(rng, CompanyFacts.DataClauses, 1, contract.Tokens));
        }

        if (rng.Chance(45))
        {
            yield return ("Staff and subcontracting", Pick(rng, CompanyFacts.SubcontractingClauses, 1, contract.Tokens));
        }

        if (rng.Chance(40))
        {
            yield return ("Insurance and liability", Pick(rng, CompanyFacts.InsuranceClauses, 1, contract.Tokens));
        }

        if (rng.Chance(40))
        {
            yield return ("Intellectual property in the work", Pick(rng, CompanyFacts.IpClauses, 1, contract.Tokens));
        }

        if (rng.Chance(35))
        {
            yield return ("Environment and sustainability", Pick(rng, CompanyFacts.SustainabilityClauses, 1, contract.Tokens));
        }

        if (rng.Chance(30))
        {
            yield return ("Disputes and applicable law", Pick(rng, CompanyFacts.DisputeClauses, 1, contract.Tokens));
        }

        yield return ("The rules of the handbook it is read with", Pick(rng, PolicyTemplates, 1, contract.Tokens));
    }

    private static (string, IReadOnlyList<string>) Term(CompanyContractGraph.Contract contract, CompanyContractGraph.Deterministic rng)
    {
        IReadOnlyList<string> life = contract.Status switch
        {
            "draft" => DraftTemplates,
            "under_review" => UnderReviewTemplates,
            _ when contract.Role is "amendment" or "extension" => ChangeTermTemplates,
            _ => TermTemplates,
        };

        var body = new List<string>(Pick(rng, life, 1, contract.Tokens));
        if (contract.Status is not ("draft" or "under_review"))
        {
            body.AddRange(Pick(rng, CompanyFacts.ExitClauses, 1, contract.Tokens));
        }

        if (contract.Termination is not null)
        {
            body.AddRange(Pick(rng, TerminationTemplates, 1, contract.Tokens));
        }

        return ("Term, renewal and notice", body);
    }

    private static (string, IReadOnlyList<string>) Transition(CompanyContractGraph.Contract contract, CompanyContractGraph.Deterministic rng)
    {
        var body = new List<string>(Pick(rng, contract.Kind.ExitClauses, Math.Min(1 + rng.Next(0, 1), contract.Kind.ExitClauses.Length), contract.Tokens));
        if (rng.Chance(60))
        {
            body.AddRange(Pick(rng, CompanyFacts.HandoverClauses, 1, contract.Tokens));
        }

        if (contract.HandoverFrom is not null)
        {
            body.AddRange(Pick(rng, HandoverTemplates, 1, contract.Tokens));
        }

        return ("Transition and exit of the scope", body);
    }

    private static (string, IReadOnlyList<string>) Relations(CompanyContractGraph.Contract contract, CompanyContractGraph.Deterministic rng)
    {
        var body = new List<string>
        {
            Fill(contract.FamilySize > 1
                ? "it is number @FAMILYINDEX@ of @FAMILYSIZE@ on the @FAMILY@, the framework being @RELATED@"
                : "the @FAMILY@ is carried by this agreement alone (@ID@); the other needs of @S@ have their own papers", contract.Tokens),
        };

        if (contract.Companion is not null)
        {
            body.Add(Fill("the parallel agreement at @S@ is @COMPANION@, which @COMPANIONVENDOR@ holds for the @COMPANIONKIND@; the two are kept separate on purpose", contract.Tokens));
        }

        if (contract.Rival is not null)
        {
            body.Add(Fill("the same kind of contract in @RIVALSITE@ is @RIVAL@ with @RIVALVENDOR@, so the group can compare the rates and the service", contract.Tokens));
        }

        body.AddRange(Pick(rng, CompanyFacts.RationaleClauses, 1, contract.Tokens));
        return ("Related agreements", body);
    }

    private static (string, IReadOnlyList<string>) History(CompanyContractGraph.Contract contract, CompanyContractGraph.Deterministic rng)
    {
        var body = Pick(rng, contract.History, Math.Min(contract.History.Length, 1 + rng.Next(0, 2)), contract.Tokens);
        return ("What happened during the term", body);
    }

    private static string RoleWords(CompanyContractGraph.Contract contract) => contract.Role switch
    {
        "framework" => contract.FamilySize > 1 ? "framework" : "standalone",
        "work_order" => "statement of work",
        "amendment" => "amendment",
        "extension" => "extension",
        _ => "renewal",
    };

    /// <summary>Picks distinct items of a pool and fills them; the pool is never used twice in the same section.</summary>
    private static List<string> Pick(CompanyContractGraph.Deterministic rng, IReadOnlyList<string> pool, int count, IDictionary<string, string> tokens)
    {
        var chosen = new List<string>();
        var seen = new HashSet<int>();
        for (var guard = 0; chosen.Count < count && guard < count * 6 && seen.Count < pool.Count; guard++)
        {
            var index = rng.Next(0, pool.Count - 1);
            if (seen.Add(index))
            {
                chosen.Add(Fill(pool[index], tokens));
            }
        }

        return chosen;
    }

    private static string Sentence(string text)
    {
        var trimmed = Cap(Spacing(text.Trim()));
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        return trimmed[^1] is '.' or ':' ? trimmed : trimmed + ".";
    }

    /// <summary>
    /// Fills the placeholders of a clause with the facts of the agreement. The pools are written compactly and can leave out the space
    /// before a placeholder (<c>the@VENDOR@ team</c>): the spacing is repaired on the template, before the values go in.
    /// </summary>
    internal static string Fill(string template, IDictionary<string, string> tokens)
    {
        var builder = new StringBuilder(Placeholders(template));
        foreach (var (name, value) in tokens)
        {
            if (value.Length == 0)
            {
                continue;
            }

            builder.Replace("@" + name + "@", value);
        }

        return CollapseSpaces(builder.ToString()).Trim();
    }

    /// <summary>Puts the missing space in front of every placeholder the pools are written without ("the@VENDOR@ team" reads "the Acme team").</summary>
    private static string Placeholders(string template) => PlaceholderSpacing.Replace(template, " @$1@");

    private static readonly Regex PlaceholderSpacing = new("(?<=[A-Za-z0-9%])@([A-Z0-9]+)@", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string Spacing(string text)
    {
        var builder = new StringBuilder(text.Length + 16);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            var previous = index > 0 ? text[index - 1] : '\0';
            if ((character == ',' || character == '.' || character == ';') && previous == ' ')
            {
                builder.Length -= 1;
            }

            builder.Append(character);
        }

        return CollapseSpaces(builder.ToString());
    }

    /// <summary>Collapses the double spaces the repairs can leave behind.</summary>
    private static string CollapseSpaces(string text)
    {
        var builder = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var character in text)
        {
            if (character == ' ')
            {
                if (lastWasSpace)
                {
                    continue;
                }

                lastWasSpace = true;
            }
            else
            {
                lastWasSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>Upper-cases the first letter, except for a document identifier, which keeps its own spelling.</summary>
    private static string Cap(string text) => text.Length == 0 || text.StartsWith("ct-", StringComparison.OrdinalIgnoreCase)
        ? text
        : $"{char.ToUpperInvariant(text[0])}{text[1..]}";

    private static string Lower(string text) => text.Length == 0 ? text : $"{char.ToLowerInvariant(text[0])}{text[1..]}";

    private static DateOnly DateInTerm(CompanyContractGraph.Contract contract, CompanyContractGraph.Deterministic rng, double phase)
    {
        var span = Math.Max(30, (contract.Ends.DayNumber - contract.Signed.DayNumber) * 9 / 10);
        return contract.Signed.AddDays((int)(span * phase) + rng.Next(0, Math.Max(1, span / 4)));
    }

    private static string OtherVendorName(CompanyFacts.ContractKind kind, CompanyFacts.Vendor vendor, CompanyContractGraph.Deterministic rng)
    {
        var others = CompanyFacts.VendorsFor(kind).Where(other => other != vendor).ToArray();
        return others.Length == 0 ? vendor.Name : others[rng.Next(0, others.Length - 1)].Name;
    }
}
