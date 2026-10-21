using System.Text.RegularExpressions;
using Quorum.Web.Client.Api;

namespace Quorum.Web.Client;

/// <summary>
/// Everything the guided registration of a knowledge base collects, kept apart from the markup so it can be tested alone: the
/// connection, the pasted schema, the mapping under review and the scope (spec 003 D-9, D-19).
/// </summary>
public sealed partial class KnowledgeBaseDraft
{
    public const int MaxNameLength = 40;
    public const int MaxDescriptionLength = 1_000;
    public const int MaxQueryPrefixLength = 200;
    public const int MaxSchemaBytes = 100 * 1024;

    /// <summary>The id of the knowledge base being edited; <see langword="null"/> for a new registration.</summary>
    public Guid? Id { get; init; }

    /// <summary>The address when the draft was loaded: a stored key is kept only while it stays the same.</summary>
    public string OriginalUrl { get; init; } = string.Empty;

    public bool HadApiKey { get; init; }

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string Collection { get; set; } = string.Empty;

    public string VectorName { get; set; } = string.Empty;

    public string EmbeddingModel { get; set; } = string.Empty;

    public string QueryPrefix { get; set; } = string.Empty;

    /// <summary>A pasted JSON Schema of the payload; empty means the collection is sampled.</summary>
    public string Schema { get; set; } = string.Empty;

    public ScopeForm Scope { get; set; } = new();

    public MappingDraft? Mapping { get; set; }

    public bool IsNew => Id is null;

    /// <summary>The key was stored for another address: the server will ask for it again.</summary>
    public bool NeedsApiKeyAgain => HadApiKey && ApiKey.Length == 0 && !string.Equals(Url.Trim(), OriginalUrl, StringComparison.Ordinal);

    public static KnowledgeBaseDraft From(KnowledgeBaseDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        var connection = detail.Connection ?? throw new ArgumentException("A knowledge base of another tenant cannot be edited.", nameof(detail));
        return new KnowledgeBaseDraft
        {
            Id = detail.Id,
            OriginalUrl = connection.Url,
            HadApiKey = connection.HasApiKey,
            Name = detail.Name,
            Description = detail.Description,
            Url = connection.Url,
            Collection = connection.Collection,
            VectorName = connection.VectorName ?? string.Empty,
            EmbeddingModel = detail.EmbeddingModel ?? string.Empty,
            QueryPrefix = detail.QueryPrefix ?? string.Empty,
            Scope = detail.Scope is { } scope ? ScopeForm.From(scope) : new ScopeForm(),
            Mapping = detail.Mapping is { } mapping ? MappingDraft.From(mapping) : null,
        };
    }

    /// <summary>What is wrong with the connection step: the same limits the server applies.</summary>
    public IReadOnlyList<string> ConnectionProblems()
    {
        var problems = new List<string>();
        if (!ValidName().IsMatch(Name.Trim()))
        {
            problems.Add($"The name is 1 to {MaxNameLength} characters: lower-case letters, digits, \"-\" and \"_\", starting with a letter or digit.");
        }

        if (Description.Trim().Length == 0 || Description.Length > MaxDescriptionLength)
        {
            problems.Add($"The description is required, up to {MaxDescriptionLength} characters: the model reads it to choose where to search.");
        }

        if (Url.Trim().Length == 0)
        {
            problems.Add("The address of the Qdrant server is required.");
        }

        if (Collection.Trim().Length == 0)
        {
            problems.Add("The collection is required.");
        }

        if (EmbeddingModel.Trim().Length == 0)
        {
            problems.Add("The embedding model is required: the one that embedded the documents.");
        }

        if (QueryPrefix.Length > MaxQueryPrefixLength)
        {
            problems.Add($"The query prefix can have up to {MaxQueryPrefixLength} characters.");
        }

        return problems;
    }

    /// <summary>What is wrong with the pasted schema, before it is sent.</summary>
    public string? SchemaProblem() =>
        System.Text.Encoding.UTF8.GetByteCount(Schema) > MaxSchemaBytes ? "The schema can have up to 100 KB." : null;

