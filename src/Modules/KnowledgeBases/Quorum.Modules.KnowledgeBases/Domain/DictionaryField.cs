namespace Quorum.Modules.KnowledgeBases.Domain;

internal enum DictionaryFieldType
{
    Text,
    Number,
    Boolean,
    Date,
    TextList,
}

/// <summary>Who may use a dictionary field: every tenant, or only the owner.</summary>
internal enum DictionaryFieldScope
{
    Global,
    Tenant,
}

/// <summary>A typed metadata field defined once and reused by knowledge bases and rag tools. Name and type never change.</summary>
internal sealed class DictionaryField
{
    public const int MaxNameLength = 40;

    /// <summary>The description reaches the model in tool definitions, so it stays short.</summary>
    public const int MaxDescriptionLength = 500;

    private DictionaryField()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>The owner: the only tenant that can change or delete the field, whatever its scope.</summary>
    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = null!;

    public DictionaryFieldType Type { get; private set; }

    public string? Description { get; private set; }

    public DictionaryFieldScope Scope { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public void Describe(string? description) => Description = description;

    public static DictionaryField Create(
        Guid tenantId,
        string name,
        DictionaryFieldType type,
        string? description,
        DictionaryFieldScope scope,
        DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(now),
            TenantId = tenantId,
            Name = name,
            Type = type,
            Description = description,
            Scope = scope,
            CreatedAt = now,
        };
}
