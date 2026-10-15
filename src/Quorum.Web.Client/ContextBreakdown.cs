using Quorum.Web.Client.Api;

namespace Quorum.Web.Client;

public enum SegmentKind
{
    SystemPrompt,
    Attachments,
    Messages,
    Free,
}

/// <summary>One part of the context bar: what takes it, how many tokens, and its share of the bar.</summary>
public sealed record BreakdownSegment(SegmentKind Kind, string Label, int Tokens, double Percent);

/// <summary>An attachment's estimated share of the context.</summary>
public sealed record AttachmentShare(string FileName, string Kind, int Tokens);

/// <summary>The detail behind the budget indicator: where the context goes, and what each attachment costs.</summary>
public sealed record ContextBreakdown(
    IReadOnlyList<BreakdownSegment> Segments,
    IReadOnlyList<AttachmentShare> Files,
    string Summary,
    string Margin,
    bool IsOver,
    int UsableTokens,
    int BudgetTokens)
{
    private const int CharactersPerToken = 4;

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
        var margin = isOver
            ? $"over by {ContextMeter.FormatTokens(-context.RemainingTokens)}"
            : $"{ContextMeter.FormatTokens(context.RemainingTokens)} left";
        var summary = $"{ContextMeter.FormatTokens(used)} of {ContextMeter.FormatTokens(context.UsableTokens)} tokens";

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
            context.ContextTokenBudget);
    }
}
