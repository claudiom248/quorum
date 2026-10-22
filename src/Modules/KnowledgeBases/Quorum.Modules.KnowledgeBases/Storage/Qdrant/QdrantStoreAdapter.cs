using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Mapping;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBases.Storage.Qdrant;

/// <summary>Where the administrator says a Qdrant knowledge base lives. <paramref name="VectorName"/> is only needed when the collection has several vectors.</summary>
internal sealed record QdrantConnectionRequest(string? Url, string? ApiKey, string? Collection, string? VectorName);

/// <summary>Where a Qdrant knowledge base lives, as it is stored. The key is the encrypted one: the plain key never reaches the database.</summary>
internal sealed record QdrantConnectionSettings(string Url, string Collection, string? VectorName, string? ProtectedApiKey);

/// <summary>
/// What is specific to Qdrant (spec 008 D-1): the shape of its connection and how it is checked, the API key and the rule that keeps
/// it, the wording of what is found in a collection, the vector name, and the mapping of its failures to the codes of spec 003.
/// </summary>
internal sealed class QdrantStoreAdapter(QdrantConnector connector, KnowledgeBaseSecretProtector protector, QdrantLookups lookups) : IStoreAdapter
{
    /// <summary>How many points discovery reads: enough to see the usual shape, few enough to be quick on any collection.</summary>
    public const int SampleSize = 20;

    private const int MaxUrlLength = 2048;

    private const int MaxNameLength = 255;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly Error ApiKeyRequired = Error.Validation(
        "knowledge_base.api_key_required",
        "The stored API key cannot be used here: enter the API key again. It is only reused for the server it was saved for, and it cannot be read if the installation's keys were replaced.");

    private static readonly Error ApiKeyUnreadable = Error.Unprocessable(
        "knowledge_base.api_key_unreadable",
        "The knowledge base's API key cannot be read. An administrator has to enter it again.");

    public KnowledgeBaseKind Kind => KnowledgeBaseKind.Qdrant;

    public Error SecretRequired => ApiKeyRequired;

    public Error SecretUnreadable => ApiKeyUnreadable;

    public Result<IStoreConnection> ReadConnection(JsonElement? connection)
    {
        QdrantConnectionRequest? request;
        try
        {
            request = connection is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } element
                ? element.Deserialize<QdrantConnectionRequest>(Web)
                : null;
        }
        catch (JsonException)
        {
            return Error.Validation("knowledge_base.invalid_connection", "The connection must be an object whose url, apiKey, collection and vectorName are text.");
        }

        if (request?.Url is null || !Uri.TryCreate(request.Url.Trim(), UriKind.Absolute, out var url)
            || url.Scheme is not ("http" or "https") || request.Url.Length > MaxUrlLength || !string.IsNullOrEmpty(url.UserInfo))
        {
            return Error.Validation(
                "knowledge_base.invalid_connection",
                "The URL must be an absolute http or https address of the Qdrant gRPC port (for example http://qdrant:6334), without credentials: the API key has its own field.");
        }

        var collection = request.Collection?.Trim();
        if (string.IsNullOrEmpty(collection) || collection.Length > MaxNameLength)
        {
            return Error.Validation("knowledge_base.invalid_connection", "The collection name is required.");
        }

        var vectorName = string.IsNullOrWhiteSpace(request.VectorName) ? null : request.VectorName.Trim();
        var apiKey = string.IsNullOrEmpty(request.ApiKey) ? null : request.ApiKey;
        if (apiKey is not null && !apiKey.All(character => character is >= ' ' and <= '~'))
        {
            // The key travels in a header, which carries ASCII only: a pasted typographic character would fail far from its cause.
            return Error.Validation("knowledge_base.invalid_connection", "The API key can only contain printable ASCII characters: check that nothing but the key was pasted.");
        }

