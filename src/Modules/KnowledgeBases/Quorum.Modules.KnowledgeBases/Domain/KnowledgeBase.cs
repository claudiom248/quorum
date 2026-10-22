using Quorum.Modules.KnowledgeBases.Mapping;

namespace Quorum.Modules.KnowledgeBases.Domain;

internal enum KnowledgeBaseStatus
{
    Available,

    /// <summary>Could not be used at the last check: registered anyway, searching it fails until it is ready (D-8, D-14).</summary>
    Unavailable,
}

/// <summary>Who may use a knowledge base (D-5). <c>Agent</c> is reserved for spec 005 and rejected until then.</summary>
internal enum KnowledgeBaseScope
{
    Global,
    Tenant,
    Assistant,
}


/// <summary>A metadata field of the payload bound to a dictionary field.</summary>
/// <param name="Listable">The administrator wants the distinct values of this field as a lookup (spec 007 D-3). Mappings saved before read as <see langword="false"/>.</param>
internal sealed record MappedField(string Path, Guid DictionaryFieldId, bool Listable = false);

/// <summary>Which payload paths carry the chunk's text and its source (roles), and which are metadata fields (D-9). Stored with the knowledge base.</summary>
internal sealed class KnowledgeBaseMapping
{
    // A class with a constructor of its own for EF: constructor binding cannot reach the navigations to the roles and the fields.
    private KnowledgeBaseMapping()
    {
    }

    public KnowledgeBaseMapping(MappingRoles roles, List<MappedField> fields, CountedPoint? countedPoint = null, string? entityName = null)
    {
        Roles = roles;
        Fields = fields;
        CountedPoint = countedPoint;
        EntityName = entityName;
    }

    public MappingRoles Roles { get; private set; } = null!;

    public List<MappedField> Fields { get; private set; } = [];

    /// <summary>What a lookup counts, when the points of a document can be told apart (spec 007 D-2). Not set in mappings saved before lookups.</summary>
    public CountedPoint? CountedPoint { get; private set; }

    /// <summary>The payload path of the name of an entity, when the points of the knowledge base are entities and not chunks (spec 007 D-4).</summary>
    public string? EntityName { get; private set; }
}

/// <summary>
/// An already populated knowledge base the administrator registered (D-1). Quorum only consumes it: the content stays in the store.
/// </summary>
internal sealed class KnowledgeBase
{
    public const int MaxNameLength = 40;

    public const int MaxDescriptionLength = 1000;

    public const int MaxQueryPrefixLength = 200;

    public const int DefaultLookupRefreshHours = 24;

    public const int MinLookupRefreshHours = 1;

    /// <summary>Seven days.</summary>
    public const int MaxLookupRefreshHours = 168;

    /// <summary>What a search is told about this knowledge base also reaches the model, so the stored problem stays short.</summary>
    public const int MaxErrorLength = 1000;

    private KnowledgeBase()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>The owner: the only tenant that can change or delete the registration, whatever its scope.</summary>
    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = null!;

    /// <summary>Shown to the model, so it says what the knowledge base is about.</summary>
    public string Description { get; private set; } = null!;

    public KnowledgeBaseKind Kind { get; private set; }

    public ConnectionSettings Connection { get; private set; } = null!;

    public string EmbeddingModel { get; private set; } = null!;

    /// <summary>Put before the query text before it is embedded (<c>search_query: </c> for nomic, e5 and bge models).</summary>
    public string? QueryPrefix { get; private set; }

    public KnowledgeBaseMapping Mapping { get; private set; } = null!;

    public KnowledgeBaseScope Scope { get; private set; }

    /// <summary>With <see cref="KnowledgeBaseScope.Tenant"/>: the tenants besides the owner that may use the knowledge base.</summary>
    public List<Guid> ScopeTenantIds { get; private set; } = [];

    /// <summary>With <see cref="KnowledgeBaseScope.Assistant"/>: the assistants that may use the knowledge base.</summary>
    public List<Guid> ScopeAssistantIds { get; private set; } = [];

    /// <summary>How often the lookups of this knowledge base are computed again (spec 007 D-10).</summary>
    public int LookupRefreshHours { get; private set; } = DefaultLookupRefreshHours;

    public KnowledgeBaseStatus Status { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset? LastVerifiedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static KnowledgeBase Create(
        Guid tenantId,
        string name,
        string description,
        KnowledgeBaseKind kind,
        ConnectionSettings connection,
        string embeddingModel,
        string? queryPrefix,
        KnowledgeBaseMapping mapping,
        KnowledgeBaseScope scope,
        IEnumerable<Guid> scopeTenantIds,
        IEnumerable<Guid> scopeAssistantIds,
        DateTimeOffset now,
        int lookupRefreshHours = DefaultLookupRefreshHours) => new()
        {
            Id = Guid.CreateVersion7(now),
            TenantId = tenantId,
            Name = name,
            Description = description,
            Kind = kind,
            Connection = connection,
            EmbeddingModel = embeddingModel,
            QueryPrefix = queryPrefix,
            Mapping = mapping,
            Scope = scope,
            ScopeTenantIds = [.. scopeTenantIds],
            ScopeAssistantIds = [.. scopeAssistantIds],
            LookupRefreshHours = lookupRefreshHours,
            Status = KnowledgeBaseStatus.Unavailable,
            CreatedAt = now,
            UpdatedAt = now,
        };

    public void Edit(
        string name,
        string description,
        KnowledgeBaseKind kind,
        ConnectionSettings connection,
        string embeddingModel,
        string? queryPrefix,
        KnowledgeBaseMapping mapping,
        KnowledgeBaseScope scope,
        IEnumerable<Guid> scopeTenantIds,
        IEnumerable<Guid> scopeAssistantIds,
        DateTimeOffset now,
        int lookupRefreshHours)
    {
        Name = name;
        Description = description;
        Kind = kind;
        Connection = connection;
        EmbeddingModel = embeddingModel;
        QueryPrefix = queryPrefix;
        Mapping = mapping;
        Scope = scope;
        ScopeTenantIds = [.. scopeTenantIds];
        ScopeAssistantIds = [.. scopeAssistantIds];
        LookupRefreshHours = lookupRefreshHours;
        UpdatedAt = now;
    }

    public void MarkAvailable(DateTimeOffset now)
    {
        Status = KnowledgeBaseStatus.Available;
        LastError = null;
        LastVerifiedAt = now;
    }

    public void MarkUnavailable(string error, DateTimeOffset now)
    {
        Status = KnowledgeBaseStatus.Unavailable;
        LastError = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
        LastVerifiedAt = now;
    }
}
