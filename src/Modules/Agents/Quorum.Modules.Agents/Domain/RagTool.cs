using System.Text.Json;

namespace Quorum.Modules.Agents.Domain;

/// <summary>
/// The binding of a rag tool to knowledge bases (spec 003 D-4): where it searches, which metadata filters the model may use, which
/// filters it can never change, the limits of a search and who may use the tool. The tool itself (name, description, state, policy,
/// activation) is a <see cref="Tool"/> of origin <see cref="ToolOrigin.Rag"/>; this is its configuration, one row per tool.
/// </summary>
internal sealed class RagTool
{
    public const int MaxKnowledgeBases = 10;

    public const int MaxExposedFields = 10;

    public const int DefaultTopK = 5;

    public const int MaxTopK = 20;

    public const int DefaultMaxResultCharacters = 6_000;

    public const int MinMaxResultCharacters = 200;

    public const int MaxMaxResultCharacters = 50_000;

    /// <summary>What the text of a result adds to the chunks: a notice, and a header and two markers per source.</summary>
    public const int FormattingOverhead = 500;

    public const int FormattingOverheadPerSource = 300;

    /// <summary>The result size that fits the limit on tool results once the formatting is counted: the one asked for, or less.</summary>
    public static int FitResultSize(int resultSize, int topK, int toolResultLimit) =>
        Math.Min(resultSize, Math.Max(1, toolResultLimit - FormattingOverhead - (topK * FormattingOverheadPerSource)));

    private RagTool()
    {
    }

    public Guid ToolId { get; private set; }

    /// <summary>The owner: the only tenant that can change or delete the tool, whatever its scope.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>Knowledge bases by id, not name: a renamed one stays in the tool. One that is deleted or no longer visible is ignored when the tool is used.</summary>
    public List<Guid> KnowledgeBaseIds { get; private set; } = [];

    /// <summary>The dictionary fields the model may filter on.</summary>
    public List<Guid> ExposedFieldIds { get; private set; } = [];

    /// <summary>The alias an exposed field was given, as a JSON object by field id; a field without one is known by its name (spec 003 A1-3).</summary>
    public string ExposedAliases { get; private set; } = "{}";

    /// <summary>The filters the model never sees and cannot override, as a JSON object in the shape of the tool's parameters (<c>department</c>, <c>year_min</c>...).</summary>
    public string FixedFilters { get; private set; } = "{}";

    /// <summary>The dictionary fields the fixed filters name, kept so that a field in use cannot be deleted.</summary>
    public List<Guid> FixedFilterFieldIds { get; private set; } = [];

    public int TopK { get; private set; }

    public double? ScoreThreshold { get; private set; }

    public int MaxResultCharacters { get; private set; }

    public ToolScopeKind Scope { get; private set; }

    /// <summary>With <see cref="ToolScopeKind.Tenant"/>: the tenants besides the owner that may use the tool.</summary>
    public List<Guid> ScopeTenantIds { get; private set; } = [];

    /// <summary>With <see cref="ToolScopeKind.Assistant"/>: the owner's assistants that may use the tool.</summary>
    public List<Guid> ScopeAssistantIds { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static RagTool Create(Guid toolId, Guid tenantId, RagConfiguration configuration, DateTimeOffset now)
    {
        var tool = new RagTool { ToolId = toolId, TenantId = tenantId, CreatedAt = now };
        tool.Apply(configuration, now);
        return tool;
    }

    public void Apply(RagConfiguration configuration, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        KnowledgeBaseIds = [.. configuration.KnowledgeBaseIds];
        ExposedFieldIds = [.. configuration.ExposedFieldIds];
        ExposedAliases = JsonSerializer.Serialize(configuration.ExposedAliases ?? new Dictionary<Guid, string>());
        FixedFilters = configuration.FixedFilters;
        FixedFilterFieldIds = [.. configuration.FixedFilterFieldIds];
        TopK = configuration.TopK;
        ScoreThreshold = configuration.ScoreThreshold;
        MaxResultCharacters = configuration.MaxResultCharacters;
        Scope = configuration.Scope;
        ScopeTenantIds = [.. configuration.ScopeTenantIds];
        ScopeAssistantIds = [.. configuration.ScopeAssistantIds];
        UpdatedAt = now;
    }
}

/// <summary>What an administrator configures on a rag tool, validated: the content of a <see cref="RagTool"/>.</summary>
internal sealed record RagConfiguration(
    IReadOnlyList<Guid> KnowledgeBaseIds,
    IReadOnlyList<Guid> ExposedFieldIds,
    string FixedFilters,
    IReadOnlyList<Guid> FixedFilterFieldIds,
    int TopK,
    double? ScoreThreshold,
    int MaxResultCharacters,
    ToolScopeKind Scope,
    IReadOnlyList<Guid> ScopeTenantIds,
    IReadOnlyList<Guid> ScopeAssistantIds,
    IReadOnlyDictionary<Guid, string>? ExposedAliases = null);