        return new QdrantConnection(url, collection, apiKey, vectorName);
    }

    public Result<IStoreConnection> KeepSecrets(IStoreConnection connection, ConnectionSettings? stored)
    {
        var qdrant = Own(connection);

        // An omitted key keeps the stored one. The key is write-only, so it is only reused for the server it was given for: sent to
        // another address it could be read there.
        var plainKey = qdrant.ApiKey;
        if (plainKey is null && stored?.Read<QdrantConnectionSettings>() is { ProtectedApiKey: { } protectedKey } previous
            && (!SameServer(previous.Url, qdrant.Url) || !protector.TryUnprotect(protectedKey, out plainKey)))
        {
            return ApiKeyRequired;
        }

        return qdrant with { ApiKey = plainKey };
    }

    public ConnectionSettings ToSettings(IStoreConnection connection)
    {
        var qdrant = Own(connection);
        return ConnectionSettings.From(new QdrantConnectionSettings(qdrant.Url.AbsoluteUri, qdrant.Collection, qdrant.VectorName, protector.Protect(qdrant.ApiKey)));
    }

    public JsonObject Describe(ConnectionSettings settings)
    {
        var qdrant = settings.Read<QdrantConnectionSettings>();
        return new JsonObject
        {
            ["url"] = qdrant.Url,
            ["collection"] = qdrant.Collection,
            ["vectorName"] = qdrant.VectorName,
            ["hasApiKey"] = qdrant.ProtectedApiKey is not null,
        };
    }

    public bool TryOpen(ConnectionSettings settings, [NotNullWhen(true)] out IStoreConnection? connection)
    {
        var qdrant = settings.Read<QdrantConnectionSettings>();
        if (!protector.TryUnprotect(qdrant.ProtectedApiKey, out var apiKey))
        {
            connection = null;
            return false;
        }

        connection = new QdrantConnection(new Uri(qdrant.Url), qdrant.Collection, apiKey, qdrant.VectorName);
        return true;
    }

    public async Task<SourceProbe> ProbeAsync(IStoreConnection connection, bool sample, CancellationToken cancellationToken)
    {
        var qdrant = Own(connection);
        var warnings = new List<InspectWarning>();
        SourceInfo? info = null;
        Version? version = null;
        var reachable = false;
        IReadOnlyList<JsonObject> rows = [];

        try
        {
            info = await connector.GetCollectionAsync(qdrant, cancellationToken);
            reachable = true;
            version = await connector.GetVersionAsync(qdrant, cancellationToken);

            if (info is null)
            {
                warnings.Add(new InspectWarning(
                    WarningCodes.CollectionNotFound,
                    "The collection does not exist on that server. It can still be registered, but RAG on it will fail until the collection is created.",
                    Path: null));
            }
            else if (sample && info.RecordCount != 0)
            {
                rows = await connector.ScrollAsync(qdrant, SampleSize, cancellationToken);
            }
        }
        catch (StoreException exception)
        {
            warnings.Add(StoreWarning(exception));
        }

        if (version is not null && !QdrantConnector.IsSupported(version))
        {
            warnings.Add(new InspectWarning(
                WarningCodes.QdrantVersion,
                $"The server runs Qdrant {version}; searching needs {QdrantConnector.MinimumVersion} or later, so RAG on this collection will fail until it is upgraded.",
                Path: null));
        }

        List<InspectWarning> notes = [];
        if (version is not null && QdrantConnector.IsSupported(version) && !QdrantConnector.SupportsLookups(version))
        {
            notes.Add(new InspectWarning(
                WarningCodes.QdrantLookupsVersion,
                $"The server runs Qdrant {version}; lookups need {QdrantConnector.LookupsMinimumVersion} or later, so a field cannot be listed until it is upgraded. Searching is not affected.",
                Path: null));
        }

        return new SourceProbe(reachable, info, rows, warnings, notes);
    }

    public VectorSelection SelectVector(SourceInfo source, IStoreConnection connection)
    {
        var vectorName = Own(connection).VectorName;
        if (vectorName is not null)
        {
            var named = source.Vectors.FirstOrDefault(v => v.Name == vectorName);
            return named is not null
                ? new VectorSelection(named, null)
                : new VectorSelection(
                    null,
                    new InspectWarning(
                        WarningCodes.VectorNotFound,
                        $"The collection has no vector named '{vectorName}'. Its vectors are: {string.Join(", ", source.Vectors.Select(v => $"'{v.Name}'"))}.",
                        Path: null));
        }

        return source.Vectors.Count == 1
            ? new VectorSelection(source.Vectors[0], null)
            : new VectorSelection(
                null,
                new InspectWarning(
                    WarningCodes.VectorNameRequired,
                    $"The collection has several vectors ({string.Join(", ", source.Vectors.Select(v => $"'{v.Name}'"))}): choose the one to search before saving.",
                    Path: null));
    }

    /// <summary>The vector the mapping will search must exist: required when the collection has several, and it must be one of them.</summary>
    public Error? ValidateVector(SourceInfo? source, IStoreConnection connection)
    {
        var vectorName = Own(connection).VectorName;
        if (source is null || source.Vectors.Count == 0)
        {
            return null;
        }

        if (vectorName is null)
        {
            return source.Vectors.Count > 1
                ? Error.Validation(
                    WarningCodes.VectorNameRequired,
                    $"The collection has several vectors ({Names(source)}): choose the one to search.")
                : null;
        }

        return source.Vectors.Any(v => v.Name == vectorName)
            ? null
            : Error.Validation(WarningCodes.VectorNotFound, $"The collection has no vector named '{vectorName}'. Its vectors are: {Names(source)}.");
    }

    public InspectWarning EmptySourceWarning() => new(
        WarningCodes.EmptyCollection,
        "The collection has no points yet, so there is nothing to sample: write the mapping by hand or paste the payload's JSON Schema.",
        Path: null);

    public InspectWarning UnindexedPathWarning(string path) => new(
        WarningCodes.FieldUnindexed,
        $"'{path}' has no payload index in Qdrant: filters on it work but can be slow on large collections. Ask the data owner to index it if you plan to filter on it.",
        path);

    public InspectWarning DimensionMismatchWarning(VectorInfo vector, int embeddingSize) => new(
        WarningCodes.DimensionMismatch,
        $"The collection's vectors have {vector.Size} dimensions but the embedding model produces {embeddingSize}. RAG searches would fail: ingestion and retrieval must use the same model.",
        Path: null);

    public Task<IReadOnlyList<StoreHit>> SearchAsync(IStoreConnection connection, StoreQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return connector.QueryAsync(Own(connection), query.Vector, query.Conditions, query.Limit, query.ScoreThreshold, query.Paths, cancellationToken);
    }

    public T? Capability<T>()
        where T : class, IStoreCapability => lookups as T;

    public string CodeOf(StoreFailure failure) => failure switch
    {
        StoreFailure.Unreachable => WarningCodes.QdrantUnreachable,
        StoreFailure.Unauthorized => WarningCodes.QdrantUnauthorized,
        StoreFailure.NotFound => WarningCodes.CollectionNotFound,
        StoreFailure.Rejected => WarningCodes.DimensionMismatch,
        _ => WarningCodes.QdrantUnexpected,
    };

    private static InspectWarning StoreWarning(StoreException exception) => exception.Failure switch
    {
        StoreFailure.Unauthorized => new InspectWarning(
            WarningCodes.QdrantUnauthorized,
            "The server refused the API key (or needs one). Until it is right, RAG on this collection will fail.",
            Path: null),
        StoreFailure.Unreachable => new InspectWarning(
            WarningCodes.QdrantUnreachable,
            $"{exception.Message} Until the server is reachable, RAG on this collection will fail.",
            Path: null),
        _ => new InspectWarning(
            WarningCodes.QdrantUnexpected,
            $"{exception.Message} RAG on this collection may malfunction.",
            Path: null),
    };

    private static bool SameServer(string storedUrl, Uri url) =>
        Uri.TryCreate(storedUrl, UriKind.Absolute, out var stored)
        && string.Equals(stored.GetLeftPart(UriPartial.Authority), url.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

    private static string Names(SourceInfo source) => string.Join(", ", source.Vectors.Select(v => v.Name is null ? "(unnamed)" : $"'{v.Name}'"));

    private static QdrantConnection Own(IStoreConnection connection) =>
        connection as QdrantConnection ?? throw new ArgumentException($"A {connection.GetType().Name} is not a Qdrant connection.", nameof(connection));
}
