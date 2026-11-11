using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Tools;

namespace Quorum.Modules.Agents.Chat;

/// <summary>
/// The root agent's tools a turn offers once the conversation's selection is applied (spec 018 D-4, D-5, D-17). Only the root's tools
/// are selectable: sub-agents' tools never pass through here.
/// </summary>
internal static class ToolSelection
{
    /// <summary>
    /// A consumer turns off optional tools only: a name that is required in storage (it was made required after the conversation
    /// was created) stays on. A debug conversation may turn off any tool. Unknown names are ignored here; creation rejects them.
    /// </summary>
    public static IReadOnlyList<ActiveTool> Effective(IReadOnlyList<ActiveTool> root, IReadOnlyCollection<string> disabledNames, bool debug)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(disabledNames);

        if (disabledNames.Count == 0)
        {
            return root;
        }

        var disabled = disabledNames.ToHashSet(StringComparer.Ordinal);
        return [.. root.Where(tool => !disabled.Contains(tool.ExposedName) || (!debug && tool.Mode == ToolMode.Required))];
    }
}
