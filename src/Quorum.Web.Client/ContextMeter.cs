using System.Globalization;
using Quorum.Web.Client.Api;

namespace Quorum.Web.Client;

public enum MeterLevel
{
    Normal,
    Warning,
    Critical,
    Over,
}

/// <summary>The budget indicator of a conversation: the overall use of the model's context, not only the attachments'.</summary>
public sealed record ContextMeter(double Percent, MeterLevel Level, string Label, string Details)
{
    private const double WarningFrom = 75;
    private const double CriticalFrom = 90;

    public static ContextMeter From(ConversationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var used = context.UsableTokens - context.RemainingTokens;
        var percent = context.UsableTokens <= 0 ? 100 : Math.Clamp(used * 100.0 / context.UsableTokens, 0, 100);
        var level = context.RemainingTokens < 0
            ? MeterLevel.Over
            : percent >= CriticalFrom ? MeterLevel.Critical : percent >= WarningFrom ? MeterLevel.Warning : MeterLevel.Normal;

        var margin = context.RemainingTokens < 0
            ? $"over by {FormatTokens(-context.RemainingTokens)}"
            : $"{FormatTokens(context.RemainingTokens)} left";
        var details =
            $"System prompt {FormatTokens(context.SystemPromptTokens)} · attachments {FormatTokens(context.AttachmentsTokens)} · " +
            $"messages {FormatTokens(context.MessagesTokens)} · {margin}";

        return new ContextMeter(percent, level, $"{FormatTokens(Math.Max(0, used))} of {FormatTokens(context.UsableTokens)} tokens used", details);
    }

    public static string FormatTokens(int value) =>
        value < 1000 ? value.ToString(CultureInfo.InvariantCulture) : $"{(value / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)}k";
}
