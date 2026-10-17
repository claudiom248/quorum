using ModelContextProtocol.Client;
using Quorum.Modules.Agents.Secrets;

namespace Quorum.Modules.Agents.Tools.Mcp;

/// <summary>
/// The sessions with MCP servers that one answer opens, lazily and one per server, and closes when the answer ends. Nothing outlives
/// the answer, so there is no long-lived connection to repair, and a server is only contacted when the model calls one of its tools.
/// </summary>
internal sealed class McpAnswerConnections(McpServerClient client, McpHeadersProtector protector) : IAsyncDisposable
{
    private readonly Dictionary<Guid, McpClient> _sessions = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    /// <exception cref="McpServerException">The server cannot be reached, or its address is not allowed.</exception>
    public async Task<McpClient> GetAsync(McpServerEndpoint server, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sessions.TryGetValue(server.ServerId, out var open))
            {
                return open;
            }

            var session = await client.ConnectAsync(new Uri(server.Url), protector.RevealHeaders(server.ProtectedHeaders), cancellationToken);
            _sessions[server.ServerId] = session;
            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forgets a session that stopped working, so that the next call to the server opens a new one.</summary>
    public async Task DropAsync(Guid serverId)
    {
        McpClient? session;
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (!_sessions.Remove(serverId, out session))
            {
                return;
            }
        }
        finally
        {
            _gate.Release();
        }

        await CloseAsync(session);
    }

    public async ValueTask DisposeAsync()
    {
        List<McpClient> sessions;
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            sessions = [.. _sessions.Values];
            _sessions.Clear();
        }
        finally
        {
            _gate.Release();
        }

        foreach (var session in sessions)
        {
            await CloseAsync(session);
        }
    }

    private static async Task CloseAsync(McpClient session)
    {
        try
        {
            await session.DisposeAsync();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The session is being thrown away: a server that fails to say goodbye changes nothing for the answer.
        }
    }
}
