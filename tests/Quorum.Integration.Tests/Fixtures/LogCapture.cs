using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace Quorum.Integration.Tests.Fixtures;

public sealed class LogCapture : ILogEventSink
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyCollection<string> Messages => _messages;

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        _messages.Enqueue(logEvent.RenderMessage(formatProvider: null));
    }
}
