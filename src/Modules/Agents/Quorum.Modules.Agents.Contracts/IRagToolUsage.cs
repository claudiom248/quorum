namespace Quorum.Modules.Agents.Contracts;

/// <summary>Which dictionary fields the rag tools use, so that KnowledgeBases can count them and refuse to delete one that is in use.</summary>
public interface IRagToolUsage
{
    /// <summary>
    /// Every rag tool of every tenant with the fields it exposes or filters on. All tenants, because a Global field is used by tools its
    /// caller cannot see; callers name only their own tools and count the others.
    /// </summary>
    Task<IReadOnlyList<RagToolUse>> ListAsync(CancellationToken cancellationToken);
}

/// <param name="FieldIds">The dictionary fields the tool exposes to the model or fixes a filter on.</param>
public sealed record RagToolUse(Guid TenantId, string ToolName, IReadOnlyList<Guid> FieldIds);
