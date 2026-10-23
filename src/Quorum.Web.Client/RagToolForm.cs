using System.Globalization;
using System.Text.Json;
using Quorum.Web.Client.Api;

namespace Quorum.Web.Client;

/// <summary>The dictionary fields a rag tool can expose or filter on, given the knowledge bases it searches.</summary>
/// <param name="Complete">
/// <see langword="false"/> when a selected knowledge base belongs to another tenant: its mapping is not visible here, so every
/// dictionary field is offered and the server refuses the ones no knowledge base maps.
/// </param>
public sealed record FieldChoices(IReadOnlyList<DictionaryField> Fields, bool Complete);

/// <summary>What the rag tool editor of the Tools page shows and reads, kept apart from the markup so it can be tested alone.</summary>
public static class RagToolView
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The fields mapped by the selected knowledge bases (spec 003 D-11: "the fields used by the KBs").</summary>
    public static FieldChoices Candidates(
        IEnumerable<Guid> selectedKnowledgeBaseIds,
        IReadOnlyDictionary<Guid, KnowledgeBaseDetail> details,
        IReadOnlyList<DictionaryField> dictionary)
    {
        ArgumentNullException.ThrowIfNull(selectedKnowledgeBaseIds);
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(dictionary);

        var mapped = new HashSet<Guid>();
        var complete = true;
        foreach (var id in selectedKnowledgeBaseIds)
        {
            if (details.TryGetValue(id, out var detail) && detail.Mapping is { } mapping)
            {
                mapped.UnionWith(mapping.Fields.Select(f => f.DictionaryFieldId));
            }
            else
            {
                complete = false;
            }
        }

        return complete
            ? new FieldChoices([.. dictionary.Where(f => mapped.Contains(f.Id)).OrderBy(f => f.Name, StringComparer.Ordinal)], Complete: true)
            : new FieldChoices([.. dictionary.OrderBy(f => f.Name, StringComparer.Ordinal)], Complete: false);
    }

    /// <summary>
    /// Whether a selected knowledge base maps the document id, so a tool can search within given documents (spec 007 D-17).
    /// <see langword="null"/> when a selected knowledge base belongs to another tenant and none of the visible ones does: its mapping
    /// is not shown, so this editor cannot tell.
    /// </summary>
    public static bool? SupportsDocumentIds(IEnumerable<Guid> selectedKnowledgeBaseIds, IReadOnlyDictionary<Guid, KnowledgeBaseDetail> details)
    {
        ArgumentNullException.ThrowIfNull(selectedKnowledgeBaseIds);
        ArgumentNullException.ThrowIfNull(details);

        var unknown = false;
        foreach (var id in selectedKnowledgeBaseIds)
        {
            if (!details.TryGetValue(id, out var detail) || detail.Mapping is not { } mapping)
            {
                unknown = true;
            }
            else if (!string.IsNullOrWhiteSpace(mapping.Roles.DocumentId))
            {
                return true;
            }
        }

        return unknown ? null : false;
    }

    /// <summary>The parameters the model will see for these exposed fields, as the server generates them (D-11): a local preview, the saved definition is the real one.</summary>
    public static IReadOnlyList<string> ParameterNames(IEnumerable<DictionaryField> exposed, int knowledgeBaseCount)
    {
        ArgumentNullException.ThrowIfNull(exposed);

        List<string> names = ["query"];
        if (knowledgeBaseCount > 1)
        {
            names.Add("knowledge_base");
        }

        names.AddRange(exposed.OrderBy(f => f.Name, StringComparer.Ordinal).SelectMany(f => FilterInputs.ParametersOf(f.Name, f.Type).Select(p => p.Name)));
        return names;
    }

    /// <summary>The same with the aliases the exposed fields were given, by field id.</summary>
    public static IReadOnlyList<string> ParameterNames(IEnumerable<DictionaryField> exposed, IReadOnlyDictionary<Guid, string> aliases, int knowledgeBaseCount)
    {
        ArgumentNullException.ThrowIfNull(exposed);
        ArgumentNullException.ThrowIfNull(aliases);

        List<string> names = ["query"];
        if (knowledgeBaseCount > 1)
        {
            names.Add("knowledge_base");
        }

        names.AddRange(exposed
            .Select(f => (Field: f, Name: aliases.GetValueOrDefault(f.Id) ?? f.Name))
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .SelectMany(e => FilterInputs.ParametersOf(e.Name, e.Field.Type).Select(p => p.Name)));
        return names;
    }

    /// <summary>The size of a definition in words.</summary>
    public static string SizeText(int estimatedTokens) =>
        estimatedTokens >= 1_000
            ? $"about {(estimatedTokens / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture)}k tokens of the context"
            : $"about {estimatedTokens} tokens of the context";

    /// <summary>The input schema as indented JSON, for the preview.</summary>
    public static string FormatSchema(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Undefined ? "{}" : JsonSerializer.Serialize(schema, Indented);

    public static string PolicyLabel(string policy) => policy == ToolPolicies.Auto ? "runs without asking" : "asks before running";
}

