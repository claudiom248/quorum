using System.Diagnostics;
using System.Globalization;
using OpenTelemetry;
using Quorum.Shared.Telemetry;

namespace Quorum.Host.Infrastructure.Telemetry;

/// <summary>
/// When content capture is on, the arguments and results of tool calls that the framework wrote on their spans go through the audit
/// redactor and are cut at the standard attribute limit (spec 016 D-2, AC-7). It runs when a span ends, before the batch exporter
/// takes it, so what leaves the process is always the redacted text.
/// </summary>
internal sealed class ContentGuardProcessor(int maxCharacters) : BaseProcessor<Activity>
{
    private const string ToolCallArguments = "gen_ai.tool.call.arguments";
    private const string ToolCallResult = "gen_ai.tool.call.result";

    public override void OnEnd(Activity activity)
    {
        if (!activity.OperationName.StartsWith("execute_tool", StringComparison.Ordinal))
        {
            return;
        }

        Guard(activity, ToolCallArguments, redactAsJson: true);
        Guard(activity, ToolCallResult, redactAsJson: false);
    }

    private void Guard(Activity activity, string key, bool redactAsJson)
    {
        if (activity.GetTagItem(key) is not string text)
        {
            return;
        }

        var value = redactAsJson ? AuditRedactor.Redact(text).Json : text;
        if (value.Length > maxCharacters)
        {
            value = value[..maxCharacters];
        }

        activity.SetTag(key, value);
    }

    /// <summary>The standard limit, <c>OTEL_ATTRIBUTE_VALUE_LENGTH_LIMIT</c>, or the SDK's default when it is not set or not a number.</summary>
    public static int LimitFrom(string? configured) =>
        int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) && limit > 0 ? limit : 4096;
}