    public InspectRequest ToInspectRequest() => new(
        KnowledgeBaseKinds.Qdrant,
        ToConnection(),
        EmbeddingModel.Trim(),
        Clean(QueryPrefix, trim: false),
        Clean(Schema, trim: true));

    /// <summary>
    /// The draft mapping the server proposed. It replaces the mapping under review; with a vector chosen it also fills the vector
    /// name when the collection has only one named vector.
    /// </summary>
    public void UseInspection(InspectResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        Mapping = MappingDraft.From(result.DraftMapping);
        if (VectorName.Trim().Length == 0 && result.Vectors.Count == 1 && result.Vectors[0].Name is { Length: > 0 } only)
        {
            VectorName = only;
        }
    }

    /// <summary>The collection has several vectors and none is chosen (spec 003 AC-11).</summary>
    public bool NeedsVectorChoice(InspectResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Vectors.Count > 1 && VectorName.Trim().Length == 0;
    }

    /// <summary>What blocks the scope step.</summary>
    public string? ScopeProblem() => Scope.Build().Error;

    /// <summary>Everything that blocks saving, across all steps.</summary>
    public IReadOnlyList<string> Problems(IReadOnlyList<DictionaryField> dictionary)
    {
        var problems = new List<string>(ConnectionProblems());
        if (Mapping is null)
        {
            problems.Add("Review the mapping first: inspect the collection to get a draft.");
        }
        else
        {
            problems.AddRange(Mapping.Problems(dictionary));
        }

        if (ScopeProblem() is { } scope)
        {
            problems.Add(scope);
        }

        return problems;
    }

    /// <summary>The request to send. Call it when <see cref="Problems"/> is empty.</summary>
    public KnowledgeBaseRequest ToRequest(IReadOnlyList<DictionaryField> dictionary)
    {
        var (mapping, createFields) = Mapping!.ToRequest(dictionary);
        return new KnowledgeBaseRequest(
            Name.Trim(),
            Description.Trim(),
            KnowledgeBaseKinds.Qdrant,
            ToConnection(),
            EmbeddingModel.Trim(),
            Clean(QueryPrefix, trim: false),
            mapping,
            Scope.Build().Scope!,
            createFields);
    }

    /// <summary>A short account of what will be saved, for the last step.</summary>
    public IReadOnlyList<string> Summary(IReadOnlyList<DictionaryField> dictionary, IReadOnlyList<AssistantSummary> assistants)
    {
        var lines = new List<string>
        {
            $"{Name.Trim()} · {Url.Trim()} · collection {Collection.Trim()}{(VectorName.Trim().Length > 0 ? $" · vector {VectorName.Trim()}" : string.Empty)}",
            $"Embedding model {EmbeddingModel.Trim()}{(QueryPrefix.Length > 0 ? $" with the prefix \"{QueryPrefix}\"" : string.Empty)}",
        };

        if (Mapping is { } mapping)
        {
            var created = mapping.NewFields(dictionary);
            lines.Add($"Text from {mapping.Text.Trim()}; {mapping.Rows.Count} metadata field(s) mapped");
            if (created.Count > 0)
            {
                lines.Add($"New dictionary field(s): {string.Join(", ", created.Select(f => f.Cluster is null ? f.Name : $"{f.Cluster}/{f.Name}"))}");
            }
        }

        var (scope, _) = Scope.Build();
        if (scope is not null)
        {
            lines.Add($"Who can use it: {ToolAdminView.ScopeSummary(scope, assistants)}");
        }

        return lines;
    }

    private ConnectionRequest ToConnection() => new(
        Url.Trim(),
        Clean(ApiKey, trim: false),
        Collection.Trim(),
        Clean(VectorName, trim: true));

    private static string? Clean(string text, bool trim)
    {
        var value = trim ? text.Trim() : text;
        return value.Trim().Length == 0 ? null : value;
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]{0,39}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ValidName();
}
