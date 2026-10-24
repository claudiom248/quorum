using System.Text.RegularExpressions;
using Quorum.Web.Client.Api;

namespace Quorum.Web.Client;

/// <summary>A lookup an administrator can bind to a tool: where it comes from decides how it is named on screen.</summary>
/// <param name="KnowledgeBaseId">The knowledge base a computed or entity lookup comes from; <see langword="null"/> for a manual one.</param>
public sealed record LookupChoice(Guid Id, string Name, string Kind, Guid? KnowledgeBaseId, string? KnowledgeBaseName, bool Shared = false)
{
    /// <summary>How the list is written in the editor: the same name can come from several knowledge bases.</summary>
    public string Label => Kind switch
    {
        LookupKinds.Manual => Shared ? $"{Name} (typed by hand, shared with you)" : $"{Name} (typed by hand)",
        LookupKinds.Entity => $"{Name} (entities)",
        _ => $"{Name} · {KnowledgeBaseName}",
    };
}

/// <summary>What the lookup tool editor of the Tools page shows and reads, kept apart from the markup so it can be tested alone.</summary>
public static class LookupToolView
{
    /// <summary>
    /// Every lookup the tenant can bind: the lists of its own knowledge bases and the manual lookups it can see. A knowledge base
    /// shared by another tenant shows no lookups here, so only its manual ones can be bound.
    /// </summary>
    public static IReadOnlyList<LookupChoice> Choices(
        IEnumerable<KnowledgeBaseListItem> knowledgeBases,
        IReadOnlyDictionary<Guid, IReadOnlyList<KnowledgeBaseLookupInfo>> lookupsByKnowledgeBase,
        IEnumerable<ManualLookupInfo> manual)
    {
        ArgumentNullException.ThrowIfNull(knowledgeBases);
        ArgumentNullException.ThrowIfNull(lookupsByKnowledgeBase);
        ArgumentNullException.ThrowIfNull(manual);

        var choices = new List<LookupChoice>();
        foreach (var knowledgeBase in knowledgeBases.Where(k => !k.Shared).OrderBy(k => k.Name, StringComparer.Ordinal))
        {
            foreach (var lookup in lookupsByKnowledgeBase.GetValueOrDefault(knowledgeBase.Id) ?? [])
            {
                choices.Add(new LookupChoice(lookup.Id, lookup.Name, lookup.Kind, knowledgeBase.Id, knowledgeBase.Name));
            }
        }

        choices.AddRange(manual.OrderBy(m => m.Name, StringComparer.Ordinal).Select(m => new LookupChoice(m.Id, m.Name, LookupKinds.Manual, null, null, m.Shared)));
        return choices;
    }

    /// <summary>The same choices from the list of every lookup: one read instead of one per knowledge base.</summary>
    public static IReadOnlyList<LookupChoice> Choices(IEnumerable<LookupListItem> lookups)
    {
        ArgumentNullException.ThrowIfNull(lookups);

        return
        [
            .. lookups
                .Where(l => l.Kind == LookupListKinds.Manual || l.KnowledgeBase is not null)
                .Select(l => l.Kind == LookupListKinds.Manual
                    ? new LookupChoice(l.Id, l.Name, LookupKinds.Manual, null, null, l.Shared)
                    : new LookupChoice(l.Id, l.Name, l.Field is null ? LookupKinds.Entity : LookupKinds.Computed, l.KnowledgeBase!.Id, l.KnowledgeBase.Name))
                .OrderBy(c => c.KnowledgeBaseName ?? "￿", StringComparer.Ordinal)
                .ThenBy(c => c.Name, StringComparer.Ordinal),
        ];
    }

    /// <summary>The knowledge bases whose mapping the editor needs: the ones the tool queries and the ones its lists come from.</summary>
    public static IReadOnlyList<Guid> KnowledgeBasesInUse(LookupToolForm form, IReadOnlyList<LookupChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(choices);

        // A list of entities cannot be filtered, so the fields only its knowledge base maps are not offered: nothing would apply them.
        return
        [
            .. form.KnowledgeBaseIds
                .Concat(form.Lookups
                    .Select(l => choices.FirstOrDefault(c => c.Id == l.LookupId))
                    .Where(c => c is { Kind: not LookupKinds.Entity })
                    .Select(c => c!.KnowledgeBaseId)
                    .OfType<Guid>())
                .Distinct(),
        ];
    }

    /// <summary>The operations the model is offered, as the server generates them: <c>values</c> always, the others only when the tool queries knowledge bases.</summary>
    public static IReadOnlyList<string> Operations(bool queriesKnowledgeBases) =>
        queriesKnowledgeBases ? ["values", "documents", "count"] : ["values"];
}

/// <summary>One lookup bound to the tool and the name the model sees for it.</summary>
public sealed class LookupBindingForm(Guid lookupId)
{
    public Guid LookupId { get; } = lookupId;

    /// <summary>Empty means the lookup's own name.</summary>
    public string Alias { get; set; } = string.Empty;
}

/// <summary>The lookup tool being created or edited: what the admin typed, and its conversion into the request (spec 007 D-12).</summary>
public sealed partial class LookupToolForm : FilteredToolForm
{
    public List<LookupBindingForm> Lookups { get; } = [];

