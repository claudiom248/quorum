using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Quorum.Shared.Networking;

namespace Quorum.Modules.KnowledgeBase.Storage.Qdrant;

/// <summary>Where a knowledge base lives in Qdrant. <see cref="ApiKey"/> is the plain key, only ever held for the length of a call.</summary>
internal sealed record QdrantConnection(Uri Url, string Collection, string? ApiKey, string? VectorName)
{
    // The generated ToString prints every member: a key must never reach a log or an exception message that way.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture, $"Collection = {Collection}");
        return true;
    }
}

internal enum QdrantFailure
{
    /// <summary>The address is one an outbound connection must never be made to.</summary>
    Blocked,

    /// <summary>No connection, or no answer in time.</summary>
    Unreachable,

    /// <summary>The server refused the API key (or the lack of one).</summary>
    Unauthorized,

    /// <summary>The collection does not exist.</summary>
    NotFound,

    /// <summary>The server answered, but not as Qdrant does.</summary>
    Unexpected,
}

/// <summary>A Qdrant call failed. The message is safe to show: it never carries the key or the address.</summary>
internal sealed class QdrantException(QdrantFailure failure, string message) : Exception(message)
{
    public QdrantFailure Failure { get; } = failure;
}

/// <param name="Name">The vector's name, or <see langword="null"/> for the collection's single unnamed vector.</param>
internal sealed record QdrantVector(string? Name, int Size, string Distance);

/// <param name="PayloadIndexes">Payload path → Qdrant's data type, for the paths that have an index.</param>
/// <param name="PointsCount">How many points the collection holds, when the server says.</param>
internal sealed record QdrantCollectionInfo(IReadOnlyList<QdrantVector> Vectors, IReadOnlyDictionary<string, string> PayloadIndexes, long? PointsCount);

/// <summary>
/// Quorum's access to Qdrant: the official client (gRPC) over a channel that goes through the guarded HTTP client, so the URL policy
/// holds for every connection. What is ours is the edge: the API key header, mapping failures to a few outcomes with messages
/// that are safe to show, and turning payloads into JSON for the mapping. Quorum only reads from the store.
/// </summary>
internal sealed class QdrantConnector(IHttpClientFactory httpClientFactory, OutboundUrlGuard guard)
{
    public const string HttpClientName = "knowledge-base-qdrant";

    /// <summary>The longest one call may take while inspecting or verifying (non-functional requirements); searching has its own limit.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The first version with the query API, which search uses.</summary>
    public static readonly Version MinimumVersion = new(1, 10);

    private const string ApiKeyHeader = "api-key";

    // 20 points of ordinary chunks are a few hundred KB; this only stops a server that sends without end.
    private const int MaxResponseBytes = 8 * 1024 * 1024;

    public static bool IsSupported(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version >= MinimumVersion;
    }

    /// <summary>The server's version, or <see langword="null"/> when it does not say.</summary>
    /// <exception cref="QdrantException">The address is blocked.</exception>
    public async Task<Version?> GetVersionAsync(QdrantConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        try
        {
            var reply = await RunAsync(connection, (client, token) => client.HealthAsync(token), cancellationToken);
            return Version.TryParse(reply.Version.Split('-', '+')[0], out var version) ? version : null;
        }
        catch (QdrantException exception) when (exception.Failure != QdrantFailure.Blocked)
        {
            return null;
        }
    }

    /// <summary>The collection's vectors and payload indexes, or <see langword="null"/> when it does not exist.</summary>
    /// <exception cref="QdrantException">The store cannot be used.</exception>
    public async Task<QdrantCollectionInfo?> GetCollectionAsync(QdrantConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        try
        {
            var info = await RunAsync(connection, (client, token) => client.GetCollectionInfoAsync(connection.Collection, token), cancellationToken);
            var vectors = ReadVectors(info.Config?.Params?.VectorsConfig);
            var indexes = info.PayloadSchema.ToDictionary(entry => entry.Key, entry => entry.Value.DataType.ToString().ToLowerInvariant(), StringComparer.Ordinal);
            return new QdrantCollectionInfo(vectors, indexes, info.HasPointsCount ? (long)info.PointsCount : null);
        }
        catch (QdrantException exception) when (exception.Failure == QdrantFailure.NotFound)
        {
            return null;
        }
    }

    /// <summary>The payloads of the first <paramref name="limit"/> points, without vectors.</summary>
    /// <exception cref="QdrantException">The store cannot be used.</exception>
    public async Task<IReadOnlyList<JsonObject>> ScrollAsync(QdrantConnection connection, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var response = await RunAsync(
            connection,
            (client, token) => client.ScrollAsync(connection.Collection, limit: (uint)limit, payloadSelector: true, vectorsSelector: false, cancellationToken: token),
            cancellationToken);
        return response.Result.Select(point => ToJson(point.Payload)).ToList();
    }

    private static List<QdrantVector> ReadVectors(VectorsConfig? configuration)
    {
        if (configuration is null)
        {
            throw Unexpected();
        }

        return configuration.ConfigCase switch
        {
            VectorsConfig.ConfigOneofCase.Params => [new QdrantVector(null, (int)configuration.Params.Size, configuration.Params.Distance.ToString())],
            VectorsConfig.ConfigOneofCase.ParamsMap => configuration.ParamsMap.Map
                .Select(entry => new QdrantVector(entry.Key, (int)entry.Value.Size, entry.Value.Distance.ToString()))
                .ToList(),
            _ => throw Unexpected(),
        };
    }

