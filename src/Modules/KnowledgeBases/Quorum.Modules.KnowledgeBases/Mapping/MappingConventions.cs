using Quorum.Modules.KnowledgeBases.Domain;

namespace Quorum.Modules.KnowledgeBases.Mapping;

/// <summary>The payload paths that carry the meaning Quorum needs of a chunk (D-9). Only <see cref="Text"/> is required to save a mapping.</summary>
internal sealed record MappingRoles(string? Text, string? DocumentId, string? Title, string? Position, string? SourceUrl);

/// <summary>A payload property proposed as a metadata field; <see cref="Issues"/> are the codes that block saving until the admin resolves them.</summary>
internal sealed record DraftField(string Path, string Name, DictionaryFieldType Type, IReadOnlyList<string> Issues);

/// <summary>What the admin reviews: the guessed roles and the proposed metadata fields, never saved without confirmation.</summary>
internal sealed record DraftMapping(MappingRoles Roles, IReadOnlyList<DraftField> Fields)
{
    public static DraftMapping Empty { get; } = new(new MappingRoles(null, null, null, null, null), []);
}

/// <summary>Fills a draft mapping from the properties of a payload by naming conventions, wherever the properties came from (sampling, pasted schema).</summary>
internal static class MappingConventions
{
    public const string FieldNameCollision = "knowledge_base.field_name_collision";

    // Spec 003 "Conventions that fill the draft mapping": names are compared ignoring case, '-' and '_'.
    private static readonly string[] TextNames = ["text", "chunktext", "content", "chunk", "body"];
    private static readonly string[] DocumentIdNames = ["documentid", "docid", "sourceid"];
    private static readonly string[] TitleNames = ["title", "filename", "documentname", "name"];
    private static readonly string[] PositionNames = ["page", "pagenumber", "position", "section"];
    private static readonly string[] SourceUrlNames = ["sourceurl", "url", "uri", "link"];

    public static DraftMapping Build(IReadOnlyList<DiscoveredProperty> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);

        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var text = Claim(properties, claimed, TextNames, DictionaryFieldType.Text);
        var documentId = Claim(properties, claimed, DocumentIdNames, DictionaryFieldType.Text, DictionaryFieldType.Number);
        var title = Claim(properties, claimed, TitleNames, DictionaryFieldType.Text);
        var position = Claim(properties, claimed, PositionNames, DictionaryFieldType.Text, DictionaryFieldType.Number);
        var sourceUrl = Claim(properties, claimed, SourceUrlNames, DictionaryFieldType.Text);

        var metadata = properties.Where(p => !claimed.Contains(p.Path)).ToList();
        var collisions = metadata.GroupBy(p => LastSegment(p.Path), StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);

        var fields = metadata.Select(property =>
        {
            var name = LastSegment(property.Path);
            var issues = new List<string>();
            if (!DictionaryFieldRules.IsValidName(name))
            {
                issues.Add("knowledge_base.invalid_field_name");
            }
            else if (DictionaryFieldRules.IsReservedName(name))
            {
                issues.Add("knowledge_base.reserved_field_name");
            }

            if (collisions.Contains(name))
            {
                issues.Add(FieldNameCollision);
            }

            return new DraftField(property.Path, name, property.Type, issues);
        }).ToList();

        return new DraftMapping(new MappingRoles(text, documentId, title, position, sourceUrl), fields);
    }

    /// <summary>
    /// The property that takes a role: among the unclaimed ones with a matching name and an allowed type, the one nearest to the
    /// root, then the one whose name comes first in the convention list, then the first found.
    /// </summary>
    private static string? Claim(IReadOnlyList<DiscoveredProperty> properties, HashSet<string> claimed, string[] names, params DictionaryFieldType[] allowedTypes)
    {
        var winner = properties
            .Select((property, index) => (property, index, rank: Array.IndexOf(names, Normalise(LastSegment(property.Path)))))
            .Where(c => c.rank >= 0 && !claimed.Contains(c.property.Path) && allowedTypes.Contains(c.property.Type))
            .OrderBy(c => c.property.Path.Count(ch => ch == '.'))
            .ThenBy(c => c.rank)
            .ThenBy(c => c.index)
            .Select(c => c.property.Path)
            .FirstOrDefault();

        if (winner is not null)
        {
            claimed.Add(winner);
        }

        return winner;
    }

    private static string LastSegment(string path) => path[(path.LastIndexOf('.') + 1)..];

    private static string Normalise(string name) => name.Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
}
