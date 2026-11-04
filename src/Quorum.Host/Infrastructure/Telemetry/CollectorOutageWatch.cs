using System.Diagnostics.Tracing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Quorum.Host.Infrastructure.Telemetry;

/// <summary>
/// Says once that the collector cannot be reached (spec 016 D-19). The OTLP exporter reports each failed send on its own diagnostics
/// source; the first one of an outage is logged as one warning, and the next warning is allowed after a quiet period. Nothing here
/// runs on a request: the events come from the exporter's background export.
/// </summary>
internal sealed partial class CollectorOutageWatch(ILogger<CollectorOutageWatch> logger, TimeProvider clock) : EventListener, IHostedService
{
    private const string ExporterSource = "OpenTelemetry-Exporter-OpenTelemetryProtocol";
    // The exporter names a failed send to an unreachable collector: a transient HTTP error (retries) or, when all retries are spent,
    // a failure to reach the collector.
    private static readonly string[] UnreachableEvents = ["TransientHttpError", "FailedToReachCollector"];

    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMinutes(10);

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telemetry export failed: the OTLP endpoint is unreachable; spans are dropped until it answers again")]
    private static partial void LogUnreachable(ILogger logger);
}
