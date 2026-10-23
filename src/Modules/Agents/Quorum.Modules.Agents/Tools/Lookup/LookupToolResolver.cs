using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.KnowledgeBases.Contracts;

namespace Quorum.Modules.Agents.Tools.Lookup;

/// <summary>A lookup tool to describe: the stored text and binding.</summary>
internal sealed record LookupToolToDescribe(Guid ToolId, string Description, LookupBinding Binding);

/// <summary>A lookup tool as the model is offered it now.</summary>
/// <param name="Lookups">The lookups the tenant can see, by the name the model was offered.</param>
internal sealed record ResolvedLookupTool(
    GeneratedLookupDefinition Definition,
    IReadOnlyDictionary<string, Guid> Lookups,
    IReadOnlyList<LookupInfo> Visible,
    IReadOnlyDictionary<string, Guid> KnowledgeBases,
    IReadOnlyDictionary<string, Guid> FilterFields)
{
    /// <summary>The attributes each entity list can be ordered by, by list name.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> SortableAttributes { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>The list that holds the exact values of a filter parameter, by parameter name.</summary>
    public IReadOnlyDictionary<string, string> FilterLists { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Builds the definition of lookup tools from the live catalog (spec 007 D-12): at the start of each answer and not when the tool is
/// saved, so a renamed or deleted lookup never leaves a stale enum, and one the tenant can no longer see is simply absent. A tool left
/// with no visible lookup is dropped: it is not offered.
/// </summary>
internal sealed class LookupToolResolver(AgentsDbContext db, IKnowledgeBaseLookups lookups, IKnowledgeBaseCatalog catalog, IMemoryCache cache)
{
    private static readonly TimeSpan RecentFor = TimeSpan.FromSeconds(30);

    /// <param name="assistantId">The assistant answering, or <see langword="null"/> for an administrator looking at a configuration.</param>
    /// <returns>The tools that can be offered, by tool id.</returns>
    public async Task<IReadOnlyDictionary<Guid, ResolvedLookupTool>> ResolveAsync(
        Guid tenantId,
        Guid? assistantId,
        IReadOnlyCollection<LookupToolToDescribe> tools,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tools);

        if (tools.Count == 0)
        {
            return new Dictionary<Guid, ResolvedLookupTool>();
        }

        var seen = await lookups.ListAsync(
            new LookupListQuery(tenantId, assistantId, [.. tools.SelectMany(t => t.Binding.LookupIds).Distinct()]),
            cancellationToken);
        var byId = seen.ToDictionary(l => l.Id);

        // The knowledge bases of the live operations and those the lists come from: a filter is offered for a field either maps.
        var fieldIds = tools.SelectMany(t => t.Binding.ExposedFieldIds).Distinct().ToList();
        var basesWanted = tools.SelectMany(t => t.Binding.KnowledgeBaseIds).Concat(seen.Select(l => l.KnowledgeBaseId).OfType<Guid>()).Distinct().ToList();
        var description = fieldIds.Count == 0 && basesWanted.Count == 0
            ? new BindingDescription([], [])
            : await catalog.DescribeAsync(new BindingQuery(tenantId, assistantId, basesWanted, fieldIds), cancellationToken);
        var basesById = description.KnowledgeBases.ToDictionary(k => k.Id);
        var fieldsById = description.Fields.ToDictionary(f => f.Id);

        var resolved = new Dictionary<Guid, ResolvedLookupTool>();
        foreach (var tool in tools)
        {
            // In the order of the names so the definition does not depend on the order the administrator picked them in. Two lookups that
            // came to share a name since the tool was saved (a field renamed) keep the first: the model must tell them apart by name.
            var offered = tool.Binding.LookupIds.Distinct()
                .Where(byId.ContainsKey)
                .Select(id => new OfferedLookup(tool.Binding.LookupAliases.GetValueOrDefault(id) ?? byId[id].Name, byId[id]))
                .OrderBy(l => l.Name, StringComparer.Ordinal)
                .ThenBy(l => l.Info.Id)
                .DistinctBy(l => l.Name, StringComparer.Ordinal)
                .ToList();
            if (offered.Count == 0)
            {
                continue;
            }

            // The live operations query only the knowledge bases the tool names that the tenant sees; a tool with none keeps its lists.
            var queried = tool.Binding.KnowledgeBaseIds.Distinct().Where(basesById.ContainsKey).Select(id => basesById[id]).OrderBy(k => k.Name, StringComparer.Ordinal).ToList();
            // Only the knowledge bases a filter can reach: the ones the tool asks and those of the lists computed from a field. A list of
            // entities cannot be filtered, so the fields only its knowledge base maps would be parameters nothing applies.
            var mapping = queried
                .SelectMany(k => k.FieldIds)
                .Concat(offered
                    .Where(l => l.Info.Kind == LookupKindInfo.Computed)
                    .Select(l => l.Info.KnowledgeBaseId)
                    .OfType<Guid>()
                    .Where(basesById.ContainsKey)
                    .SelectMany(id => basesById[id].FieldIds))
                .ToHashSet();

            // A field is offered only while a visible knowledge base maps it: a parameter nothing can apply would only mislead the model.
            var exposed = tool.Binding.ExposedFieldIds.Distinct()
                .Where(id => fieldsById.ContainsKey(id) && mapping.Contains(id))
                .Select(id => Named(fieldsById[id], tool.Binding.ExposedAliases.GetValueOrDefault(id)))
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .ToList();

            resolved[tool.ToolId] = new ResolvedLookupTool(
                LookupToolDefinition.Build(tool.Description, offered, queried, exposed),
                offered.ToDictionary(l => l.Name, l => l.Info.Id, StringComparer.Ordinal),
                [.. offered.Select(l => l.Info)],
                queried.ToDictionary(k => k.Name, k => k.Id, StringComparer.Ordinal),
                exposed.GroupBy(f => f.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal))
            {
                SortableAttributes = offered
                    .Where(l => l.Info.SortableAttributes is { Count: > 0 })
                    .ToDictionary(l => l.Name, l => l.Info.SortableAttributes!, StringComparer.Ordinal),
                FilterLists = LookupToolDefinition.ListsOfFilters(offered, exposed),
            };
        }

        return resolved;
    }

    /// <summary>The lookup tools of a pool that an assistant of the tenant can be offered, by tool id: those with at least one visible lookup.</summary>
    public async Task<IReadOnlyDictionary<Guid, ResolvedLookupTool>> ResolvePoolAsync(
        IReadOnlyList<PoolTool> pool,
        Guid tenantId,
        Guid assistantId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pool);

        var tools = pool.Where(t => t.Tool.Origin == ToolOrigin.Lookup).ToList();
        if (tools.Count == 0)
        {
            return new Dictionary<Guid, ResolvedLookupTool>();
        }

        var ids = tools.Select(t => t.Tool.Id).ToList();
        var bindings = await db.LookupTools.IgnoreQueryFilters().AsNoTracking().Where(l => ids.Contains(l.ToolId)).ToListAsync(cancellationToken);
        var described = tools
            .Join(bindings, t => t.Tool.Id, b => b.ToolId, (t, b) => new LookupToolToDescribe(t.Tool.Id, t.Tool.Description, LookupBinding.From(b)))
            .ToList();

        return await ResolveAsync(tenantId, assistantId, described, cancellationToken);
    }

    /// <summary>Gives the lookup tools among <paramref name="tools"/> the definition they have now; those with no visible lookup are left out.</summary>
    /// <param name="allowRecent">Accepts a definition made in the last seconds, for the context gauge, which only needs its size and is asked for as the user types; an answer always gets a fresh one.</param>
    public async Task<IReadOnlyList<ActiveTool>> ResolveActiveAsync(
        IReadOnlyList<ActiveTool> tools,
        Guid tenantId,
        Guid assistantId,
        CancellationToken cancellationToken,
        bool allowRecent = false)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var lookupTools = tools.Where(t => t.Lookup is not null).ToList();
        if (lookupTools.Count == 0)
        {
            return tools;
        }

        // The key has the hash of the stored configuration, so an edited tool is never served from before the edit.
        string KeyOf(ActiveTool tool) => $"lookup-definition:{tenantId}:{assistantId}:{tool.ToolId}:{tool.DefinitionHash}";

        var resolved = new Dictionary<Guid, ResolvedLookupTool?>();
        var missing = new List<ActiveTool>();
        foreach (var tool in lookupTools)
        {
            if (allowRecent && cache.TryGetValue(KeyOf(tool), out Recent? recent) && recent is not null)
            {
                resolved[tool.ToolId] = recent.Tool;
            }
            else
            {
                missing.Add(tool);
            }
        }

        if (missing.Count > 0)
        {
            var fresh = await ResolveAsync(tenantId, assistantId, [.. missing.Select(t => new LookupToolToDescribe(t.ToolId, t.Description, t.Lookup!))], cancellationToken);
            foreach (var tool in missing)
            {
                var found = fresh.GetValueOrDefault(tool.ToolId);
                resolved[tool.ToolId] = found;
                if (allowRecent)
                {
                    cache.Set(KeyOf(tool), new Recent(found), RecentFor);
                }
            }
        }

        return
        [
            .. tools.Select(tool => tool.Lookup is null
                ? tool
                : resolved.GetValueOrDefault(tool.ToolId) is { } definition
                    ? tool with
                    {
                        Description = definition.Definition.Description,
                        InputSchema = definition.Definition.InputSchema,
                        Lookup = tool.Lookup with
                        {
                            Lookups = definition.Lookups,
                            KnowledgeBases = definition.KnowledgeBases,
                            FilterFields = definition.FilterFields,
                            SortableAttributes = definition.SortableAttributes,
                            FilterLists = definition.FilterLists,
                        },
                    }
                    : null).OfType<ActiveTool>(),
        ];
    }

    // The model knows the field by its alias. A field with no description of its own says which cluster it is from, so two fields that
    // share a name (hr/year, finance/year) are not told apart only by their aliases.
    private static FilterFieldInfo Named(FilterFieldInfo field, string? alias) => field with
    {
        Name = alias ?? field.Name,
        Description = field.Description ?? (field.Cluster is null ? null : $"{field.Name} ({field.Cluster})"),
    };

    /// <summary>A cached outcome; <see langword="null"/> inside means the tool had no visible lookup.</summary>
    private sealed record Recent(ResolvedLookupTool? Tool);
}
