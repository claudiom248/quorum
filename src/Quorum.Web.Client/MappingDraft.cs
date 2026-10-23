using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Quorum.Web.Client.Api;

namespace Quorum.Web.Client;

/// <summary>One metadata field of a draft mapping: a payload path bound to a dictionary field by cluster and name.</summary>
public sealed class MappingRow
{
    private string _name = string.Empty;
    private string _cluster = string.Empty;

    /// <summary>The payload path, in dot notation for nested objects (<c>meta.year</c>).</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>The dictionary field it is bound to, with <see cref="Cluster"/>. An existing field is used as it is; one that does not exist yet is created when the admin confirms.</summary>
    public string Name
    {
        get => _name;
        set
        {
            _name = value;
            Chosen = true;
        }
    }

    /// <summary>The cluster of the field; empty means no cluster.</summary>
    public string Cluster
    {
        get => _cluster;
        set
        {
            _cluster = value;
            Chosen = true;
        }
    }

    /// <summary>
    /// The type the payload values suggest, or the one chosen for a new field; <see langword="null"/> when nobody has an opinion (a
    /// row added by hand, or one read from a saved mapping). It is the type of a new field and is compared with an existing one.
    /// </summary>
    public string? Type { get; set; }

    /// <summary>Keep the distinct values of the field as a lookup (spec 007). Only a text or a list of texts can be listed.</summary>
    public bool Listable { get; set; }

    /// <summary>The entity lookup lists the field beside the name of each entity.</summary>
    public bool EntityAttribute { get; set; }

    /// <summary>The admin accepts that the existing dictionary field has another type than the payload suggested (its values must still fit).</summary>
    public bool AcceptExistingType { get; set; }

    /// <summary>Several visible fields have the row's name, in different clusters, and none was preselected.</summary>
    public IReadOnlyList<FieldCandidate> Candidates { get; private set; } = [];

    /// <summary><see langword="false"/> while the admin has to pick among <see cref="Candidates"/>; editing the name or the cluster counts as a choice.</summary>
    public bool Chosen { get; private set; } = true;

    internal void Seed(string name, string cluster, IReadOnlyList<FieldCandidate> candidates)
    {
        _name = name;
        _cluster = cluster;
        Candidates = candidates;
        Chosen = candidates.Count == 0;
    }

    public void Pick(FieldCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        _cluster = candidate.Cluster ?? string.Empty;
        Chosen = true;
    }
}

/// <param name="Existing">The dictionary field of that cluster and name, when there is one.</param>
/// <param name="Problems">What blocks saving this row; empty when it is fine.</param>
public sealed record MappingRowReview(MappingRow Row, DictionaryField? Existing, IReadOnlyList<string> Problems)
{
    public bool IsNew => Existing is null;

    /// <summary>The type the row will have: the dictionary's, or the one chosen for the new field.</summary>
    public string EffectiveType => Existing?.Type ?? Row.Type ?? DictionaryFieldTypes.Text;

    public bool HasTypeConflict => Existing is not null && Row.Type is { } type && type != Existing.Type;
}

/// <summary>
/// The mapping the admin reviews while registering a knowledge base (spec 003 D-9): the roles, and the metadata fields bound to the
/// dictionary. It starts from what the server inferred and everything is editable; the checks here only save a round trip, the
/// server decides.
/// </summary>
public sealed partial class MappingDraft
{
    private const int MaxPathLength = 255;

    private static readonly string[] ReservedNames = ["query", "knowledge_base"];
    private static readonly string[] ReservedSuffixes = ["_min", "_max", "_from", "_to"];

    public string Text { get; set; } = string.Empty;

    public string DocumentId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Position { get; set; } = string.Empty;

    public string SourceUrl { get; set; } = string.Empty;

    /// <summary>The payload path of the name of an entity, for a collection whose points are entities (spec 007 D-4).</summary>
    public string EntityName { get; set; } = string.Empty;

    /// <summary>The payload path that tells which point of each document the lookups count (spec 007 D-2).</summary>
    public string CountedPointPath { get; set; } = string.Empty;

    /// <summary>The value at <see cref="CountedPointPath"/>: <c>true</c>, a number or a text. A text that looks like one of them is written in double quotes.</summary>
    public string CountedPointValue { get; set; } = string.Empty;

    public List<MappingRow> Rows { get; } = [];