    protected override string Prefix => "lookup";

    protected override int MaxExposedFields => LookupToolLimits.MaxExposedFields;

    protected override IReadOnlyList<string> ReservedNames { get; } = ["operation", "lookup", "knowledge_base", "search", "sort", "limit", "cursor"];

    /// <param name="mapped">The fields the knowledge bases of the tool and of its lookups map, which its fixed filters may name; every dictionary field when not given.</param>
    public static LookupToolForm From(LookupToolDetail detail, IReadOnlyList<DictionaryField> dictionary, IReadOnlyList<DictionaryField>? mapped = null)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(dictionary);

        var form = new LookupToolForm
        {
            Id = detail.Id,
            OriginalName = detail.Name,
            Name = detail.Name,
            Description = detail.Description,
            Policy = detail.Policy,
            Scope = ScopeForm.From(detail.Scope),
        };
        form.Lookups.AddRange(detail.Lookups.Select(l => new LookupBindingForm(l.LookupId) { Alias = l.Alias ?? string.Empty }));
        form.KnowledgeBaseIds.AddRange(detail.KnowledgeBaseIds);
        form.LoadFilters(detail.ExposedFields, detail.FixedFilters, dictionary, mapped);
        return form;
    }

    public bool HasLookup(Guid lookupId) => Lookups.Any(l => l.LookupId == lookupId);

    public void AddLookup(Guid lookupId)
    {
        if (!HasLookup(lookupId))
        {
            Lookups.Add(new LookupBindingForm(lookupId));
        }
    }

    public void RemoveLookup(Guid lookupId) => Lookups.RemoveAll(l => l.LookupId == lookupId);

    /// <summary>Everything that blocks saving, with the same limits the server applies.</summary>
    public IReadOnlyList<string> Problems(IReadOnlyList<DictionaryField> dictionary, IReadOnlyList<LookupChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        ArgumentNullException.ThrowIfNull(choices);

        var problems = new List<string>();
        problems.AddRange(new[] { NameProblem(LookupToolLimits.MaxNameLength), DescriptionProblem(LookupToolLimits.MaxDescriptionLength, "when to read the lists") }.OfType<string>());

        if (Lookups.Count == 0)
        {
            problems.Add("Choose at least one lookup.");
        }
        else if (Lookups.Count > LookupToolLimits.MaxLookups)
        {
            problems.Add($"A lookup tool can bind up to {LookupToolLimits.MaxLookups} lookups.");
        }

        if (KnowledgeBaseIds.Count > LookupToolLimits.MaxKnowledgeBases)
        {
            problems.Add($"A lookup tool can query up to {LookupToolLimits.MaxKnowledgeBases} knowledge bases.");
        }

        if (ExposedCountProblem() is { } exposed)
        {
            problems.Add(exposed);
        }

        problems.AddRange(LookupNameProblems(choices));
        problems.AddRange(AliasProblems(dictionary));

        if (ScopeProblem() is { } scope)
        {
            problems.Add(scope);
        }

        var (_, filterError) = ReadFixedFilters(dictionary);
        if (filterError is not null)
        {
            problems.Add(filterError);
        }

        return problems;
    }

    /// <summary>The request to send. Call it when <see cref="Problems"/> is empty.</summary>
    public LookupToolRequest ToRequest(IReadOnlyList<DictionaryField> dictionary)
    {
        var (filters, _) = ReadFixedFilters(dictionary);
        return new LookupToolRequest(
            Name.Trim(),
            Description.Trim(),
            [.. Lookups.Select(l => new LookupBindingRequest(l.LookupId, l.Alias.Trim().Length == 0 ? null : l.Alias.Trim()))],
            [.. KnowledgeBaseIds],
            [.. ExposedFieldIds.Select(id => new ExposedFieldRequest(id, AliasToSend(id, dictionary)))],
            filters is { Count: > 0 } ? filters : null,
            Scope.Build().Scope!,
            Policy);
    }

    // The model tells the lists apart by name: the alias, or the lookup's own. Two lists with one name need an alias on one of them.
    private List<string> LookupNameProblems(IReadOnlyList<LookupChoice> choices)
    {
        var problems = new List<string>();
        foreach (var binding in Lookups.Where(l => l.Alias.Trim().Length > 0 && !LookupAliasPattern().IsMatch(l.Alias.Trim())))
        {
            problems.Add($"The alias \"{binding.Alias.Trim()}\" of a lookup is not allowed: 1 to 40 lower-case letters, digits and \"_\", starting with a letter.");
        }

        var named = Lookups
            .Select(l => (Binding: l, Choice: choices.FirstOrDefault(c => c.Id == l.LookupId)))
            .Where(l => l.Choice is not null)
            .Select(l => l.Binding.Alias.Trim().Length > 0 ? l.Binding.Alias.Trim() : l.Choice!.Name);
        foreach (var repeated in named.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            problems.Add($"Two lookups would be called \"{repeated.Key}\" for the model. Give one of them an alias.");
        }

        return problems;
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,39}\z", RegexOptions.CultureInvariant)]
    private static partial Regex LookupAliasPattern();
}