/// <summary>The rag tool being created or edited: what the admin typed, and its conversion into the request.</summary>
public sealed class RagToolForm : FilteredToolForm
{
    public int TopK { get; set; } = RagToolLimits.DefaultTopK;

    public string ScoreThreshold { get; set; } = string.Empty;

    public int MaxResultCharacters { get; set; } = RagToolLimits.DefaultMaxResultCharacters;

    /// <summary>The model may search within given documents (<c>document_ids</c>); needs a knowledge base that maps the document id (spec 007 D-17).</summary>
    public bool AllowDocumentIds { get; set; }

    /// <summary>The admin decided about <see cref="AllowDocumentIds"/>: what the knowledge bases map no longer changes it.</summary>
    public bool DocumentIdsChosen { get; private set; }

    protected override string Prefix => "rag";

    protected override int MaxExposedFields => RagToolLimits.MaxExposedFields;

    protected override IReadOnlyList<string> ReservedNames { get; } = ["query", "knowledge_base", "document_ids"];

    /// <param name="mapped">The fields the tool's knowledge bases map, which its fixed filters may name; every dictionary field when not given.</param>
    public static RagToolForm From(RagToolDetail detail, IReadOnlyList<DictionaryField> dictionary, IReadOnlyList<DictionaryField>? mapped = null)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(dictionary);

        var form = new RagToolForm
        {
            Id = detail.Id,
            OriginalName = detail.Name,
            Name = detail.Name,
            Description = detail.Description,
            Policy = detail.Policy,
            TopK = detail.TopK,
            ScoreThreshold = detail.ScoreThreshold?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            MaxResultCharacters = detail.MaxResultCharacters,
            Scope = ScopeForm.From(detail.Scope),
            AllowDocumentIds = detail.AllowDocumentIds,
            DocumentIdsChosen = true,
        };
        form.KnowledgeBaseIds.AddRange(detail.KnowledgeBaseIds);
        form.LoadFilters(detail.ExposedFields, detail.FixedFilters, dictionary, mapped);
        return form;
    }

    /// <summary>The admin switches <c>document_ids</c> on or off.</summary>
    public void ChooseDocumentIds(bool allow)
    {
        AllowDocumentIds = allow;
        DocumentIdsChosen = true;
    }

    /// <summary>
    /// Follows the knowledge bases: a tool whose knowledge bases cannot filter by document has the switch off, and a new tool gets it
    /// on as soon as one can, until the admin decides (the server's default for a tool that leaves it out).
    /// </summary>
    public void ApplyDocumentIds(bool? supported)
    {
        if (supported == false)
        {
            AllowDocumentIds = false;
        }
        else if (supported == true && IsNew && !DocumentIdsChosen)
        {
            AllowDocumentIds = true;
        }
    }

    /// <summary>Everything that blocks saving, with the same limits the server applies.</summary>
    public IReadOnlyList<string> Problems(IReadOnlyList<DictionaryField> dictionary)
    {
        var problems = new List<string>();
        problems.AddRange(new[] { NameProblem(RagToolLimits.MaxNameLength), DescriptionProblem(RagToolLimits.MaxDescriptionLength, "when to search") }.OfType<string>());

        if (KnowledgeBaseIds.Count == 0)
        {
            problems.Add("Choose at least one knowledge base.");
        }
        else if (KnowledgeBaseIds.Count > RagToolLimits.MaxKnowledgeBases)
        {
            problems.Add($"A rag tool can search up to {RagToolLimits.MaxKnowledgeBases} knowledge bases.");
        }

        if (ExposedCountProblem() is { } exposed)
        {
            problems.Add(exposed);
        }

        if (TopK is < 1 or > RagToolLimits.MaxTopK)
        {
            problems.Add($"The number of results is between 1 and {RagToolLimits.MaxTopK}.");
        }

        if (MaxResultCharacters is < RagToolLimits.MinMaxResultCharacters or > RagToolLimits.MaxMaxResultCharacters)
        {
            problems.Add($"The result size is between {RagToolLimits.MinMaxResultCharacters} and {RagToolLimits.MaxMaxResultCharacters} characters.");
        }

        if (ScoreThreshold.Trim().Length > 0 && (ParseThreshold() is not { } threshold || !double.IsFinite(threshold)))
        {
            problems.Add("The score threshold is a number, or empty for none.");
        }

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
    public RagToolRequest ToRequest(IReadOnlyList<DictionaryField> dictionary)
    {
        var (filters, _) = ReadFixedFilters(dictionary);
        return new RagToolRequest(
            Name.Trim(),
            Description.Trim(),
            [.. KnowledgeBaseIds],
            [.. ExposedFieldIds.Select(id => new ExposedFieldRequest(id, AliasToSend(id, dictionary)))],
            filters is { Count: > 0 } ? filters : null,
            TopK,
            ParseThreshold(),
            MaxResultCharacters,
            Scope.Build().Scope!,
            Policy,
            AllowDocumentIds);
    }

    private double? ParseThreshold() =>
        double.TryParse(ScoreThreshold, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
}
