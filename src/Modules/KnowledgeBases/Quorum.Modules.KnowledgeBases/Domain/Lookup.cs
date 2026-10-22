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

/// <summary>
/// A named list of values the model can read (spec 007). A computed or entity lookup belongs to its knowledge base and goes with it;
/// the values live in its slices.
/// </summary>
internal sealed class Lookup
{
    public const int MaxNameLength = 40;

    public const int MaxDescriptionLength = 500;

    /// <summary>The values of a manual lookup (D-5, D-9).</summary>
    public const int MaxManualValues = 5000;

    private Lookup()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public LookupKind Kind { get; private set; }

    /// <summary><see langword="null"/> for a manual lookup.</summary>
    public Guid? KnowledgeBaseId { get; private set; }

    /// <summary>The field a computed lookup lists. Its name is the lookup's name; the field is never changed, so the lookup follows it.</summary>
    public Guid? DictionaryFieldId { get; private set; }

    /// <summary>Only for a manual lookup: the other kinds are named after their field or their knowledge base.</summary>
    public string? Name { get; private set; }

    public string? Description { get; private set; }

    /// <summary>Only for a manual lookup.</summary>
    public LookupScope? Scope { get; private set; }

    /// <summary>With <see cref="LookupScope.Tenant"/>: the tenants besides the owner that may use the lookup.</summary>
    public List<Guid> ScopeTenantIds { get; private set; } = [];

    /// <summary><see langword="null"/> for a manual or entity lookup, which have no counts.</summary>
    public LookupUnit? Unit { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Changes what the administrator can edit of a manual lookup. The tenant that owns it never changes.</summary>
    public void Revise(string name, string? description, LookupScope scope, IEnumerable<Guid> scopeTenantIds)
    {
        Name = name;
        Description = description;
        Scope = scope;
        ScopeTenantIds = [.. scopeTenantIds];
    }

    public static Lookup Computed(Guid tenantId, Guid knowledgeBaseId, Guid dictionaryFieldId, LookupUnit unit, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        TenantId = tenantId,
        Kind = LookupKind.Computed,
        KnowledgeBaseId = knowledgeBaseId,
        DictionaryFieldId = dictionaryFieldId,
        Unit = unit,
        CreatedAt = now,
    };

    public static Lookup Entity(Guid tenantId, Guid knowledgeBaseId, string? description, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        TenantId = tenantId,
        Kind = LookupKind.Entity,
        KnowledgeBaseId = knowledgeBaseId,
        Description = description,
        CreatedAt = now,
    };

    public static Lookup Manual(
        Guid tenantId,
        string name,
        string? description,
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
