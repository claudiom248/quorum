namespace Quorum.Integration.Tests.Fixtures;

/// <summary>
/// Holds the calls the application makes to Qdrant while a test says so, to cross a time limit with the app's fake clock: set
/// <see cref="Hold"/> and every call waits for it (or for its own cancellation).
/// </summary>
internal sealed class QdrantGate
{
    public Func<CancellationToken, Task>? Hold { get; set; }
}

/// <summary>The handler that applies <see cref="QdrantGate"/> to the HTTP client every Qdrant connection goes through.</summary>
internal sealed class QdrantGateHandler(QdrantGate gate) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (gate.Hold is { } hold)
        {
            await hold(cancellationToken);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
