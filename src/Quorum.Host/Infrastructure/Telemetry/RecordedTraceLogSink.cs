using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace Quorum.Host.Infrastructure.Telemetry;

/// <summary>
/// Passes the log events to the OTLP sink with their trace and span ids only when the activity they were written in is recorded.
/// Serilog stamps the ids of the current activity on every event, also of a request that the span filter left out (spec 016 D-10):
/// that trace is never exported, and the link from Loki to it would end in "not found".
/// </summary>
internal sealed class RecordedTraceLogSink(ILogEventSink inner) : ILogEventSink, IDisposable
{
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        // The sink runs on the thread that logs, so the current activity is the one the event was written in.
        if (logEvent.TraceId is not null && Activity.Current is not { Recorded: true })
        {
            logEvent = new LogEvent(
                logEvent.Timestamp,
                logEvent.Level,
                logEvent.Exception,
                logEvent.MessageTemplate,
                logEvent.Properties.Select(property => new LogEventProperty(property.Key, property.Value)),
                default,
                default);
        }

        inner.Emit(logEvent);
    }

    public void Dispose() => (inner as IDisposable)?.Dispose();
}
