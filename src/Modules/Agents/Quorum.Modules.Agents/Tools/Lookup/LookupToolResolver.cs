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
internal sealed record ResolvedLookupTool(GeneratedLookupDefinition Definition, IReadOnlyDictionary<string, Guid> Lookups, IReadOnlyList<LookupInfo> Visible);

/// <summary>
/// Builds the definition of lookup tools from the live catalog (spec 007 D-12): at the start of each answer and not when the tool is
/// saved, so a renamed or deleted lookup never leaves a stale enum, and one the tenant can no longer see is simply absent. A tool left
/// with no visible lookup is dropped: it is not offered.
/// </summary>
internal sealed class LookupToolResolver(AgentsDbContext db, IKnowledgeBaseLookups lookups, IMemoryCache cache)
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

            resolved[tool.ToolId] = new ResolvedLookupTool(
                LookupToolDefinition.Build(tool.Description, offered),
                offered.ToDictionary(l => l.Name, l => l.Info.Id, StringComparer.Ordinal),
                [.. offered.Select(l => l.Info)]);
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
                        Lookup = tool.Lookup with { Lookups = definition.Lookups },
                    }
                    : null).OfType<ActiveTool>(),
        ];
    }

    /// <summary>A cached outcome; <see langword="null"/> inside means the tool had no visible lookup.</summary>
    private sealed record Recent(ResolvedLookupTool? Tool);
}
