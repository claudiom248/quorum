using Quorum.Modules.KnowledgeBases.Domain;

namespace Quorum.Modules.KnowledgeBases.Features.Lookups;

/// <summary>What a computer needs to compute one slice.</summary>
/// <param name="MaxValues">The ceiling of values; the computer may stop early and report <see cref="LookupComputation.Truncated"/>.</param>
/// <param name="AttributeNames">Only entity lookups: the key of each attribute field in the stored JSON, by dictionary field id.</param>
internal sealed record LookupComputationRequest(
    Lookup Lookup,
    LookupSlice Slice,
    KnowledgeBase KnowledgeBase,
    int MaxValues,
    IReadOnlyDictionary<Guid, string>? AttributeNames = null);

/// <param name="Count"><see langword="null"/> for an entity.</param>
/// <param name="Attributes">Only entities: the chosen attributes as a JSON object.</param>
internal sealed record ComputedValue(string Value, int? Count = null, string? Attributes = null);

/// <param name="Values">A computed lookup is ranked by the refresher (count, then value); an entity lookup keeps the order given.</param>
/// <param name="Truncated">The computer stopped at the ceiling of values or at its own cap.</param>
internal sealed record LookupComputation(IReadOnlyList<ComputedValue> Values, bool Truncated = false);

/// <summary>
/// Computes the values of a slice from the knowledge base's store. The refresher owns time, storage and failure; the computer only
/// reads. The real one is built on the per-kind store contract (plan 007 PR 5).
/// </summary>
internal interface ILookupComputer
{
    /// <exception cref="LookupComputationException">The slice cannot be computed; the message is stored and shown to the administrator, so it names no value.</exception>
    Task<LookupComputation> ComputeAsync(LookupComputationRequest request, CancellationToken cancellationToken);
}

/// <summary>A computation that failed for a reason the administrator can act on (a missing index, an unreachable store).</summary>
internal sealed class LookupComputationException : Exception
{
    public LookupComputationException()
    {
    }

    public LookupComputationException(string message)
        : base(message)
    {
    }

    public LookupComputationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

