using Quorum.Modules.Agents.Domain;
using Quorum.Modules.KnowledgeBases.Contracts;

namespace Quorum.Modules.Agents.Tools.Lookup;

/// <summary>What an administrator should know about a lookup tool whose lookups or fields changed since it was saved.</summary>
internal static class LookupToolWarnings
{
    /// <param name="visible">The lookups of the tool that its owner can see now.</param>
    /// <param name="seen">The catalog's description of the tool's knowledge bases and fields, as the owner sees them now.</param>
    public static IReadOnlyList<ToolWarning> Evaluate(LookupTool binding, IReadOnlyList<LookupInfo> visible, BindingDescription seen)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(visible);
        ArgumentNullException.ThrowIfNull(seen);

        var warnings = new List<ToolWarning>();

        var wanted = binding.LookupIds.Distinct().ToList();
        var found = wanted.Count(id => visible.Any(l => l.Id == id));
        if (found == 0)
        {
            warnings.Add(new ToolWarning("lookup_tool.no_lookup_visible", "None of the lookups of this tool exist or are visible any more, so the tool is not offered to any assistant."));
        }
        else if (found < wanted.Count)
        {
            warnings.Add(new ToolWarning("lookup_tool.lookup_missing", $"{wanted.Count - found} of the lookups of this tool no longer exist or are no longer visible; the tool offers the others."));
        }

        var known = seen.Fields.Select(f => f.Id).ToHashSet();
        if (binding.ExposedFieldIds.Distinct().Any(id => !known.Contains(id)))
        {
            warnings.Add(new ToolWarning("lookup_tool.field_missing", "A field exposed to the model no longer exists or is no longer visible; it is left out of the tool's parameters."));
        }

        if (binding.FixedFilterFieldIds.Distinct().Any(id => !known.Contains(id)))
        {
            warnings.Add(new ToolWarning("lookup_tool.fixed_filter_field_missing", "A field of a fixed filter no longer exists or is no longer visible, so every read fails until the fixed filters are corrected."));
        }

        return warnings;
    }

    /// <summary>
    /// The filters of a tool that no index in the store backs (spec 007 D-8): the fields the model may filter on and the ones the tool
    /// always filters on, in the knowledge bases the live operations query and those the lists come from. A live call on them is allowed
    /// but can be slow, and ends at the 15 s limit.
    /// </summary>
    public static async Task<IReadOnlyList<ToolWarning>> UnindexedAsync(
        IKnowledgeBaseLookups lookups,
        Guid tenantId,
        LookupTool binding,
        IReadOnlyList<LookupInfo> visible,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lookups);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(visible);

        var knowledgeBases = binding.KnowledgeBaseIds.Concat(visible.Select(l => l.KnowledgeBaseId).OfType<Guid>()).Distinct().ToList();
        var fields = binding.ExposedFieldIds.Concat(binding.FixedFilterFieldIds).Distinct().ToList();
        if (knowledgeBases.Count == 0 || fields.Count == 0)
        {
            return [];
        }

        var unindexed = await lookups.UnindexedFiltersAsync(new UnindexedFiltersQuery(tenantId, knowledgeBases, fields), cancellationToken);
        return
        [
            .. unindexed.Select(u => new ToolWarning(
                "lookup_tool.filter_unindexed",
                $"The field '{u.FieldName}' has no payload index in {u.KnowledgeBaseName} ({u.Path}): a filter on it may take a long time, and a call ends after 15 seconds. Ask the data owner to index it.")),
        ];
    }
}
