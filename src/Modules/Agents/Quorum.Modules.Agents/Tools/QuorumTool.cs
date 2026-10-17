using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Quorum.Modules.Agents.Tools;

/// <summary>
/// A tool as the model is offered it: the exposed name, the description and the argument schema, all taken from the stored
/// definition. It is an <see cref="AIFunction"/> so that the request carries exactly what the budget counted, but it is run
/// through <see cref="RunAsync"/> by <see cref="ToolCallInvoker"/>, which has already validated, audited and limited the call.
/// </summary>
internal abstract class QuorumTool : AIFunction
{
    private readonly JsonElement _schema;

    protected QuorumTool(ActiveTool descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Descriptor = descriptor;
        _schema = JsonDocument.Parse(descriptor.InputSchema).RootElement.Clone();
    }

    public ActiveTool Descriptor { get; }

    public override string Name => Descriptor.ExposedName;

    public override string Description => Descriptor.Description;

    public override JsonElement JsonSchema => _schema;

    /// <returns>The result the model receives.</returns>
    /// <exception cref="ToolFailedException">The call cannot be done; the message goes to the model as the error.</exception>
    public abstract Task<string> RunAsync(JsonElement arguments, CancellationToken cancellationToken);

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return await RunAsync(JsonSerializer.SerializeToElement(arguments), cancellationToken);
    }
}

/// <summary>A tool Quorum runs in-process.</summary>
internal sealed class NativeQuorumTool(ActiveTool descriptor, INativeTool implementation) : QuorumTool(descriptor)
{
    public override Task<string> RunAsync(JsonElement arguments, CancellationToken cancellationToken) =>
        implementation.ExecuteAsync(arguments, cancellationToken);
}

/// <summary>Turns the active tools of an assistant into what the model is offered.</summary>
internal sealed class QuorumToolFactory(IEnumerable<INativeTool> nativeTools)
{
    private readonly Dictionary<string, INativeTool> _nativeTools = nativeTools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);

    /// <summary>A native tool without an implementation in this build cannot be called, so it is not offered.</summary>
    public IReadOnlyList<QuorumTool> Create(IEnumerable<ActiveTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return tools
            .Where(tool => tool.Origin == Domain.ToolOrigin.Native && _nativeTools.ContainsKey(tool.Name))
            .Select(tool => (QuorumTool)new NativeQuorumTool(tool, _nativeTools[tool.Name]))
            .ToList();
    }
}
