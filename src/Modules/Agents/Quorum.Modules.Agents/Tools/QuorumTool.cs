using System.Text.Json;
using Microsoft.Extensions.AI;
using Quorum.Modules.Agents.Secrets;
using Quorum.Modules.Agents.Tools.Mcp;

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

/// <summary>
/// The tool that asks the user a question. It runs nothing: <see cref="ToolCallInvoker"/> pauses the answer for the user's reply,
/// because only it can publish the question and wait for the reply outside the call timeout.
/// </summary>
internal sealed class AskUserQuorumTool(ActiveTool descriptor) : QuorumTool(descriptor)
{
    public override Task<string> RunAsync(JsonElement arguments, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The question to the user is asked by the invoker, not run as a tool.");
}

/// <summary>Turns the active tools of an assistant into what the model is offered.</summary>
internal sealed class QuorumToolFactory(IEnumerable<INativeTool> nativeTools, McpServerClient mcpClient, McpHeadersProtector headersProtector)
{
    private readonly Dictionary<string, INativeTool> _nativeTools = nativeTools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);

    /// <summary>The sessions with MCP servers of one answer; the answer disposes it when it ends.</summary>
    public McpAnswerConnections NewConnections() => new(mcpClient, headersProtector);

    /// <summary>A native tool without an implementation in this build cannot be called, so it is not offered.</summary>
    public IReadOnlyList<QuorumTool> Create(IEnumerable<ActiveTool> tools, McpAnswerConnections? connections)
    {
        ArgumentNullException.ThrowIfNull(tools);

        return tools
            .Select(tool => tool.Origin switch
            {
                Domain.ToolOrigin.Mcp when tool.Server is not null && connections is not null => new McpQuorumTool(tool, connections),
                Domain.ToolOrigin.Native when tool.Name == NativeToolCatalog.AskUser => new AskUserQuorumTool(tool),
                Domain.ToolOrigin.Native when _nativeTools.TryGetValue(tool.Name, out var implementation) => new NativeQuorumTool(tool, implementation),
                _ => (QuorumTool?)null,
            })
            .OfType<QuorumTool>()
            .ToList();
    }
}
