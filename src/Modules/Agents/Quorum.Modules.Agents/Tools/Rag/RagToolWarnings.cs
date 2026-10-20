using Quorum.Modules.Agents.Domain;
using Quorum.Modules.KnowledgeBases.Contracts;

namespace Quorum.Modules.Agents.Tools.Rag;

/// <summary>What an administrator should know about a rag tool whose knowledge bases or fields changed since it was saved.</summary>
internal static class RagToolWarnings
{
    /// <param name="seen">The catalog's description of the tool's knowledge bases and fields, as the owner sees them now.</param>
    public static IReadOnlyList<ToolWarning> Evaluate(RagTool binding, BindingDescription seen)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(seen);

        var warnings = new List<ToolWarning>();

        var wanted = binding.KnowledgeBaseIds.Distinct().ToList();
        var visible = wanted.Count(id => seen.KnowledgeBases.Any(k => k.Id == id));
        if (visible == 0)
        {
            warnings.Add(new ToolWarning("rag.no_knowledge_base_visible", "None of the knowledge bases of this tool exist or are visible any more, so the tool is not offered to any assistant."));
        }
        else if (visible < wanted.Count)
        {
            warnings.Add(new ToolWarning("rag.knowledge_base_missing", $"{wanted.Count - visible} of the knowledge bases of this tool no longer exist or are no longer visible; the tool searches the others."));
        }

        var known = seen.Fields.Select(f => f.Id).ToHashSet();
        var exposedMissing = binding.ExposedFieldIds.Distinct().Count(id => !known.Contains(id));
        if (exposedMissing > 0)
        {
            warnings.Add(new ToolWarning("rag.field_missing", $"{exposedMissing} fields exposed to the model no longer exist or are no longer visible; they are left out of the tool's parameters."));
        }

        if (binding.FixedFilterFieldIds.Distinct().Any(id => !known.Contains(id)))
        {
            warnings.Add(new ToolWarning("rag.fixed_filter_field_missing", "A field of a fixed filter no longer exists or is no longer visible, so every search fails until the fixed filters are corrected."));
        }

        return warnings;
    }
}
