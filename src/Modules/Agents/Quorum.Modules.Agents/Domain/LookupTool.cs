using System.Text.Json;

namespace Quorum.Modules.Agents.Domain;

/// <summary>
/// The binding of a lookup tool to lookups (spec 007 D-12): which lists the model can read, which knowledge bases the live operations
/// may query, which filters it may use and which it can never change, and who may use the tool. The tool itself (name, description,
/// state, policy, activation) is a <see cref="Tool"/> of origin <see cref="ToolOrigin.Lookup"/>; this is its configuration, one row per tool.
/// </summary>
internal sealed class LookupTool
{
    public const int MaxLookups = 10;

    public const int MaxKnowledgeBases = 10;

    public const int MaxExposedFields = 10;

    /// <summary>The most characters a page of values takes in a result (spec 007 D-13); the tenant's limit on tool results applies if it is lower.</summary>
    public const int ResultCharacters = 6_000;

    private LookupTool()
    {
    }

    public Guid ToolId { get; private set; }

    /// <summary>The owner: the only tenant that can change or delete the tool, whatever its scope.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>Lookups by id, not name: a renamed field keeps its lookup in the tool. One that is deleted or no longer visible is ignored when the tool is used.</summary>
    public List<Guid> LookupIds { get; private set; } = [];

    /// <summary>The alias a lookup was given, as a JSON object by lookup id; a lookup without one is known by its own name.</summary>
    public string LookupAliases { get; private set; } = "{}";

    /// <summary>The knowledge bases the live operations query.</summary>
    public List<Guid> KnowledgeBaseIds { get; private set; } = [];

    /// <summary>The dictionary fields the model may filter on.</summary>
    public List<Guid> ExposedFieldIds { get; private set; } = [];

    /// <summary>The alias an exposed field was given, as a JSON object by field id; a field without one is known by its name (spec 003 A1-3).</summary>
    public string ExposedAliases { get; private set; } = "{}";

    /// <summary>The filters the model never sees and cannot override, as a JSON object in the shape of the tool's parameters.</summary>
    public string FixedFilters { get; private set; } = "{}";

    /// <summary>The dictionary fields the fixed filters name, kept so that a field in use cannot be deleted.</summary>
    public List<Guid> FixedFilterFieldIds { get; private set; } = [];

    public ToolScopeKind Scope { get; private set; }

    /// <summary>With <see cref="ToolScopeKind.Tenant"/>: the tenants besides the owner that may use the tool.</summary>
    public List<Guid> ScopeTenantIds { get; private set; } = [];

    /// <summary>With <see cref="ToolScopeKind.Assistant"/>: the owner's assistants that may use the tool.</summary>
    public List<Guid> ScopeAssistantIds { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static LookupTool Create(Guid toolId, Guid tenantId, LookupConfiguration configuration, DateTimeOffset now)
    {
        var tool = new LookupTool { ToolId = toolId, TenantId = tenantId, CreatedAt = now };
        tool.Apply(configuration, now);
        return tool;
    }

    public void Apply(LookupConfiguration configuration, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        LookupIds = [.. configuration.LookupIds];
        LookupAliases = JsonSerializer.Serialize(configuration.LookupAliases ?? new Dictionary<Guid, string>());
        KnowledgeBaseIds = [.. configuration.KnowledgeBaseIds];
        ExposedFieldIds = [.. configuration.ExposedFieldIds];
        ExposedAliases = JsonSerializer.Serialize(configuration.ExposedAliases ?? new Dictionary<Guid, string>());
        FixedFilters = configuration.FixedFilters;
        FixedFilterFieldIds = [.. configuration.FixedFilterFieldIds];
        Scope = configuration.Scope;
        ScopeTenantIds = [.. configuration.ScopeTenantIds];
        ScopeAssistantIds = [.. configuration.ScopeAssistantIds];
        UpdatedAt = now;
    }
}

/// <summary>What an administrator configures on a lookup tool, validated: the content of a <see cref="LookupTool"/>.</summary>
internal sealed record LookupConfiguration(
    IReadOnlyList<Guid> LookupIds,
    IReadOnlyDictionary<Guid, string>? LookupAliases,
    IReadOnlyList<Guid> KnowledgeBaseIds,
    IReadOnlyList<Guid> ExposedFieldIds,
    IReadOnlyDictionary<Guid, string>? ExposedAliases,
    string FixedFilters,
    IReadOnlyList<Guid> FixedFilterFieldIds,
    ToolScopeKind Scope,
    IReadOnlyList<Guid> ScopeTenantIds,
    IReadOnlyList<Guid> ScopeAssistantIds);
