namespace Quorum.Modules.Agents.Contracts;

public static class AssistantDefaults
{
    public const string AssistantName = "Assistant";

    public const int ContextTokenBudget = 16384;

    public const int MinContextTokenBudget = 256;

    public const int MaxContextTokenBudget = 1_000_000;

    public const int MaxSystemPromptLength = 20_000;

    /// <summary>The description of the agent the setup creates; it only matters if the agent is ever called by another.</summary>
    public const string AgentDescription = "The general-purpose assistant of the organization.";

    public const string SystemPrompt =
        "You are a helpful assistant for the people of this organization. " +
        "Answer in the same language as the user's message. " +
        "Be accurate and concise, and say so when you are not sure instead of guessing.";
}
