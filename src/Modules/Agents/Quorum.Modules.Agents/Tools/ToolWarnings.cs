namespace Quorum.Modules.Agents.Tools;

internal sealed record ToolWarning(string Code, string Message);

/// <summary>
/// The risks of giving an assistant many tools: small models pick the right tool less often, and the definitions use
/// context that the conversation needs. Nothing is blocked; the admin is told.
/// </summary>
internal static class ToolWarnings
{
    public const int MaxRecommendedTools = 15;

    /// <summary>Share of the usable context that the tool definitions may take before the admin is warned.</summary>
    public const double MaxDefinitionShare = 0.2;

    public static IReadOnlyList<ToolWarning> Evaluate(int activeTools, int definitionCharacters, int usableCharacters)
    {
        List<ToolWarning> warnings = [];

        if (activeTools > MaxRecommendedTools)
        {
            warnings.Add(new ToolWarning(
                "tools.too_many_active",
                $"{activeTools} tools are active. Small models choose the right tool less reliably as the number grows; " +
                $"keep an assistant to {MaxRecommendedTools} tools or fewer unless its model is a large one."));
        }

        if (usableCharacters > 0 && definitionCharacters > usableCharacters * MaxDefinitionShare)
        {
            warnings.Add(new ToolWarning(
                "tools.definitions_large",
                $"The active tools' definitions take {definitionCharacters} of the {usableCharacters} characters of the model's context " +
                $"(more than {MaxDefinitionShare:P0}). Deactivate tools you do not need or raise the context budget in the settings."));
        }

        return warnings;
    }
}
