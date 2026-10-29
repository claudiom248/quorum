namespace Quorum.Modules.Agents.Contracts;

/// <summary>Which lookups and dictionary fields the lookup tools use, so that KnowledgeBases can refuse to delete a manual lookup or a field that is in use.</summary>
public interface ILookupToolUsage
{
    /// <summary>
    /// Every lookup tool of every tenant. All tenants, because a Global lookup or field is used by tools its caller cannot see; callers
    /// name only their own tools and count the others.
    /// </summary>
    Task<IReadOnlyList<LookupToolUse>> ListAsync(CancellationToken cancellationToken);
}

/// <param name="LookupIds">The lookups the tool binds.</param>
/// <param name="FieldIds">The dictionary fields the tool exposes to the model or fixes a filter on.</param>
/// <param name="KnowledgeBaseIds">The live knowledge bases the tool queries (spec 012 A1-2).</param>
/// <param name="LookupAliases">The alias a lookup was bound with, by lookup id; a lookup without one is known by its own name.</param>
/// <param name="AttributeKeys">The attributes the tool fixes a condition on (spec 012 D-7), by lookup id.</param>
public sealed record LookupToolUse(
    Guid TenantId,
    string ToolName,
    IReadOnlyList<Guid> LookupIds,
    IReadOnlyList<Guid> FieldIds,
    IReadOnlyList<Guid> KnowledgeBaseIds,
    IReadOnlyDictionary<Guid, string> LookupAliases,
    IReadOnlyDictionary<Guid, IReadOnlyList<string>>? AttributeKeys = null);
