using Quorum.Modules.KnowledgeBases.Domain;

namespace Quorum.Modules.KnowledgeBases.Features.Lookups;

/// <summary>What a computer needs to compute one slice.</summary>
/// <param name="MaxValues">The ceiling of values; the computer may stop early and report <see cref="LookupComputation.Truncated"/>.</param>
internal sealed record LookupComputationRequest(Lookup Lookup, LookupSlice Slice, KnowledgeBase KnowledgeBase, int MaxValues);

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

/// <summary>Stands in until the store contract can compute lookups: every computation fails, visibly, and the last good list stays.</summary>
internal sealed class UnavailableLookupComputer : ILookupComputer
{
    public Task<LookupComputation> ComputeAsync(LookupComputationRequest request, CancellationToken cancellationToken) =>
        throw new LookupComputationException("This knowledge base cannot compute lookups yet.");
}
