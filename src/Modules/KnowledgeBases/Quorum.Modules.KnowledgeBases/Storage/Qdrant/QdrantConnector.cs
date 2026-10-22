using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using GrpcRange = Qdrant.Client.Grpc.Range;

namespace Quorum.Modules.KnowledgeBases.Storage.Qdrant;

/// <summary>Where a knowledge base lives in Qdrant. <see cref="ApiKey"/> is the plain key, only ever held for the length of a call.</summary>
internal sealed record QdrantConnection(Uri Url, string Collection, string? ApiKey, string? VectorName) : IStoreConnection
{
    // The generated ToString prints every member: a key must never reach a log or an exception message that way.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture, $"Collection = {Collection}");
        return true;
    }
}

/// <summary>
/// Quorum's access to Qdrant: the official client (gRPC) over a channel that goes through the named HTTP client (no redirects).
/// What is ours is the edge: the API key header, mapping failures to a few outcomes with messages
/// that are safe to show, and turning payloads into JSON for the mapping. Quorum only reads from the store.
/// </summary>
internal sealed class QdrantConnector(IHttpClientFactory httpClientFactory)
{
    public const string HttpClientName = "knowledge-base-qdrant";

    /// <summary>The longest one call may take while inspecting or verifying (non-functional requirements); searching has its own limit.</summary>
    public static readonly TimeSpan RequestTimeout = StoreLimits.RequestTimeout;

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
    public async Task<Version?> GetVersionAsync(QdrantConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        try
        {
            var reply = await RunAsync(connection, (client, token) => client.HealthAsync(token), cancellationToken);
            return Version.TryParse(reply.Version.Split('-', '+')[0], out var version) ? version : null;
        }
        catch (StoreException)
        {
            return null;
        }
    }

    /// <summary>The collection's vectors and payload indexes, or <see langword="null"/> when it does not exist.</summary>
    /// <exception cref="StoreException">The store cannot be used.</exception>
    public async Task<SourceInfo?> GetCollectionAsync(QdrantConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        try
        {
            var info = await RunAsync(connection, (client, token) => client.GetCollectionInfoAsync(connection.Collection, token), cancellationToken);
            var vectors = ReadVectors(info.Config?.Params?.VectorsConfig);
            var indexes = info.PayloadSchema.ToDictionary(entry => entry.Key, entry => entry.Value.DataType.ToString().ToLowerInvariant(), StringComparer.Ordinal);
            return new SourceInfo(vectors, indexes, info.HasPointsCount ? (long)info.PointsCount : null);
        }
        catch (StoreException exception) when (exception.Failure == StoreFailure.NotFound)
        {
            return null;
        }
    }

    /// <summary>The payloads of the first <paramref name="limit"/> points, without vectors.</summary>
    /// <exception cref="StoreException">The store cannot be used.</exception>
    public async Task<IReadOnlyList<JsonObject>> ScrollAsync(QdrantConnection connection, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var response = await RunAsync(
            connection,
            (client, token) => client.ScrollAsync(connection.Collection, limit: (uint)limit, payloadSelector: true, vectorsSelector: false, cancellationToken: token),
            cancellationToken);
        return response.Result.Select(point => ToJson(point.Payload)).ToList();
    }

    /// <summary>
    /// The points nearest to <paramref name="vector"/> that satisfy all <paramref name="conditions"/>, best first, with the payload
    /// at <paramref name="payloadPaths"/> and nothing else.
    /// </summary>
    /// <param name="scoreThreshold">Passed to the store, which knows whether a higher or a lower score is better for the collection's distance.</param>
    /// <exception cref="StoreException">The store cannot be used.</exception>
    public async Task<IReadOnlyList<StoreHit>> QueryAsync(
        QdrantConnection connection,
        float[] vector,
        IReadOnlyList<FilterCondition> conditions,
        int limit,
        double? scoreThreshold,
        IReadOnlyList<string> payloadPaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(vector);
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(payloadPaths);

        var filter = new Filter();
        filter.Must.AddRange(conditions.Select(ToCondition));
        var payload = new WithPayloadSelector { Include = new PayloadIncludeSelector { Fields = { payloadPaths } } };

        var points = await RunAsync(
            connection,
            (client, token) => client.QueryAsync(
                connection.Collection,
                query: vector,
                usingVector: connection.VectorName,
                filter: conditions.Count == 0 ? null : filter,
                scoreThreshold: scoreThreshold is { } threshold ? (float)threshold : null,
                limit: (ulong)limit,
                payloadSelector: payload,
                cancellationToken: token),
            cancellationToken);
        return points.Select(point => new StoreHit(point.Score, ToJson(point.Payload))).ToList();
    }

