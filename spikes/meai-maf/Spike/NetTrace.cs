using System.Diagnostics.Tracing;

/// <summary>
/// In-process proof of "no outbound traffic": records every DNS resolution, socket connect and HTTP request the runtime reports
/// while the experiments run (System.Net.* EventSources). Enabled with SPIKE_NETTRACE=1.
/// </summary>
sealed class NetTrace : EventListener
{
    private readonly SortedSet<string> _targets = [];

    public static NetTrace? StartIfRequested() => Environment.GetEnvironmentVariable("SPIKE_NETTRACE") == "1" ? new NetTrace() : null;

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name is "System.Net.Http" or "System.Net.NameResolution" or "System.Net.Sockets")
        {
            EnableEvents(source, EventLevel.Informational, EventKeywords.All);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        var name = e.EventName ?? "";
        if (name is "RequestStart" or "ResolutionStart" or "ConnectStart" or "ConnectedAsync")
        {
            lock (_targets)
            {
                _targets.Add($"{e.EventSource.Name}/{name}: {string.Join(" ", e.Payload?.Select(p => p?.ToString()) ?? [])}");
            }
        }
    }

    public void Report()
    {
        lock (_targets)
        {
            Console.WriteLine($"[nettrace] {_targets.Count} distinct network events:");
            foreach (var t in _targets) Console.WriteLine("[nettrace]   " + t);
        }
    }
}
