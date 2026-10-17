using System.Collections.Concurrent;
using System.Net;
using Quorum.Modules.Agents.Tools.Mcp;

namespace Quorum.Integration.Tests.Fixtures;

/// <summary>
/// Resolves host names for the MCP client. A name a test scripted gets the scripted answers in order (the last one repeats),
/// so a test can make a name change its address between the registration's check and the connection (DNS rebinding);
/// any other name resolves normally.
/// </summary>
public sealed class TestHostResolver : IMcpHostResolver
{
    private readonly ConcurrentDictionary<string, Queue<IPAddress[]>> _scripted = new(StringComparer.OrdinalIgnoreCase);

    public void Script(string host, params string[][] answers) =>
        _scripted[host] = new Queue<IPAddress[]>(answers.Select(a => a.Select(IPAddress.Parse).ToArray()));

    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (_scripted.TryGetValue(host, out var answers))
        {
            lock (answers)
            {
                return answers.Count > 1 ? answers.Dequeue() : answers.Peek();
            }
        }

        // Anything not scripted resolves as in production.
        return await new DnsMcpHostResolver().ResolveAsync(host, cancellationToken);
    }
}
