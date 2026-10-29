namespace Quorum.Modules.KnowledgeBases.Domain;

/// <summary>Where the values of a lookup come from (spec 007 D-1).</summary>
internal enum LookupKind
{
    /// <summary>The distinct values of one listable field of a knowledge base, with the number of documents of each.</summary>
    Computed,

    /// <summary>The entities of a collection the data owner keeps, with their attributes.</summary>
    Entity,

    /// <summary>Values the administrator typed: configuration, not content of a knowledge base.</summary>
    Manual,
}

/// <summary>What the count of a value counts (D-2). It is shown to the model so that chunk counts are never presented as document counts.</summary>
internal enum LookupUnit
{
    Documents,
    Chunks,
}

/// <summary>Who may use a manual lookup (D-5). The scope of the other kinds is the one of their knowledge base.</summary>
internal enum LookupScope
{
    Global,
    Tenant,
}

/// <summary>Where the rows of an entity lookup are read from (spec 012 D-4). A manual lookup is always a snapshot.</summary>
internal enum LookupStorage
{
    /// <summary>The rows are kept in the platform database, computed from the knowledge base.</summary>
    Snapshot,

    /// <summary>Nothing is kept: the rows are read from the store at each request.</summary>
    Live,
}

/// <summary>The type of an attribute of a lookup (spec 012 D-3).</summary>
internal enum LookupAttributeType
{
    Text,
    Number,
    Date,
    TextList,
}

/// <summary>
/// One attribute of an entity or manual lookup (spec 012 D-3). <paramref name="Key"/> is the name of the value in the stored JSON;
/// <paramref name="DictionaryFieldId"/> is set for an entity lookup, whose attributes are mapped fields.
/// </summary>
internal sealed record LookupAttribute(string Key, string? Label, LookupAttributeType Type, Guid? DictionaryFieldId = null);

/// <summary>
/// A named list of values the model can read (spec 007, spec 012). A computed or entity lookup belongs to its knowledge base and goes
/// with it; the values live in its slices.
/// </summary>
internal sealed class Lookup
{
    public const int MaxNameLength = 100;

    public const int MaxDescriptionLength = 500;

    /// <summary>The values of a manual lookup (D-5, D-9).</summary>
    public const int MaxManualValues = 5000;

    /// <summary>The attributes of one lookup (spec 012 D-3).</summary>
    public const int MaxAttributes = 10;

    private Lookup()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public LookupKind Kind { get; private set; }

    /// <summary><see langword="null"/> for a manual lookup.</summary>
    public Guid? KnowledgeBaseId { get; private set; }

    /// <summary>The field a computed lookup lists. The field is never changed, so the lookup follows it.</summary>
    public Guid? DictionaryFieldId { get; private set; }

    /// <summary>Unique per tenant across the three kinds. A computed lookup is <c>&lt;knowledge base&gt;.&lt;field&gt;</c>, an entity lookup the knowledge base name (spec 012 D-2).</summary>
    public string Name { get; private set; } = null!;

    /// <summary>What the model reads to choose the lookup (spec 012 D-2). Always present.</summary>
    public string Description { get; private set; } = null!;

    /// <summary>Only for an entity lookup: <see cref="LookupStorage.Live"/> reads the store instead of keeping rows.</summary>
    public LookupStorage Storage { get; private set; } = LookupStorage.Snapshot;

    /// <summary>The attributes of an entity or manual lookup (spec 012 D-3). A computed lookup has none.</summary>
    public List<LookupAttribute> Attributes { get; private set; } = [];

    /// <summary>Only for a manual lookup.</summary>
    public LookupScope? Scope { get; private set; }

    /// <summary>With <see cref="LookupScope.Tenant"/>: the tenants besides the owner that may use the lookup.</summary>
    public List<Guid> ScopeTenantIds { get; private set; } = [];

    /// <summary><see langword="null"/> for a manual or entity lookup, which have no counts.</summary>
    public LookupUnit? Unit { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>What the lookup counts changed because the mapping did (a counted point or a document id was added or removed).</summary>
    public void CountAs(LookupUnit unit) => Unit = unit;

    /// <summary>Renames the lookup and changes its description, for any kind. The tenant that owns it never changes.</summary>
    public void Rename(string name, string description)
    {
        Name = name;
        Description = description;
    }

    /// <summary>Changes what the administrator can edit of a manual lookup.</summary>
    public void Revise(string name, string description, LookupScope scope, IEnumerable<Guid> scopeTenantIds)
    {
        Rename(name, description);
        Scope = scope;
        ScopeTenantIds = [.. scopeTenantIds];
    }

    /// <summary>Changes the storage of an entity lookup (spec 012 D-4).</summary>
    public void Store(LookupStorage storage) => Storage = storage;

    /// <summary>Replaces the attributes; the caller has validated them.</summary>
    public void SetAttributes(IEnumerable<LookupAttribute> attributes) => Attributes = [.. attributes];

    public static Lookup Computed(Guid tenantId, Guid knowledgeBaseId, Guid dictionaryFieldId, LookupUnit unit, string name, string description, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        TenantId = tenantId,
        Kind = LookupKind.Computed,
        KnowledgeBaseId = knowledgeBaseId,
        DictionaryFieldId = dictionaryFieldId,
        Name = name,
        Description = description,
        Unit = unit,
        CreatedAt = now,
    };

    public static Lookup Entity(Guid tenantId, Guid knowledgeBaseId, string name, string description, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        TenantId = tenantId,
        Kind = LookupKind.Entity,
        KnowledgeBaseId = knowledgeBaseId,
        Name = name,
        Description = description,
        CreatedAt = now,
    };

    public static Lookup Manual(
        Guid tenantId,
        string name,
        string description,
        LookupScope scope,
        IEnumerable<Guid> scopeTenantIds,
        DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(now),
            TenantId = tenantId,
            Kind = LookupKind.Manual,
            Name = name,
            Description = description,
            Scope = scope,
            ScopeTenantIds = [.. scopeTenantIds],
            CreatedAt = now,
        };
}
