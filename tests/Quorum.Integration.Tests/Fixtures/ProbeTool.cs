using System.Collections.Concurrent;
using System.Text.Json;
using Quorum.Modules.Agents.Tools;

namespace Quorum.Integration.Tests.Fixtures;

/// <summary>
/// A native tool whose behaviour tests script: it records the arguments it receives and runs <see cref="Handler"/>.
/// Its row is created by <see cref="ToolFixtures.AddProbeAsync"/>.
/// </summary>
public sealed class ProbeTool : INativeTool
{
    public const string ToolName = "probe";

    private readonly ConcurrentQueue<string> _calls = new();

    public string Name => ToolName;

    /// <summary>The arguments of every call received, as JSON text, oldest first.</summary>
    public IReadOnlyList<string> Calls => _calls.ToList();

    public Func<JsonElement, CancellationToken, Task<string>> Handler { get; set; } = (_, _) => Task.FromResult("probe result");

    public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        _calls.Enqueue(arguments.GetRawText());
        return Handler(arguments, cancellationToken);
    }
}
