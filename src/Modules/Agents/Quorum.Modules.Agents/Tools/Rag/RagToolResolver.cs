using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.KnowledgeBases.Contracts;

namespace Quorum.Modules.Agents.Tools.Rag;

/// <summary>A rag tool to describe: the stored text and binding.</summary>
internal sealed record RagToolToDescribe(Guid ToolId, string Description, RagBinding Binding);

/// <summary>A rag tool as the model is offered it now.</summary>
/// <param name="KnowledgeBases">The knowledge bases the tenant can see, by name.</param>
/// <param name="FilterFields">The filter parameters the model was offered (the aliases of the exposed fields), with the field each stands for.</param>
internal sealed record ResolvedRagTool(GeneratedRagDefinition Definition, IReadOnlyDictionary<string, Guid> KnowledgeBases, IReadOnlyDictionary<string, Guid> FilterFields);

/// <summary>
/// Builds the definition of rag tools from the live catalog (spec 003 D-11): at the start of each answer and not when the tool is
/// saved, so a renamed or deleted knowledge base never leaves a stale enum, and one the tenant can no longer see is simply absent.
/// A tool left with no visible knowledge base is dropped: it is not offered.
/// </summary>
internal sealed partial class RagToolResolver(AgentsDbContext db, IKnowledgeBaseCatalog catalog, IMemoryCache cache, ILogger<RagToolResolver> logger)
{
    /// <param name="assistantId">The assistant answering, or <see langword="null"/> for an administrator looking at a configuration.</param>
    /// <returns>The tools that can be offered, by tool id.</returns>
    public async Task<IReadOnlyDictionary<Guid, ResolvedRagTool>> ResolveAsync(
        Guid tenantId,
        Guid? assistantId,
        IReadOnlyCollection<RagToolToDescribe> tools,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tools);

        if (tools.Count == 0)
        {
            return new Dictionary<Guid, ResolvedRagTool>();
        }

        var description = await catalog.DescribeAsync(
            new BindingQuery(
                tenantId,
                assistantId,
                [.. tools.SelectMany(t => t.Binding.KnowledgeBaseIds).Distinct()],
                [.. tools.SelectMany(t => t.Binding.ExposedFieldIds.Concat(t.Binding.FixedFilterFieldIds)).Distinct()]),
            cancellationToken);

        var basesById = description.KnowledgeBases.ToDictionary(k => k.Id);
        var fieldsById = description.Fields.ToDictionary(f => f.Id);

        var resolved = new Dictionary<Guid, ResolvedRagTool>();
        foreach (var tool in tools)
        {
            // In the order of the catalog (by name) so the definition does not depend on the order the administrator picked them in.
            var visible = tool.Binding.KnowledgeBaseIds.Distinct().Where(basesById.ContainsKey).Select(id => basesById[id]).OrderBy(k => k.Name, StringComparer.Ordinal).ToList();
            if (visible.Count == 0)
            {
                continue;
            }

            // A field deleted (or hidden) after the tool was saved, for instance by a deletion that raced with the save, is dropped quietly
            // for the model: the administrator is told through the log, with ids and counts only.
            var missing = tool.Binding.ExposedFieldIds.Concat(tool.Binding.FixedFilterFieldIds).Distinct().Count(id => !fieldsById.ContainsKey(id));
            if (missing > 0)
            {
                LogMissingFields(logger, tool.ToolId, missing);
            }

            // A field is offered only while a visible knowledge base maps it: a parameter nothing can apply would only mislead the model.
            var exposed = tool.Binding.ExposedFieldIds.Distinct()
                .Where(id => fieldsById.ContainsKey(id) && visible.Any(k => k.FieldIds.Contains(id)))
                .Select(id => fieldsById[id] with { Name = tool.Binding.ExposedAliases.GetValueOrDefault(id) ?? fieldsById[id].Name })
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .ToList();

            resolved[tool.ToolId] = new ResolvedRagTool(
                RagToolDefinition.Build(tool.Description, visible, exposed),
                visible.ToDictionary(k => k.Name, k => k.Id, StringComparer.Ordinal),
                exposed.GroupBy(f => f.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal));
        }

        return resolved;
    }

    /// <summary>The rag tools of a pool that an assistant of the tenant can be offered, by tool id: those with at least one visible knowledge base.</summary>
    public async Task<IReadOnlyDictionary<Guid, ResolvedRagTool>> ResolvePoolAsync(
        IReadOnlyList<PoolTool> pool,
        Guid tenantId,
        Guid assistantId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pool);

        var rag = pool.Where(t => t.Tool.Origin == ToolOrigin.Rag).ToList();
        if (rag.Count == 0)
        {
            return new Dictionary<Guid, ResolvedRagTool>();
        }

        var ids = rag.Select(t => t.Tool.Id).ToList();
        var bindings = await db.RagTools.IgnoreQueryFilters().AsNoTracking().Where(r => ids.Contains(r.ToolId)).ToListAsync(cancellationToken);
        var described = rag
            .Join(bindings, t => t.Tool.Id, b => b.ToolId, (t, b) => new RagToolToDescribe(t.Tool.Id, t.Tool.Description, RagBinding.From(b)))
            .ToList();

        return await ResolveAsync(tenantId, assistantId, described, cancellationToken);
    }

    /// <summary>Gives the rag tools among <paramref name="tools"/> the definition they have now; those with no visible knowledge base are left out.</summary>
    /// <param name="allowRecent">Accepts a definition made in the last seconds, for the context gauge, which only needs its size and is asked for as the user types; an answer always gets a fresh one.</param>
    public async Task<IReadOnlyList<ActiveTool>> ResolveActiveAsync(
        IReadOnlyList<ActiveTool> tools,
        Guid tenantId,
        Guid assistantId,
        CancellationToken cancellationToken,
        bool allowRecent = false)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var rag = tools.Where(t => t.Rag is not null).ToList();
        if (rag.Count == 0)
        {
            return tools;
        }

        // The key has the hash of the stored configuration, so an edited tool is never served from before the edit.
        string KeyOf(ActiveTool tool) => $"rag-definition:{tenantId}:{assistantId}:{tool.ToolId}:{tool.DefinitionHash}";

        var resolved = new Dictionary<Guid, ResolvedRagTool?>();
        var missing = new List<ActiveTool>();
        foreach (var tool in rag)
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
            var fresh = await ResolveAsync(tenantId, assistantId, [.. missing.Select(t => new RagToolToDescribe(t.ToolId, t.Description, t.Rag!))], cancellationToken);
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
            .. tools.Select(tool => tool.Rag is null
                ? tool
                : resolved.GetValueOrDefault(tool.ToolId) is { } definition
                    ? tool with
                    {
                        Description = definition.Definition.Description,
                        InputSchema = definition.Definition.InputSchema,
                        Rag = tool.Rag with { KnowledgeBases = definition.KnowledgeBases, FilterFields = definition.FilterFields },
                    }
                    : null).OfType<ActiveTool>(),
        ];
    }

    private static readonly TimeSpan RecentFor = TimeSpan.FromSeconds(30);

    /// <summary>A cached outcome; <see langword="null"/> inside means the tool had no visible knowledge base.</summary>
    private sealed record Recent(ResolvedRagTool? Tool);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rag tool {ToolId} names {Count} dictionary fields that no longer exist or are no longer visible")]
    private static partial void LogMissingFields(ILogger logger, Guid toolId, int count);
}
