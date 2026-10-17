using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.KnowledgeBase.Domain;
using Quorum.Modules.KnowledgeBase.Features.Dictionary;
using Quorum.Modules.KnowledgeBase.Mapping;
using Quorum.Modules.KnowledgeBase.Persistence;
using Quorum.Modules.KnowledgeBase.Storage.Qdrant;
using Quorum.Shared.Networking;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBase.Features.Inspect;

/// <summary>
/// Looks at a collection before it is registered: is it reachable, what vectors does it have, how does its payload look, and does
/// an embedding of the configured model fit its vectors. Everything it finds is a draft or a warning; nothing is saved.
/// </summary>
internal sealed partial class KnowledgeBaseInspector(
    KnowledgeBaseDbContext db,
    QdrantConnector qdrant,
    OutboundUrlGuard guard,
    IEmbeddings embeddings,
    ILogger<KnowledgeBaseInspector> logger)
{
    public const string UrlBlockedCode = "knowledge_base.url_blocked";

    private const int MaxUrlLength = 2048;

    private const int MaxNameLength = 255;

    private static readonly Error UrlBlocked = Error.Unprocessable(
        UrlBlockedCode,
        "The address is not allowed: link-local and cloud metadata addresses are blocked.");

    public async Task<Result<InspectResponse>> InspectAsync(InspectRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var started = Stopwatch.GetTimestamp();
        var validated = Validate(request);
        if (!validated.IsSuccess)
        {
            return validated.Error;
        }

        var (connection, embeddingModel, schema) = validated.Value;
        if (await guard.IsBlockedAsync(connection.Url, cancellationToken))
        {
            return UrlBlocked;
        }

        var schemaDiscovery = schema is null ? null : JsonSchemaReader.Read(schema);
        if (schemaDiscovery is { IsSuccess: false })
        {
            return schemaDiscovery.Error;
        }

        var warnings = new List<InspectWarning>();
        QdrantCollectionInfo? info = null;
        Version? version = null;
        var reachable = false;
        var sampled = Discovery.Empty;

        try
        {
            info = await qdrant.GetCollectionAsync(connection, cancellationToken);
            reachable = true;
            version = await qdrant.GetVersionAsync(connection, cancellationToken);

            if (info is null)
            {
                warnings.Add(new InspectWarning(
                    WarningCodes.CollectionNotFound,
                    "The collection does not exist on that server. It can still be registered, but searching it will fail until it is created.",
                    Path: null));
            }
            else if (info.PointsCount == 0)
            {
                warnings.Add(new InspectWarning(
                    WarningCodes.EmptyCollection,
                    "The collection has no points yet, so there is nothing to sample: write the mapping by hand or paste the payload's JSON Schema.",
                    Path: null));
            }
            else if (schemaDiscovery is null)
            {
                sampled = PayloadSampler.Sample(await qdrant.ScrollAsync(connection, PayloadSampler.SampleSize, cancellationToken));
            }
        }
        catch (QdrantException exception) when (exception.Failure == QdrantFailure.Blocked)
        {
            return UrlBlocked;
        }
        catch (QdrantException exception)
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

        var discovery = schemaDiscovery is { IsSuccess: true } ? schemaDiscovery.Value : sampled;
        warnings.AddRange(discovery.Warnings);

        var embeddingSize = await ProbeEmbeddingAsync(embeddingModel, warnings, cancellationToken);
        var vectors = info?.Vectors.Select(v => new VectorDto(v.Name, v.Size, v.Distance)).ToList() ?? [];
        var dimensionMatch = CheckDimensions(info, connection.VectorName, embeddingSize, warnings);

        var draft = MappingConventions.Build(discovery.Properties);
        var visible = await DictionaryVisibility.Fields(db).AsNoTracking().ToListAsync(cancellationToken);
        var fields = draft.Fields.Select(field => Resolve(field, visible)).ToList();
        var proposed = fields
            .Where(f => f.DictionaryFieldId is null && f.Issues.Count == 0)
            .DistinctBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => new ProposedFieldDto(f.Name, f.Type))
            .ToList();

        var unindexed = new List<string>();
        if (info is not null)
        {
            foreach (var field in fields.Where(f => !info.PayloadIndexes.ContainsKey(f.Path)))
            {
                unindexed.Add(field.Path);
                warnings.Add(new InspectWarning(
                    WarningCodes.FieldUnindexed,
                    $"'{field.Path}' has no payload index in Qdrant: filters on it work but can be slow on large collections. Ask the data owner to index it if you plan to filter on it.",
                    field.Path));
            }
        }

        var response = new InspectResponse(
            reachable,
            reachable && info is not null,
            vectors,
            embeddingSize,
            dimensionMatch,
            new DraftMappingDto(
                new RolesDto(draft.Roles.Text, draft.Roles.DocumentId, draft.Roles.Title, draft.Roles.Position, draft.Roles.SourceUrl),
                fields),
            proposed,
            unindexed,
            warnings.Select(w => new WarningDto(w.Code, w.Message, w.Path)).ToList());

        LogInspected(logger, response.Reachable, response.CollectionExists, fields.Count, warnings.Count, (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return response;
    }

    private static Result<(QdrantConnection Connection, string EmbeddingModel, string? Schema)> Validate(InspectRequest request)
    {
        if (!DictionaryFieldRules.TryParseMember<KnowledgeBaseKind>(request.Kind, out _))
        {
            return Error.Validation("knowledge_base.unsupported_kind", "The kind must be Qdrant: it is the only storage kind supported.");
        }

        var connection = request.Connection;
        if (connection?.Url is null || !Uri.TryCreate(connection.Url.Trim(), UriKind.Absolute, out var url)
            || url.Scheme is not ("http" or "https") || connection.Url.Length > MaxUrlLength || !string.IsNullOrEmpty(url.UserInfo))
        {
            return Error.Validation(
                "knowledge_base.invalid_connection",
                "The URL must be an absolute http or https address of the Qdrant gRPC port (for example http://qdrant:6334), without credentials: the API key has its own field.");
        }

        var collection = connection.Collection?.Trim();
        if (string.IsNullOrEmpty(collection) || collection.Length > MaxNameLength)
        {
            return Error.Validation("knowledge_base.invalid_connection", "The collection name is required.");
        }

        var embeddingModel = request.EmbeddingModel?.Trim();
        if (string.IsNullOrEmpty(embeddingModel) || embeddingModel.Length > MaxNameLength)
        {
            return Error.Validation("knowledge_base.invalid_embedding_model", "The name of the embedding model is required.");
        }

        var vectorName = string.IsNullOrWhiteSpace(connection.VectorName) ? null : connection.VectorName.Trim();
        var apiKey = string.IsNullOrEmpty(connection.ApiKey) ? null : connection.ApiKey;
        if (apiKey is not null && !apiKey.All(character => character is >= ' ' and <= '~'))
        {
            // The key travels in a header, which carries ASCII only: a pasted typographic character would fail far from its cause.
            return Error.Validation("knowledge_base.invalid_connection", "The API key can only contain printable ASCII characters: check that nothing but the key was pasted.");
        }

        return (new QdrantConnection(url, collection, apiKey, vectorName), embeddingModel, SchemaText(request.Schema));
    }

    /// <summary>The schema as text, whether the client sent a string or the schema object itself; <see langword="null"/> when none was sent.</summary>
    private static string? SchemaText(JsonElement? schema) => schema switch
    {
        null => null,
        { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        { ValueKind: JsonValueKind.String } element => string.IsNullOrWhiteSpace(element.GetString()) ? null : element.GetString(),
        { } element => element.GetRawText(),
    };

    private static InspectWarning StoreWarning(QdrantException exception) => exception.Failure switch
    {
        QdrantFailure.Unauthorized => new InspectWarning(
            WarningCodes.QdrantUnauthorized,
            "The server refused the API key (or needs one). Until it is right, RAG on this collection will fail.",
            Path: null),
        QdrantFailure.Unreachable => new InspectWarning(
            WarningCodes.QdrantUnreachable,
            $"{exception.Message} Until the server is reachable, RAG on this collection will fail.",
            Path: null),
        _ => new InspectWarning(
            WarningCodes.QdrantUnexpected,
            $"{exception.Message} RAG on this collection may malfunction.",
            Path: null),
    };

    private async Task<int?> ProbeEmbeddingAsync(string model, List<InspectWarning> warnings, CancellationToken cancellationToken)
    {
        // Inspection promises an answer within the connection timeout, the embedding call included.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(QdrantConnector.RequestTimeout);

        Result<IReadOnlyList<float>> result;
        try
        {
            result = await embeddings.EmbedAsync("dimension probe", model, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = Error.Unprocessable("embeddings.unreachable", $"The model endpoint did not answer within {QdrantConnector.RequestTimeout.TotalSeconds:0} seconds.");
        }

        if (result.IsSuccess)
        {
            return result.Value.Count;
        }

        warnings.Add(new InspectWarning(
            WarningCodes.EmbeddingFailed,
            $"{result.Error.Description} Without it the dimensions cannot be checked and searches will fail.",
            Path: null));
        return null;
    }

    /// <summary>Whether the vector that will be searched has the size of the model's embeddings; <see langword="null"/> when that cannot be told yet.</summary>
    private static bool? CheckDimensions(QdrantCollectionInfo? info, string? vectorName, int? embeddingSize, List<InspectWarning> warnings)
    {
        if (info is null || info.Vectors.Count == 0)
        {
            return null;
        }

        QdrantVector chosen;
        if (vectorName is not null)
        {
            var named = info.Vectors.FirstOrDefault(v => v.Name == vectorName);
            if (named is null)
            {
                warnings.Add(new InspectWarning(
                    WarningCodes.VectorNotFound,
                    $"The collection has no vector named '{vectorName}'. Its vectors are: {string.Join(", ", info.Vectors.Select(v => $"'{v.Name}'"))}.",
                    Path: null));
                return null;
            }

            chosen = named;
        }
        else if (info.Vectors.Count == 1)
        {
            chosen = info.Vectors[0];
        }
        else
        {
            warnings.Add(new InspectWarning(
                WarningCodes.VectorNameRequired,
                $"The collection has several vectors ({string.Join(", ", info.Vectors.Select(v => $"'{v.Name}'"))}): choose the one to search before saving.",
                Path: null));
            return null;
        }

        if (embeddingSize is null)
        {
            return null;
        }

        if (chosen.Size == embeddingSize)
        {
            return true;
        }

        warnings.Add(new InspectWarning(
            WarningCodes.DimensionMismatch,
            $"The collection's vectors have {chosen.Size} dimensions but the embedding model produces {embeddingSize}. Searches would fail: ingestion and retrieval must use the same model.",
            Path: null));
        return false;
    }

    private static DraftFieldDto Resolve(DraftField field, List<DictionaryField> visible)
    {
        var existing = visible.FirstOrDefault(f => f.Name == field.Name);
        var issues = field.Issues.ToList();
        if (existing is not null && existing.Type != field.Type)
        {
            issues.Add("knowledge_base.field_type_conflict");
        }

        return new DraftFieldDto(field.Path, field.Name, field.Type.ToString(), existing?.Id, issues);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Inspected a knowledge base store: reachable {Reachable}, collection found {CollectionExists}, {FieldCount} fields, {WarningCount} warnings, {ElapsedMilliseconds} ms")]
    private static partial void LogInspected(ILogger logger, bool reachable, bool collectionExists, int fieldCount, int warningCount, int elapsedMilliseconds);
}
