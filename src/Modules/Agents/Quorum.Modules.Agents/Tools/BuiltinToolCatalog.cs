namespace Quorum.Modules.Agents.Tools;

/// <summary>The definition of a built-in tool as the model sees it. The name has no origin prefix.</summary>
internal sealed record BuiltinToolDefinition(string Name, string Description, string InputSchema);

/// <summary>
/// The tools Quorum runs in-process. A release that changes a definition updates the stored copy at startup
/// (<see cref="BuiltinToolProvisioner"/>); what the admin decided (state, policy) is kept.
/// </summary>
internal static class BuiltinToolCatalog
{
    public const string CurrentDateTime = "current_datetime";

    public const string Calculator = "calculator";

    /// <summary>
    /// The model asks the user a question. The invoker runs it: it pauses the answer like a confirmation does. Its policy is not
    /// consulted, because asking is already the interaction: <c>confirm</c> would only ask the user whether they want to be asked.
    /// </summary>
    public const string AskUser = "ask_user";

    public static IReadOnlyList<BuiltinToolDefinition> All { get; } =
    [
        new(
            AskUser,
            "Asks the user a question and returns their answer. Use it only when you cannot continue without information that only " +
            "the user has, such as a choice between options or a missing detail. Do not use it to confirm something you can decide yourself.",
            """
            {"type":"object","properties":{"question":{"type":"string","description":"The question for the user, short and self-contained."}},"required":["question"],"additionalProperties":false}
            """),
        new(
            CurrentDateTime,
            "Returns the current date and time. Use it whenever the answer depends on today's date or on the time of day, " +
            "because you cannot know them yourself.",
            """
            {"type":"object","properties":{"timezone":{"type":"string","description":"IANA time zone id such as Europe/Rome. Defaults to UTC."}},"additionalProperties":false}
            """),
        new(
            Calculator,
            "Evaluates an arithmetic expression exactly. Use it for any calculation instead of computing in your head. " +
            "Supports + - * / % ^, parentheses and the functions sqrt, pow, abs, round, min and max.",
            """
            {"type":"object","properties":{"expression":{"type":"string","description":"The expression to evaluate, e.g. (12.5 + 3) * 4"}},"required":["expression"],"additionalProperties":false}
            """),
    ];
}
