namespace Quorum.Shared.Telemetry;

/// <summary>
/// The telemetry settings of <c>Quorum:Telemetry</c>, read once at startup (spec 016 D-3). The standard <c>OTEL_*</c> variables
/// configure the export itself.
/// </summary>
public sealed class TelemetryOptions
{
    public const string SectionName = "Quorum:Telemetry";

    /// <summary>Prompts, answers, tool arguments and results are recorded in spans and logs (spec 016 D-2).</summary>
    public bool CaptureContent { get; set; }

    /// <summary>The administrative requests get a span of their own; the answer path is always traced when export is on (D-10).</summary>
    public bool AdminRequests { get; set; }

    /// <summary>An absolute http(s) URL with the placeholder <c>{traceId}</c>, used for the link on the audit page (D-21).</summary>
    public string? TraceUrlTemplate { get; set; }
}
