using System.Text.Json.Nodes;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.KnowledgeBases.Mapping;
using Quorum.Modules.KnowledgeBases.Storage;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBases.Features.Verification;

/// <summary>What looking at a source found.</summary>
/// <param name="Info">The source, or <see langword="null"/> when it does not exist or the store could not be used.</param>
/// <param name="Sample">Rows of the source, empty unless sampling was asked for and the source has records.</param>
/// <param name="DimensionMatch"><see langword="null"/> when it cannot be told yet (no source, no embedding, no vector chosen).</param>
internal sealed record StoreProbeResult(
    bool Reachable,
    SourceInfo? Info,
    IReadOnlyList<JsonObject> Sample,
    int? EmbeddingSize,
    bool? DimensionMatch,
    IReadOnlyList<InspectWarning> Warnings);

/// <summary>
/// The checks inspection and verification share: what the adapter of the kind finds in the source, and whether an embedding of
/// the configured model fits the vector that will be searched (D-8). Every problem is a warning that says the cause and what
/// follows from it; none stops registration.
/// </summary>
internal sealed class StoreProbe(IEmbeddings embeddings)
{
    public async Task<StoreProbeResult> RunAsync(IStoreAdapter adapter, IStoreConnection connection, string embeddingModel, bool sample, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(connection);

        var source = await adapter.ProbeAsync(connection, sample, cancellationToken);
        var warnings = source.Warnings.ToList();

        var embeddingSize = await ProbeEmbeddingAsync(embeddingModel, warnings, cancellationToken);
        var dimensionMatch = CheckDimensions(adapter, connection, source.Info, embeddingSize, warnings);
        return new StoreProbeResult(source.Reachable, source.Info, source.Sample, embeddingSize, dimensionMatch, warnings);
    }

    private async Task<int?> ProbeEmbeddingAsync(string model, List<InspectWarning> warnings, CancellationToken cancellationToken)
    {
        // Inspection and verification promise an answer within the connection timeout, the embedding call included.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StoreLimits.RequestTimeout);

        Result<IReadOnlyList<float>> result;
        try
        {
            result = await embeddings.EmbedAsync("dimension probe", model, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = Error.Unprocessable("embeddings.unreachable", $"The model endpoint did not answer within {StoreLimits.RequestTimeout.TotalSeconds:0} seconds.");
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
    private static bool? CheckDimensions(IStoreAdapter adapter, IStoreConnection connection, SourceInfo? info, int? embeddingSize, List<InspectWarning> warnings)
    {
        if (info is null || info.Vectors.Count == 0)
        {
            return null;
        }

        var selection = adapter.SelectVector(info, connection);
        if (selection.Vector is not { } chosen)
        {
            warnings.Add(selection.Warning!);
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

        warnings.Add(adapter.DimensionMismatchWarning(chosen, embeddingSize.Value));
        return false;
    }
}
