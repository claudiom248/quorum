using System.Globalization;
using Quorum.Web.Client.Api;

namespace Quorum.Web.Client;

public enum SegmentKind
{
    SystemPrompt,
    Attachments,
    Messages,
    Free,
}

public enum MeterLevel
{
    Normal,
    Warning,
    Critical,
    Over,
}

/// <summary>One part of the context bar: what takes it, how many tokens, and its share of the bar.</summary>
public sealed record BreakdownSegment(SegmentKind Kind, string Label, int Tokens, double Percent);

/// <summary>An attachment's estimated share of the context.</summary>
public sealed record AttachmentShare(string FileName, string Kind, int Tokens);

/// <summary>
/// The budget indicator of a conversation and the detail behind it: the overall use of the model's context (system prompt,
/// attachments and messages), where it goes, and what each attachment costs.
/// </summary>
public sealed record ContextBreakdown(
    IReadOnlyList<BreakdownSegment> Segments,
    IReadOnlyList<AttachmentShare> Files,
    string Summary,
    string Margin,
    bool IsOver,
    int UsableTokens,
    int BudgetTokens,
    double Percent,
    MeterLevel Level)
{
    private const int CharactersPerToken = 4;
    private const double WarningFrom = 75;
    private const double CriticalFrom = 90;

    /// <summary>How many ticks the gauge has; each one stands for a tenth of the context.</summary>
    public const int GaugeTicks = 10;

    /// <summary>The gauge ticks to light: a tick is lit as soon as its tenth is entered, so any use shows.</summary>
    public int LitTicks => (int)Math.Ceiling(Percent / 100 * GaugeTicks);

    public static ContextBreakdown From(ConversationContext context, IReadOnlyList<AttachmentInfo> attachments)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(attachments);

        var used = context.SystemPromptTokens + context.AttachmentsTokens + context.MessagesTokens;
        var free = Math.Max(0, context.RemainingTokens);

        // When the context overflows the bar is the whole of what is used; otherwise it is the usable context.
        var bar = Math.Max(1, Math.Max(context.UsableTokens, used));
        BreakdownSegment Segment(SegmentKind kind, string label, int tokens) => new(kind, label, tokens, tokens * 100.0 / bar);

        var isOver = context.RemainingTokens < 0;
        var percent = context.UsableTokens <= 0 ? 100 : Math.Clamp(used * 100.0 / context.UsableTokens, 0, 100);
        var level = isOver
            ? MeterLevel.Over
            : percent >= CriticalFrom ? MeterLevel.Critical : percent >= WarningFrom ? MeterLevel.Warning : MeterLevel.Normal;

        var margin = isOver
            ? $"over by {FormatTokens(-context.RemainingTokens)}"
            : $"{FormatTokens(context.RemainingTokens)} left";
        var summary = $"{FormatTokens(used)} of {FormatTokens(context.UsableTokens)} tokens";

        return new ContextBreakdown(
            [
                Segment(SegmentKind.SystemPrompt, "System prompt", context.SystemPromptTokens),
                Segment(SegmentKind.Attachments, "Attachments", context.AttachmentsTokens),
                Segment(SegmentKind.Messages, "Messages", context.MessagesTokens),
                Segment(SegmentKind.Free, "Free", free),
            ],
            attachments.Select(a => new AttachmentShare(a.FileName, AttachmentRules.Kind(a.FileName), a.CharacterCount / CharactersPerToken)).ToList(),
            isOver ? $"{summary} · {margin}" : summary,
            margin,
            isOver,
            context.UsableTokens,
            context.ContextTokenBudget,
            percent,
            level);
    }

    /// <summary>Token counts as the UI shows them: "850", "2.1k".</summary>
    public static string FormatTokens(int value) =>
        value < 1000 ? value.ToString(CultureInfo.InvariantCulture) : $"{(value / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)}k";
}