    private static Condition ToCondition(FilterCondition condition) => condition switch
    {
        MatchAnyCondition any => Conditions.Match(any.Path, any.Values),
        MatchBooleanCondition flag => Conditions.Match(flag.Path, flag.Value),
        NumberRangeCondition range => Conditions.Range(range.Path, ToRange(range)),
        DateRangeCondition range => Conditions.DatetimeRange(range.Path, gte: range.From, lte: range.To),
        DocumentIdsCondition documents => Conditions.Match(documents.Path, documents.Ids),
        _ => throw new ArgumentOutOfRangeException(nameof(condition), condition, "Unknown condition."),
    };

    // The bounds are optional fields of the message: a bound that is not set must stay unset, not become 0.
    private static GrpcRange ToRange(NumberRangeCondition condition)
    {
        var range = new GrpcRange();
        if (condition.Min is { } min)
        {
            range.Gte = min;
        }

        if (condition.Max is { } max)
        {
            range.Lte = max;
        }

        return range;
    }

    private static List<VectorInfo> ReadVectors(VectorsConfig? configuration)
    {
        if (configuration is null)
        {
            throw Unexpected();
        }

        return configuration.ConfigCase switch
        {
            VectorsConfig.ConfigOneofCase.Params => [new VectorInfo(null, (int)configuration.Params.Size, configuration.Params.Distance.ToString())],
            VectorsConfig.ConfigOneofCase.ParamsMap => configuration.ParamsMap.Map
                .Select(entry => new VectorInfo(entry.Key, (int)entry.Value.Size, entry.Value.Distance.ToString()))
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
            throw new StoreException(StoreFailure.Unreachable, $"The server did not answer within {RequestTimeout.TotalSeconds:0} seconds.");
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
        {
            // The caller gave up: the call is cancelled, not failed.
            throw new OperationCanceledException(cancellationToken);
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.Cancelled && timeout.IsCancellationRequested)
        {
            throw new StoreException(StoreFailure.Unreachable, $"The server did not answer within {RequestTimeout.TotalSeconds:0} seconds.");
        }
        catch (RpcException exception)
        {
            throw Translate(exception);
        }
    }

    private static StoreException Translate(RpcException exception)
    {
        return exception.StatusCode switch
        {
            StatusCode.Unauthenticated or StatusCode.PermissionDenied =>
                new StoreException(StoreFailure.Unauthorized, "The server refused the API key (or needs one)."),
            StatusCode.NotFound => new StoreException(StoreFailure.NotFound, "The collection was not found."),
            StatusCode.InvalidArgument => new StoreException(
                StoreFailure.Rejected,
                "The server rejected the query. Usually the collection's vectors have a different size than the embedding model's, or the vector name does not exist: ingestion and retrieval must use the same model."),
            StatusCode.DeadlineExceeded =>
                new StoreException(StoreFailure.Unreachable, $"The server did not answer within {RequestTimeout.TotalSeconds:0} seconds."),
            StatusCode.Unavailable => new StoreException(
                StoreFailure.Unreachable,
                "The server could not be reached over gRPC. Check the address: Qdrant's gRPC port is 6334 by default, not the REST port 6333."),
            _ => Unexpected(),
        };
    }

    private static StoreException Unexpected() => new(StoreFailure.Unexpected, "The server answered, but not as the Qdrant gRPC API does. Check that the address is Qdrant's gRPC port (6334 by default).");

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