    /// <summary>A payload as JSON, the shape the mapping works on: nested objects stay objects, lists stay lists.</summary>
    private static JsonObject ToJson(IEnumerable<KeyValuePair<string, Value>> payload)
    {
        var result = new JsonObject();
        // The server keeps payload keys in no particular order; sorted, a draft is the same every time.
        foreach (var (key, value) in payload.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            result[key] = ToJson(value);
        }

        return result;
    }

    private static JsonNode? ToJson(Value value) => value.KindCase switch
    {
        Value.KindOneofCase.StringValue => JsonValue.Create(value.StringValue),
        Value.KindOneofCase.IntegerValue => JsonValue.Create(value.IntegerValue),
        Value.KindOneofCase.DoubleValue => JsonValue.Create(value.DoubleValue),
        Value.KindOneofCase.BoolValue => JsonValue.Create(value.BoolValue),
        Value.KindOneofCase.StructValue => ToJson(value.StructValue.Fields),
        Value.KindOneofCase.ListValue => new JsonArray(value.ListValue.Values.Select(ToJson).ToArray()),
        _ => null,
    };

    private async Task<T> RunAsync<T>(QdrantConnection connection, Func<QdrantClient, CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        // Up front, so a blocked address is a clear answer; the connection checks again for the address it really opens.
        if (await guard.IsBlockedAsync(connection.Url, cancellationToken))
        {
            throw Blocked();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        using var channel = GrpcChannel.ForAddress(
            connection.Url,
            new GrpcChannelOptions
            {
                HttpClient = httpClientFactory.CreateClient(HttpClientName),
                MaxReceiveMessageSize = MaxResponseBytes,
            });
        var invoker = string.IsNullOrEmpty(connection.ApiKey) ? channel.CreateCallInvoker() : channel.Intercept(new ApiKeyInterceptor(connection.ApiKey));
        using var client = new QdrantClient(new QdrantGrpcClient(invoker), RequestTimeout);

        try
        {
            return await call(client, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new QdrantException(QdrantFailure.Unreachable, $"The server did not answer within {RequestTimeout.TotalSeconds:0} seconds.");
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
        {
            // The caller gave up: the call is cancelled, not failed.
            throw new OperationCanceledException(cancellationToken);
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.Cancelled && timeout.IsCancellationRequested)
        {
            throw new QdrantException(QdrantFailure.Unreachable, $"The server did not answer within {RequestTimeout.TotalSeconds:0} seconds.");
        }
        catch (RpcException exception)
        {
            throw Translate(exception);
        }
    }

    private static QdrantException Translate(RpcException exception)
    {
        if (IsBlocked(exception))
        {
            return Blocked();
        }

        return exception.StatusCode switch
        {
            StatusCode.Unauthenticated or StatusCode.PermissionDenied =>
                new QdrantException(QdrantFailure.Unauthorized, "The server refused the API key (or needs one)."),
            StatusCode.NotFound => new QdrantException(QdrantFailure.NotFound, "The collection was not found."),
            StatusCode.DeadlineExceeded =>
                new QdrantException(QdrantFailure.Unreachable, $"The server did not answer within {RequestTimeout.TotalSeconds:0} seconds."),
            StatusCode.Unavailable => new QdrantException(
                QdrantFailure.Unreachable,
                "The server could not be reached over gRPC. Check the address: Qdrant's gRPC port is 6334 by default, not the REST port 6333."),
            _ => Unexpected(),
        };
    }

    private static QdrantException Blocked() =>
        new(QdrantFailure.Blocked, "The address is not allowed (link-local and cloud metadata addresses are blocked).");

    private static QdrantException Unexpected() => new(QdrantFailure.Unexpected, "The server answered, but not as the Qdrant gRPC API does. Check that the address is Qdrant's gRPC port (6334 by default).");

    private static bool IsBlocked(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is OutboundUrlBlockedException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Adds the API key to every call: the official client has no hook to give it with a channel of our own.</summary>
    private sealed class ApiKeyInterceptor(string apiKey) : Interceptor
    {
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            TRequest request,
            ClientInterceptorContext<TRequest, TResponse> context,
            AsyncUnaryCallContinuation<TRequest, TResponse> continuation) =>
            continuation(request, WithKey(context));

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            TRequest request,
            ClientInterceptorContext<TRequest, TResponse> context,
            BlockingUnaryCallContinuation<TRequest, TResponse> continuation) =>
            continuation(request, WithKey(context));

        private ClientInterceptorContext<TRequest, TResponse> WithKey<TRequest, TResponse>(ClientInterceptorContext<TRequest, TResponse> context)
            where TRequest : class
            where TResponse : class
        {
            var headers = context.Options.Headers ?? [];
            headers.Add(ApiKeyHeader, apiKey);
            return new ClientInterceptorContext<TRequest, TResponse>(context.Method, context.Host, context.Options.WithHeaders(headers));
        }
    }
}
