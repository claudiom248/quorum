using System.Text.Json.Nodes;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.KnowledgeBases.Mapping;
using Quorum.Modules.KnowledgeBases.Storage.Qdrant;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBases.Features.Verification;

/// <summary>What looking at a collection found.</summary>
/// <param name="Info">The collection, or <see langword="null"/> when it does not exist or the store could not be used.</param>
/// <param name="Sample">Payloads of the first points, empty unless sampling was asked for and the collection has points.</param>
/// <param name="DimensionMatch"><see langword="null"/> when it cannot be told yet (no collection, no embedding, no vector chosen).</param>
internal sealed record StoreProbeResult(
    bool Reachable,
    QdrantCollectionInfo? Info,
    IReadOnlyList<JsonObject> Sample,
    int? EmbeddingSize,
    bool? DimensionMatch,
    IReadOnlyList<InspectWarning> Warnings);

/// <summary>
/// The checks inspection and verification share: is the store reachable, does the collection exist, does the server speak a
/// version that can search, and does an embedding of the configured model fit the vector that will be searched (D-8). Every
/// problem is a warning that says the cause and what follows from it; none stops registration.
/// </summary>
internal sealed class StoreProbe(QdrantConnector qdrant, IEmbeddings embeddings)
{
    public async Task<StoreProbeResult> RunAsync(QdrantConnection connection, string embeddingModel, bool sample, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var warnings = new List<InspectWarning>();
        QdrantCollectionInfo? info = null;
        Version? version = null;
        var reachable = false;
        IReadOnlyList<JsonObject> payloads = [];

        try
        {
            info = await qdrant.GetCollectionAsync(connection, cancellationToken);
            reachable = true;
            version = await qdrant.GetVersionAsync(connection, cancellationToken);

            if (info is null)
            {
                warnings.Add(new InspectWarning(
                    WarningCodes.CollectionNotFound,
                    "The collection does not exist on that server. It can still be registered, but RAG on it will fail until the collection is created.",
                    Path: null));
            }
            else if (sample && info.PointsCount != 0)
            {
                payloads = await qdrant.ScrollAsync(connection, PayloadSampler.SampleSize, cancellationToken);
            }
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

        var embeddingSize = await ProbeEmbeddingAsync(embeddingModel, warnings, cancellationToken);
        var dimensionMatch = CheckDimensions(info, connection.VectorName, embeddingSize, warnings);
        return new StoreProbeResult(reachable, info, payloads, embeddingSize, dimensionMatch, warnings);
    }

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
        // Inspection and verification promise an answer within the connection timeout, the embedding call included.
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
            $"{result.Error.Description} Without it the dimensions cannot be checked and RAG searches will fail.",
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
            $"The collection's vectors have {chosen.Size} dimensions but the embedding model produces {embeddingSize}. RAG searches would fail: ingestion and retrieval must use the same model.",
            Path: null));
        return false;
    }
}
