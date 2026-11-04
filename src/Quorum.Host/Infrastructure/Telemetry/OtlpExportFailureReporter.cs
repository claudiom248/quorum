using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;

namespace Quorum.Host.Infrastructure.Telemetry;

/// <summary>
/// Wraps the OTLP trace exporter to say once, when an outage starts, that the collector cannot be reached (spec 016 D-19).
/// The SDK does not turn each dropped batch into an event the app can count, so the wrapper watches the result of each export:
/// a failure after a success logs one warning, and the next success re-arms it. The batch processor exports on its own thread,
/// so none of this runs on a request.
/// </summary>
internal sealed class OtlpExportFailureReporter(OtlpTraceExporter inner, ILogger logger) : BaseExporter<Activity>
{
    private static readonly Action<ILogger, Exception?> ExportFailed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(1, nameof(ExportFailed)),
        "Telemetry export failed: the OTLP endpoint is unreachable; spans are dropped until it answers again");

    private int _failing;

    public override ExportResult Export(in Batch<Activity> batch)
    {
        var result = inner.Export(batch);
        if (result == ExportResult.Success)
        {
            Interlocked.Exchange(ref _failing, 0);
        }
        else if (Interlocked.Exchange(ref _failing, 1) == 0)
        {
            ExportFailed(logger, null);
        }

        return result;
    }

    protected override bool OnShutdown(int timeoutMilliseconds) => inner.Shutdown(timeoutMilliseconds);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
