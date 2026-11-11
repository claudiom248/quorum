using Microsoft.EntityFrameworkCore;
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
/// <param name="OffersDocumentIds">The definition has <c>document_ids</c>: the tool allows it and a visible knowledge base maps the document id.</param>
internal sealed record ResolvedRagTool(GeneratedRagDefinition Definition, IReadOnlyDictionary<string, Guid> KnowledgeBases, IReadOnlyDictionary<string, Guid> FilterFields, bool OffersDocumentIds = false);

/// <summary>
/// Builds the definition of rag tools from the live catalog (spec 003 D-11): at the start of each answer and not when the tool is
/// saved, so a renamed or deleted knowledge base never leaves a stale enum, and one the tenant can no longer see is simply absent.
/// A tool left with no visible knowledge base is dropped: it is not offered.
/// </summary>
internal sealed partial class RagToolResolver(AgentsDbContext db, IKnowledgeBaseCatalog catalog, ILogger<RagToolResolver> logger)
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

        var knowledgeBaseIds = tools.SelectMany(t => t.Binding.KnowledgeBaseIds).Distinct().ToList();
        var fieldIds = tools.SelectMany(t => t.Binding.ExposedFieldIds.Concat(t.Binding.FixedFilterFieldIds)).Distinct().ToList();
        var description = knowledgeBaseIds.Count == 0 && fieldIds.Count == 0
            ? new BindingDescription([], [])
            : await catalog.DescribeAsync(new BindingQuery(tenantId, assistantId, knowledgeBaseIds, fieldIds), cancellationToken);

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
                .Select(id => OfferedFields.Offered(fieldsById[id], tool.Binding.ExposedAliases.GetValueOrDefault(id)))
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .ToList();

            // Like a field: offered only while a visible knowledge base can apply it.
            var offersDocumentIds = tool.Binding.AllowDocumentIds && visible.Any(k => k.MapsDocumentId);

            resolved[tool.ToolId] = new ResolvedRagTool(
                RagToolDefinition.Build(tool.Description, visible, exposed, offersDocumentIds),
                visible.ToDictionary(k => k.Name, k => k.Id, StringComparer.Ordinal),
                OfferedFields.FirstByName(exposed, field => field.Id),
                offersDocumentIds);
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

    /// <summary>
    /// Gives the rag tools among those of several agents (the root agent and its sub-agents) the definition they have now, those with no
    /// visible knowledge base left out: the catalog is asked one time for all of them, and each group comes back in the order it came in.
    /// </summary>
    public async Task<IReadOnlyList<IReadOnlyList<ActiveTool>>> ResolveActiveGroupsAsync(
        IReadOnlyList<IReadOnlyList<ActiveTool>> groups,
        Guid tenantId,
        Guid assistantId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(groups);

        var rag = ActiveToolGroups.DistinctWhere(groups, tool => tool.Rag is not null);
        if (rag.Count == 0)
        {
            return groups;
        }

        var fresh = await ResolveAsync(tenantId, assistantId, [.. rag.Select(t => new RagToolToDescribe(t.ToolId, t.Description, t.Rag!))], cancellationToken);

        return ActiveToolGroups.Rebuild(groups, tool => tool.Rag is null
            ? [tool]
            : fresh.GetValueOrDefault(tool.ToolId) is { } definition
                ? [tool with
                {
                    Description = definition.Definition.Description,
                    InputSchema = definition.Definition.InputSchema,
                    Rag = tool.Rag with { KnowledgeBases = definition.KnowledgeBases, FilterFields = definition.FilterFields, AllowDocumentIds = definition.OffersDocumentIds },
                }]
                : []);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rag tool {ToolId} names {Count} dictionary fields that no longer exist or are no longer visible")]
    private static partial void LogMissingFields(ILogger logger, Guid toolId, int count);
}
