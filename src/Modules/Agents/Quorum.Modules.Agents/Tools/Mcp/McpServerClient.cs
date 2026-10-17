using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Quorum.Modules.Agents.Tools.Mcp;

/// <summary>A tool as an MCP server describes it.</summary>
internal sealed record DiscoveredTool(string Name, string Description, string InputSchema, string? Annotations);

/// <summary>An MCP server could not be used: <see cref="Code"/> is the stable API code, the message is safe to show (it never carries header values).</summary>
internal sealed class McpServerException(string code, string message) : Exception(message)
{
    public const string Unreachable = "tool.server_unreachable";

    public const string UrlBlocked = "tool.url_blocked";

    public string Code { get; } = code;
}

/// <summary>The MCP client of Quorum: Streamable HTTP, tools only (D-29), through the guarded <c>mcp-server</c> HTTP client.</summary>
internal sealed class McpServerClient(IHttpClientFactory httpClientFactory, McpUrlGuard guard)
{
    public const string HttpClientName = "mcp-server";

    /// <summary>The longest a connection and a tool listing may take (non-functional requirements), apart from the per-call timeout.</summary>
    public static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(10);

    private const string BlockedMessage = "The address of the server is not allowed (link-local and cloud metadata addresses are blocked).";

    // A server that keeps listing tools forever must not fill the database.
    private const int MaxTools = 500;

    // Definitions are stored and sent to the model: bound what a server can make us keep.
    private const int MaxToolCharacters = 50_000;

    private const int MaxTotalCharacters = 1_000_000;

    private const int MaxErrorLength = 300;

    private static readonly Implementation ClientInfo = new() { Name = "Quorum", Version = "1.0" };

    /// <summary>Connects, lists every tool (all pages) and disconnects.</summary>
    /// <exception cref="McpServerException">The URL is blocked, or the server did not answer as an MCP server within the connection timeout.</exception>
    public async Task<IReadOnlyList<DiscoveredTool>> ListToolsAsync(
        Uri url,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        await EnsureAllowedAsync(url, cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectionTimeout);

        try
        {
            await using var client = await OpenAsync(url, headers, timeout.Token);

            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
            if (tools.Count > MaxTools)
            {
                throw new McpServerException(McpServerException.Unreachable, $"The server lists more than {MaxTools} tools.");
            }

            var described = tools.Select(Describe).ToList();
            if (described.Any(t => DefinitionSize(t) > MaxToolCharacters) || described.Sum(DefinitionSize) > MaxTotalCharacters)
            {
                throw new McpServerException(
                    McpServerException.Unreachable,
                    $"The tool definitions are too large (at most {MaxToolCharacters} characters per tool and {MaxTotalCharacters} in total).");
            }

            return described;
        }
        catch (Exception exception) when (IsServerFailure(exception, cancellationToken))
        {
            throw ToServerException(exception);
        }
    }

    /// <summary>
    /// Opens a session with the server for the calls of one answer; the caller disposes it. Nothing here is cached: a session lives
    /// as long as the answer that asked for it.
    /// </summary>
    /// <exception cref="McpServerException">The URL is blocked, or the server did not answer as an MCP server within the connection timeout.</exception>
    public async Task<McpClient> ConnectAsync(Uri url, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        await EnsureAllowedAsync(url, cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectionTimeout);

        try
        {
            return await OpenAsync(url, headers, timeout.Token);
        }
        catch (Exception exception) when (IsServerFailure(exception, cancellationToken))
        {
            throw ToServerException(exception);
        }
    }

    /// <summary>Whether a failure while talking to a server is the server's (or the network's) rather than ours or the caller's cancellation.</summary>
    internal static bool IsServerFailure(Exception exception, CancellationToken callerToken) =>
        exception is OperationCanceledException
            ? !callerToken.IsCancellationRequested
            : IsBlocked(exception) || exception is McpException or HttpRequestException or IOException or JsonException or InvalidOperationException;

    /// <summary>The error to show for a failure that <see cref="IsServerFailure"/> accepted. It never carries header values.</summary>
    internal static McpServerException ToServerException(Exception exception) => exception switch
    {
        OperationCanceledException => new McpServerException(McpServerException.Unreachable, $"The server did not answer within {ConnectionTimeout.TotalSeconds:0} seconds."),
        _ when IsBlocked(exception) => new McpServerException(McpServerException.UrlBlocked, BlockedMessage),
        _ => new McpServerException(McpServerException.Unreachable, Explain(exception)),
    };

    private async Task EnsureAllowedAsync(Uri url, CancellationToken cancellationToken)
    {
        if (await guard.IsBlockedAsync(url, cancellationToken))
        {
            throw new McpServerException(McpServerException.UrlBlocked, BlockedMessage);
        }
    }

    private Task<McpClient> OpenAsync(Uri url, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken) =>
        McpClient.CreateAsync(
            CreateTransport(url, headers),
            new McpClientOptions { ClientInfo = ClientInfo, Capabilities = new ClientCapabilities() },
            cancellationToken: cancellationToken);

    /// <summary>The HTTP transport every connection to a server uses (also by the tool invoker): the guarded client, static headers and no redirects.</summary>
    internal HttpClientTransport CreateTransport(Uri url, IReadOnlyDictionary<string, string>? headers) => new(
        new HttpClientTransportOptions
        {
            Endpoint = url,
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = headers is null ? null : new Dictionary<string, string>(headers),
            ConnectionTimeout = ConnectionTimeout,
        },
        httpClientFactory.CreateClient(HttpClientName),
        ownsHttpClient: false);

    private static int DefinitionSize(DiscoveredTool tool) => tool.Name.Length + tool.Description.Length + tool.InputSchema.Length;

    private static DiscoveredTool Describe(McpClientTool tool) => new(
        tool.Name,
        tool.Description ?? string.Empty,
        tool.JsonSchema.ValueKind == JsonValueKind.Object ? tool.JsonSchema.GetRawText() : """{"type":"object"}""",
        Annotations(tool.ProtocolTool.Annotations));

    private static string? Annotations(ToolAnnotations? annotations)
    {
        if (annotations is null)
        {
            return null;
        }

        var result = new JsonObject();
        if (annotations.Title is { } title)
        {
            result["title"] = title;
        }

        if (annotations.ReadOnlyHint is { } readOnly)
        {
            result["readOnlyHint"] = readOnly;
        }

        if (annotations.DestructiveHint is { } destructive)
        {
            result["destructiveHint"] = destructive;
        }

        if (annotations.IdempotentHint is { } idempotent)
        {
            result["idempotentHint"] = idempotent;
        }

        if (annotations.OpenWorldHint is { } openWorld)
        {
            result["openWorldHint"] = openWorld;
        }

        return result.Count == 0 ? null : result.ToJsonString();
    }

    private static bool IsBlocked(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is McpUrlBlockedException)
            {
                return true;
            }
        }

        return false;
    }

    private static string Explain(Exception exception)
    {
        var message = exception switch
        {
            HttpRequestException { StatusCode: { } status } => $"The server answered {(int)status} ({status}).",
            HttpRequestException { InnerException: { } inner } => $"Could not connect: {inner.Message}",
            _ => exception.Message,
        };

        return message.Length <= MaxErrorLength ? message : message[..MaxErrorLength];
    }
}
