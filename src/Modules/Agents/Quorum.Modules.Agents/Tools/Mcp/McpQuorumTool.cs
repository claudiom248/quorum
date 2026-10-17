using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Quorum.Modules.Agents.Tools.Mcp;

/// <summary>
/// A tool of an MCP server. It goes through the same pipeline as a native tool (policy, confirmation, audit, timeouts, result cap):
/// this class only knows how to reach the server and how to read what it answers.
/// </summary>
internal sealed class McpQuorumTool(ActiveTool descriptor, McpAnswerConnections connections) : QuorumTool(descriptor)
{
    // A server decides how much an error says; the model gets a bounded part of it.
    private const int MaxErrorCharacters = 2000;

    public override async Task<string> RunAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var server = Descriptor.Server ?? throw new InvalidOperationException("An MCP tool has a server.");

        try
        {
            var session = await connections.GetAsync(server, cancellationToken);
            var result = await session.CallToolAsync(
                new CallToolRequestParams { Name = Descriptor.Name, Arguments = Arguments(arguments) },
                cancellationToken);
            return Read(result);
        }
        catch (McpServerException exception)
        {
            throw new ToolFailedException(exception.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The call timeout, the total time or the user cut the request short: the session may be left half-way, so the next call opens a new one.
            await connections.DropAsync(server.ServerId);
            throw;
        }
        catch (Exception exception) when (McpServerClient.IsServerFailure(exception, cancellationToken))
        {
            // After a failure of the transport the session may be dead; a protocol error leaves it usable.
            if (exception is not McpException)
            {
                await connections.DropAsync(server.ServerId);
            }

            var message = McpServerClient.ToServerException(exception).Message;
            throw new ToolFailedException(
                exception is McpException ? $"The server failed the call: {message}" : message);
        }
    }

    private static Dictionary<string, JsonElement>? Arguments(JsonElement arguments) =>
        arguments.ValueKind == JsonValueKind.Object
            ? arguments.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal)
            : null;

    /// <summary>The result as the text the model reads. Only text is passed on: other kinds of content are named, not forwarded.</summary>
    private static string Read(CallToolResult result)
    {
        var parts = result.Content.Select(block => block switch
        {
            TextContentBlock text => text.Text,
            ImageContentBlock image => $"[image content ({image.MimeType}) not shown]",
            AudioContentBlock audio => $"[audio content ({audio.MimeType}) not shown]",
            _ => "[content not shown]",
        }).ToList();

        if (!result.Content.OfType<TextContentBlock>().Any() && result.StructuredContent is { } structured)
        {
            parts = [structured.GetRawText()];
        }

        var text = string.Join('\n', parts);
        if (result.IsError == true)
        {
            throw new ToolFailedException(
                text.Length == 0 ? "The tool reported an error." : text.Length <= MaxErrorCharacters ? text : text[..MaxErrorCharacters]);
        }

        return text;
    }
}
