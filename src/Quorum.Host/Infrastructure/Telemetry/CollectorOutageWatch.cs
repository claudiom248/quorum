using System.Diagnostics.Tracing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Quorum.Host.Infrastructure.Telemetry;

/// <summary>
/// Says once that the export is failing (spec 016 D-19). The OTLP exporter reports each failed send on its own diagnostics
/// source; the first one is logged as one warning, and the next is allowed after a quiet period (the exporter says nothing when it
/// recovers, so the period is the only way to report a later outage). Nothing here
/// runs on a request: the events come from the exporter's background export.
/// </summary>
internal sealed partial class CollectorOutageWatch(ILogger<CollectorOutageWatch> logger, TimeProvider clock) : EventListener, IHostedService
{
    private const string ExporterSource = "OpenTelemetry-Exporter-OpenTelemetryProtocol";
    // The exporter names a failed send: a transient error (the endpoint is unreachable or answers with an error, and it retries) or,
    // when all retries are spent, a failure to reach the collector.
    private static readonly string[] UnreachableEvents = ["TransientHttpError", "FailedToReachCollector"];

    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private DateTimeOffset? _lastWarning;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == ExporterSource)
        {
            EnableEvents(source, EventLevel.Warning, EventKeywords.All);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (!UnreachableEvents.Contains(eventData.EventName))
        {
            return;
        }

        var now = clock.GetUtcNow();
        lock (_gate)
        {
            if (_lastWarning is { } last && now - last < QuietPeriod)
            {
                return;
            }

            _lastWarning = now;
        }

        LogUnreachable(logger);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telemetry export failed: the OTLP endpoint is unreachable or answering with errors; spans are dropped until it accepts them again")]
    private static partial void LogUnreachable(ILogger logger);
}
