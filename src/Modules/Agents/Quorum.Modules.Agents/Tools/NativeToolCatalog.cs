namespace Quorum.Modules.Agents.Tools;

/// <summary>The definition of a native tool as the model sees it. The name has no origin prefix.</summary>
internal sealed record NativeToolDefinition(string Name, string Description, string InputSchema);

/// <summary>
/// The tools Quorum runs in-process. A release that changes a definition updates the stored copy at startup
/// (<see cref="NativeToolProvisioner"/>); what the admin decided (state, policy) is kept.
/// </summary>
internal static class NativeToolCatalog
{
    public const string CurrentDateTime = "current_datetime";

    public const string Calculator = "calculator";

    public static IReadOnlyList<NativeToolDefinition> All { get; } =
    [
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
