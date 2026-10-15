namespace Quorum.Modules.Agents.Contracts;

public static class AssistantDefaults
{
    public const string AssistantName = "Assistant";

    public const int ContextTokenBudget = 16384;

    public const int MinContextTokenBudget = 256;

    public const int MaxContextTokenBudget = 1_000_000;

    public const int MaxSystemPromptLength = 20_000;

    public const string SystemPrompt =
        "You are a helpful assistant for the people of this organization. " +
        "Answer in the same language as the user's message. " +
        "Be accurate and concise, and say so when you are not sure instead of guessing.";
}
