using Quorum.Web.Client.Api;

namespace Quorum.Web.Client;

public enum WarningLevel
{
    /// <summary>The knowledge base will not work until this is fixed.</summary>
    Error,
    Warning,
    Info,
}

/// <summary>What the knowledge base admin pages show, kept apart from the markup so it can be tested alone.</summary>
public static class KnowledgeAdminView
{
    // Warnings that mean the store, the collection or the model cannot be used as typed. Everything else is advice.
    private static readonly HashSet<string> BlockingCodes =
    [
        "knowledge_base.qdrant_unreachable",
        "knowledge_base.qdrant_unauthorized",
        "knowledge_base.qdrant_version_unsupported",
        "knowledge_base.qdrant_unexpected_response",
        "knowledge_base.collection_not_found",
        "knowledge_base.dimension_mismatch",
        "knowledge_base.embedding_failed",
        "knowledge_base.vector_name_required",
        "knowledge_base.vector_not_found",
        "knowledge_base.url_blocked",
    ];

    public static WarningLevel LevelOf(KnowledgeBaseWarning warning)
    {
        ArgumentNullException.ThrowIfNull(warning);

        return BlockingCodes.Contains(warning.Code)
            ? WarningLevel.Error
            : warning.Code is "knowledge_base.field_unindexed" or "knowledge_base.empty_collection" or "knowledge_base.unsupported_schema_keyword"
                ? WarningLevel.Info
                : WarningLevel.Warning;
    }

    public static string StatusLabel(string status) => status switch
    {
        KnowledgeBaseStatuses.Available => "Available",
        KnowledgeBaseStatuses.Unavailable => "Unavailable",
        _ => status,
    };

    /// <summary>The words for the outcome of a registration or a verification.</summary>
    public static string Outcome(string name, string status)
    {
        return status == KnowledgeBaseStatuses.Available
            ? $"{name} was saved and is available."
            : $"{name} was saved but is unavailable: searches on it fail until the problem below is fixed. Verify it again when it is.";
    }

    /// <summary>The dictionary type in the words of the admin pages.</summary>
    public static string TypeLabel(string type) => type switch
    {
        DictionaryFieldTypes.TextList => "List of texts",
        _ => type,
    };

    /// <summary>Who sees a dictionary field.</summary>
    public static string DictionaryScopeLabel(string scope) => scope switch
    {
        ScopeKinds.Global => "Every tenant",
        ScopeKinds.Tenant => "This tenant",
        _ => scope,
    };

    /// <summary>The vectors of a collection in one line: <c>768 · Cosine</c>, <c>"dense": 768 · Cosine</c>.</summary>
    public static string DescribeVector(VectorInfo vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        return $"{(vector.Name is { Length: > 0 } name ? $"\"{name}\": " : string.Empty)}{vector.Size} · {vector.Distance}";
    }

    /// <summary>What the inspection found, as short statements for the admin.</summary>
    public static IReadOnlyList<string> Findings(InspectResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var findings = new List<string>
        {
            result.Reachable ? "The server answered." : "The server could not be reached.",
        };

        if (result.Reachable)
        {
            findings.Add(result.CollectionExists ? "The collection exists." : "The collection was not found.");
        }

        if (result.Vectors.Count > 0)
        {
            findings.Add($"Vectors (size · distance): {string.Join("; ", result.Vectors.Select(DescribeVector))}.");
        }

        if (result.EmbeddingSize is { } size)
        {
            findings.Add(result.DimensionMatch switch
            {
                true => $"The embedding model gives vectors of {size} dimensions: they match.",
                false => $"The embedding model gives vectors of {size} dimensions: they do not match the collection.",
                null => $"The embedding model gives vectors of {size} dimensions.",
            });
        }

        return findings;
    }

    /// <summary>Whether the admin can go on to the mapping: the collection was read, so there is something to review.</summary>
    public static bool CanReview(InspectResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Reachable && result.CollectionExists;
    }

    /// <summary>The first lines of a text, for a result list.</summary>
    public static string Excerpt(string text, int maxCharacters = 400)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length <= maxCharacters ? text : text[..maxCharacters].TrimEnd() + "…";
    }

    /// <summary>A reference of a search result as the model would cite it: <c>title, position</c>.</summary>
    public static string DescribeSource(TestSearchSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var parts = new[] { source.Title, source.Position is { Length: > 0 } position ? $"({position})" : null, source.DocumentId is { Length: > 0 } id && source.Title is null ? id : null }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        var text = string.Join(" ", parts);
        return text.Length > 0 ? text : "Untitled";
    }

    /// <summary>A source URL as a link target only for http and https, as the chat does (spec 003 AC-40).</summary>
    public static string? SafeUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.ToString() : null;

    /// <summary>
    /// How a field is written: its name, with its cluster in front only where the name alone is ambiguous, that is when fields of
    /// other clusters have the same name (spec 003 A1-6). No cluster shows no label.
    /// </summary>
    public static string FieldLabel(DictionaryField field, IEnumerable<DictionaryField> all)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(all);

        return field.Cluster is not null && all.Count(f => f.Name == field.Name) > 1 ? $"{field.Cluster}/{field.Name}" : field.Name;
    }

    /// <summary>The fields grouped by cluster: the ones with no cluster first, then each cluster by name; a heading is only for a cluster.</summary>
    public static IReadOnlyList<(string? Cluster, IReadOnlyList<DictionaryField> Fields)> GroupByCluster(IEnumerable<DictionaryField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        return
        [
            .. fields
                .GroupBy(f => f.Cluster)
                .OrderBy(g => g.Key is not null)
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => (g.Key, (IReadOnlyList<DictionaryField>)[.. g.OrderBy(f => f.Name, StringComparer.Ordinal)])),
        ];
    }

    /// <summary>
    /// The filter names of a test search on one knowledge base, as the server reads them: the field's name, or, when two of the
    /// fields it maps share a name, the cluster and the name joined by an underscore (a field with no cluster keeps its name).
    /// </summary>
    public static IReadOnlyList<(string Parameter, DictionaryField Field)> TestSearchNames(IReadOnlyList<DictionaryField> mapped)
    {
        ArgumentNullException.ThrowIfNull(mapped);

        var names = new List<(string, DictionaryField)>();
        foreach (var group in mapped.GroupBy(f => f.Name, StringComparer.Ordinal))
        {
            foreach (var field in group)
            {
                names.Add((group.Count() > 1 && field.Cluster is not null ? $"{field.Cluster}_{field.Name}" : field.Name, field));
            }
        }

        return [.. names.OrderBy(n => n.Item1, StringComparer.Ordinal)];
    }
}

/// <summary>What the registration wizard reports once the server saved a knowledge base.</summary>
public sealed record SavedKnowledgeBase(Guid Id, string Name, string Status, IReadOnlyList<KnowledgeBaseWarning> Warnings);