    /// <summary>The admin confirmed the creation of the dictionary fields the mapping needs and that do not exist yet.</summary>
    public bool ConfirmCreate { get; set; }

    public static MappingDraft From(DraftMapping draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var result = new MappingDraft
        {
            Text = draft.Roles.Text ?? string.Empty,
            DocumentId = draft.Roles.DocumentId ?? string.Empty,
            Title = draft.Roles.Title ?? string.Empty,
            Position = draft.Roles.Position ?? string.Empty,
            SourceUrl = draft.Roles.SourceUrl ?? string.Empty,
        };
        result.UseCountedPoint(draft.CountedPoint);
        foreach (var field in draft.Fields)
        {
            var row = new MappingRow { Path = field.Path, Type = field.Type };
            row.Seed(field.Name, field.Cluster ?? string.Empty, field.Candidates ?? []);
            result.Rows.Add(row);
        }

        return result;
    }

    /// <summary>The mapping of a saved knowledge base. The rows have no type opinion: their fields exist.</summary>
    public static MappingDraft From(MappingInfo mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        var result = new MappingDraft
        {
            Text = mapping.Roles.Text ?? string.Empty,
            DocumentId = mapping.Roles.DocumentId ?? string.Empty,
            Title = mapping.Roles.Title ?? string.Empty,
            Position = mapping.Roles.Position ?? string.Empty,
            SourceUrl = mapping.Roles.SourceUrl ?? string.Empty,
            EntityName = mapping.EntityName ?? string.Empty,
        };
        result.UseCountedPoint(mapping.CountedPoint);
        result.Rows.AddRange(mapping.Fields.Select(f => new MappingRow
        {
            Path = f.Path,
            Name = f.Name ?? string.Empty,
            Cluster = f.Cluster ?? string.Empty,
            Listable = f.Listable,
            EntityAttribute = f.EntityAttribute,
        }));
        return result;
    }

    private void UseCountedPoint(CountedPointInfo? point)
    {
        CountedPointPath = point?.Path ?? string.Empty;
        CountedPointValue = point is null ? string.Empty : CountedPointText(point.Value);
    }

    public MappingRow AddRow()
    {
        var row = new MappingRow();
        Rows.Add(row);
        return row;
    }

    public void RemoveRow(MappingRow row) => Rows.Remove(row);

