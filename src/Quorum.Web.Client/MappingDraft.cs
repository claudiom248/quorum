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
        };
        result.Rows.AddRange(mapping.Fields.Select(f => new MappingRow { Path = f.Path, Name = f.Name ?? string.Empty, Cluster = f.Cluster ?? string.Empty }));
        return result;
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

    /// <summary>The mapping to send and the dictionary fields to create. Call it when <see cref="Problems"/> is empty.</summary>
    public (MappingRequest Mapping, IReadOnlyList<CreateFieldRequest> CreateFields) ToRequest(IReadOnlyList<DictionaryField> dictionary)
    {
        var roles = new MappingRoles(Clean(Text), Clean(DocumentId), Clean(Title), Clean(Position), Clean(SourceUrl));
        var fields = Review(dictionary)
            .Select(r => new MappedFieldRequest(
                r.Row.Path.Trim(),
                r.Existing?.Id,
                r.Existing is null ? r.Row.Name.Trim() : null,
                r.Existing is null ? Clean(r.Row.Cluster) : null))
            .ToList();
        var create = ConfirmCreate
            ? NewFields(dictionary).Select(f => new CreateFieldRequest(f.Name, f.Type, Cluster: f.Cluster)).ToList()
            : [];
        return (new MappingRequest(roles, fields), create);
    }

    /// <summary>A dictionary type in words.</summary>
    public static string TypeLabel(string type) => type switch
    {
        DictionaryFieldTypes.TextList => "a list of texts",
        DictionaryFieldTypes.Text => "text",
        DictionaryFieldTypes.Number => "a number",
        DictionaryFieldTypes.Boolean => "yes/no",
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
        new[] { Text, DocumentId, Title, Position, SourceUrl }.Select(p => p.Trim()).Where(p => p.Length > 0);

    private static string? Clean(string text) => text.Trim() is { Length: > 0 } trimmed ? trimmed : null;

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,39}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ValidName();
}
