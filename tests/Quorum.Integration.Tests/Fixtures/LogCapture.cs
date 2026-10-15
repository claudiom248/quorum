using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace Quorum.Integration.Tests.Fixtures;

public sealed class LogCapture : ILogEventSink
{
    private readonly ConcurrentQueue<(LogEventLevel Level, string Message)> _events = new();

    public IReadOnlyCollection<string> Messages => _events.Select(e => e.Message).ToList();

    public IReadOnlyCollection<string> ErrorsAndAbove =>
        _events.Where(e => e.Level >= LogEventLevel.Error).Select(e => e.Message).ToList();

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        _events.Enqueue((logEvent.Level, logEvent.RenderMessage(formatProvider: null)));
    }
}