    /// <summary>Every row with the dictionary field it binds to and what blocks it.</summary>
    public IReadOnlyList<MappingRowReview> Review(IReadOnlyList<DictionaryField> dictionary)
    {
        ArgumentNullException.ThrowIfNull(dictionary);

        var names = Rows.GroupBy(r => (r.Cluster.Trim(), r.Name.Trim())).ToDictionary(g => g.Key, g => g.Count());
        var paths = Rows.GroupBy(r => r.Path.Trim(), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var rolePaths = RolePaths().ToHashSet(StringComparer.Ordinal);

        return [.. Rows.Select(row =>
        {
            var name = row.Name.Trim();
            var cluster = row.Cluster.Trim();
            var path = row.Path.Trim();
            var existing = dictionary.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal) && string.Equals(f.Cluster ?? string.Empty, cluster, StringComparison.Ordinal));
            var problems = new List<string>();

            if (path.Length == 0)
            {
                problems.Add("Give the payload path this field reads.");
            }
            else if (path.Length > MaxPathLength)
            {
                problems.Add($"A payload path can have up to {MaxPathLength} characters.");
            }
            else if (paths[path] > 1 || rolePaths.Contains(path))
            {
                problems.Add($"\"{path}\" is mapped twice: each payload field can have only one meaning.");
            }

            if (!row.Chosen)
            {
                var clusters = string.Join(", ", row.Candidates.Select(c => c.Cluster ?? "no cluster").Order(StringComparer.Ordinal));
                problems.Add($"Several fields are called \"{row.Name}\" (clusters: {clusters}). Choose one, or give this field another cluster or name.");
            }
            else if (name.Length == 0)
            {
                problems.Add("Give the dictionary field a name.");
            }
            else if (existing is null)
            {
                problems.AddRange(NameProblems(name));
                if (cluster.Length > 0 && !ValidName().IsMatch(cluster))
                {
                    problems.Add($"\"{cluster}\" is not a valid cluster: 1 to 40 lower-case letters, digits and \"_\", starting with a letter.");
                }
            }

            if (row.Chosen && name.Length > 0 && names[(cluster, name)] > 1)
            {
                problems.Add($"Another payload field also uses \"{Label(cluster, name)}\": give one of them a different name or cluster.");
            }

            var review = new MappingRowReview(row, existing, problems);
            if (row.Chosen && review.HasTypeConflict && !row.AcceptExistingType)
            {
                problems.Add($"The dictionary field \"{Label(cluster, name)}\" is {TypeLabel(existing!.Type)}, but the payload looks like {TypeLabel(row.Type!)}. Choose a field of another cluster, give it another name, or accept the dictionary's type if the values fit.");
            }

            return review;
        })];
    }

    /// <summary>The dictionary fields this mapping needs and that do not exist yet, once each.</summary>
    public IReadOnlyList<ProposedField> NewFields(IReadOnlyList<DictionaryField> dictionary) =>
        [.. Review(dictionary)
            .Where(r => r.IsNew && r.Row.Chosen && r.Row.Name.Trim().Length > 0)
            .GroupBy(r => (Cluster: r.Row.Cluster.Trim(), Name: r.Row.Name.Trim()))
            .Select(g => new ProposedField(g.Key.Name, g.First().EffectiveType, g.Key.Cluster.Length > 0 ? g.Key.Cluster : null))];

    /// <summary>Everything that blocks saving the mapping, in the order the admin meets it.</summary>
    public IReadOnlyList<string> Problems(IReadOnlyList<DictionaryField> dictionary)
    {
        var problems = new List<string>();
        if (Text.Trim().Length == 0)
        {
            problems.Add("The text role is required: the payload field that holds the text of the chunk.");
        }

        var rolePaths = RolePaths().ToList();
        if (rolePaths.Any(p => p.Length > MaxPathLength))
        {
            problems.Add($"A payload path can have up to {MaxPathLength} characters.");
        }

        foreach (var repeated in rolePaths.GroupBy(p => p, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            problems.Add($"\"{repeated.Key}\" is used for two roles: each payload field can have only one meaning.");
        }

        var reviews = Review(dictionary);
        problems.AddRange(reviews.SelectMany(r => r.Problems.Select(p => $"{(r.Row.Path.Trim().Length > 0 ? r.Row.Path.Trim() : "A field")}: {p}")));

        if (NewFields(dictionary).Count > 0 && !ConfirmCreate)
        {
            problems.Add("Confirm the creation of the new dictionary fields, or bind those payload fields to existing ones.");
        }

        return problems;
    }

    /// <summary>What blocks the lookups step: the same limits the server applies to the counted point and the entity attributes.</summary>
    public IReadOnlyList<string> LookupProblems(IReadOnlyList<DictionaryField> dictionary)
    {
        var problems = new List<string>();
        foreach (var review in Review(dictionary).Where(r => r.Row.Listable && r.EffectiveType is not (DictionaryFieldTypes.Text or DictionaryFieldTypes.TextList)))
        {
            problems.Add($"{review.Row.Path.Trim()}: only text and list of texts fields can be listed, because a lookup shows the values as they are stored; this one is {TypeLabel(review.EffectiveType)}.");
        }

        var path = CountedPointPath.Trim();
        var value = CountedPointValue.Trim();
        if (path.Length > MaxPathLength || EntityName.Trim().Length > MaxPathLength)
        {
            problems.Add($"A payload path can have up to {MaxPathLength} characters.");
        }

        if ((path.Length == 0) != (value.Length == 0))
        {
            problems.Add("The counted point needs both the payload path and the value that marks the point to count, or neither.");
        }
        else if (path.Length > 0 && CountedPointJson() is null)
        {
            problems.Add("The value of the counted point must be true, a number or a text of up to 200 characters.");
        }

        var attributes = Rows.Count(r => r.EntityAttribute);
        if (attributes > LookupLimits.MaxEntityAttributes)
        {
            problems.Add($"An entity lookup lists up to {LookupLimits.MaxEntityAttributes} attributes: unmark some of the fields.");
        }
        else if (attributes > 0 && EntityName.Trim().Length == 0)
        {
            problems.Add("Entity attributes need the entity name: say which payload field holds the name of each entity.");
        }

        return problems;
    }

    /// <summary>What the lookups of the mapping will be, for the last step; <see langword="null"/> when there are none.</summary>
    public string? LookupSummary(int refreshHours)
    {
        var parts = new List<string>();
        var listed = Rows.Count(r => r.Listable);
        if (listed > 0)
        {
            parts.Add($"{listed} listed field(s)");
        }

        if (EntityName.Trim().Length > 0)
        {
            parts.Add($"entities named by {EntityName.Trim()}");
        }

        if (parts.Count == 0)
        {
            return null;
        }

        var counted = CountedPointPath.Trim().Length > 0 ? $"counting the points where {CountedPointPath.Trim()} is {CountedPointValue.Trim()}" : "counting chunks";
        return $"Lookups: {string.Join(", ", parts)}, {counted}, refreshed every {refreshHours} hour(s)";
    }

    /// <summary>The counted point as the server reads it; <see langword="null"/> when there is none or its value is not valid.</summary>
    public CountedPointInfo? CountedPoint()
    {
        var path = CountedPointPath.Trim();
        return path.Length > 0 && CountedPointJson() is { } value ? new CountedPointInfo(path, value) : null;
    }

    private JsonElement? CountedPointJson()
    {
        const int MaxTextLength = 200;
        var text = CountedPointValue.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            var quoted = text[1..^1];
            return quoted.Length is > 0 and <= MaxTextLength ? JsonSerializer.SerializeToElement(quoted) : null;
        }

        if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
        {
            return JsonSerializer.SerializeToElement(true);
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
        {
            return JsonSerializer.SerializeToElement(number);
        }

        return text.Length <= MaxTextLength ? JsonSerializer.SerializeToElement(text) : null;
    }

    private static string CountedPointText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => "true",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.String when LooksLikeAnotherType(value.GetString()!) => $"\"{value.GetString()}\"",
        JsonValueKind.String => value.GetString()!,
        _ => value.GetRawText(),
    };

    private static bool LooksLikeAnotherType(string text) =>
        text.Length == 0
        || text[0] == '"'
        || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)
        || (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number));

    /// <summary>The mapping to send and the dictionary fields to create. Call it when <see cref="Problems"/> is empty.</summary>
    public (MappingRequest Mapping, IReadOnlyList<CreateFieldRequest> CreateFields) ToRequest(IReadOnlyList<DictionaryField> dictionary)
    {
        var roles = new MappingRoles(Clean(Text), Clean(DocumentId), Clean(Title), Clean(Position), Clean(SourceUrl));
        var fields = Review(dictionary)
            .Select(r => new MappedFieldRequest(
                r.Row.Path.Trim(),
                r.Existing?.Id,
                r.Existing is null ? r.Row.Name.Trim() : null,
                r.Existing is null ? Clean(r.Row.Cluster) : null,
                r.Row.Listable,
                r.Row.EntityAttribute))
            .ToList();
        var create = ConfirmCreate
            ? NewFields(dictionary).Select(f => new CreateFieldRequest(f.Name, f.Type, Cluster: f.Cluster)).ToList()
            : [];
        return (new MappingRequest(roles, fields, CountedPoint(), Clean(EntityName)), create);
    }

    /// <summary>A dictionary type in words.</summary>
    public static string TypeLabel(string type) => type switch
    {
        DictionaryFieldTypes.TextList => "a list of texts",
        DictionaryFieldTypes.Text => "text",
        DictionaryFieldTypes.Number => "a number",
        DictionaryFieldTypes.Boolean => "a boolean",
        DictionaryFieldTypes.Date => "a date",
        _ => type,
    };

    private static string Label(string cluster, string name) => cluster.Length == 0 ? name : $"{cluster}/{name}";

    private static IEnumerable<string> NameProblems(string name)
    {
        if (!ValidName().IsMatch(name))
        {
            yield return $"\"{name}\" is not a valid dictionary name: 1 to 40 lower-case letters, digits and \"_\", starting with a letter.";
        }
        else if (ReservedNames.Contains(name, StringComparer.Ordinal) || ReservedSuffixes.Any(s => name.EndsWith(s, StringComparison.Ordinal)))
        {
            yield return $"\"{name}\" is reserved: tools use \"query\", \"knowledge_base\" and the endings \"_min\", \"_max\", \"_from\", \"_to\" for their own parameters.";
        }
    }

    private IEnumerable<string> RolePaths() =>
        new[] { Text, DocumentId, Title, Position, SourceUrl, EntityName }.Select(p => p.Trim()).Where(p => p.Length > 0);

    private static string? Clean(string text) => text.Trim() is { Length: > 0 } trimmed ? trimmed : null;

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,39}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ValidName();
}
