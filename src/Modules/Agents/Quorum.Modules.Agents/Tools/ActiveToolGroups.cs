namespace Quorum.Modules.Agents.Tools;

/// <summary>
/// The tools of several agents at once (the root agent and its sub-agents), as the resolvers that give a tool its definition handle them:
/// one catalog read for all of them, and each group back in the order it came in.
/// </summary>
internal static class ActiveToolGroups
{
    /// <summary>The tools of the groups that match, each tool once: the same tool on two agents has the same binding.</summary>
    public static List<ActiveTool> DistinctWhere(IEnumerable<IReadOnlyList<ActiveTool>> groups, Func<ActiveTool, bool> match) =>
        groups.SelectMany(tools => tools).Where(match).DistinctBy(tool => tool.ToolId).ToList();

    /// <summary>Each group again, with every tool replaced by what <paramref name="transform"/> gives it; an empty result drops it.</summary>
    public static IReadOnlyList<IReadOnlyList<ActiveTool>> Rebuild(IReadOnlyList<IReadOnlyList<ActiveTool>> groups, Func<ActiveTool, IEnumerable<ActiveTool>> transform) =>
        [.. groups.Select(tools => (IReadOnlyList<ActiveTool>)[.. tools.SelectMany(transform)])];
}
