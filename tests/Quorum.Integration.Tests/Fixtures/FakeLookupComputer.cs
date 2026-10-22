using Quorum.Modules.KnowledgeBases.Features.Lookups;

namespace Quorum.Integration.Tests.Fixtures;

/// <summary>The computer of lookups that tests script: set <see cref="Handler"/> and count <see cref="Calls"/>.</summary>
internal sealed class FakeLookupComputer : ILookupComputer
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public Func<LookupComputationRequest, CancellationToken, Task<LookupComputation>> Handler { get; set; } =
        (_, _) => throw new LookupComputationException("The fake computer has no script.");

    public static LookupComputation Of(params (string Value, int Count)[] values) =>
        new([.. values.Select(v => new ComputedValue(v.Value, v.Count))]);

    public Task<LookupComputation> ComputeAsync(LookupComputationRequest request, CancellationToken cancellationToken)
    {
        _ = Interlocked.Increment(ref _calls);
        return Handler(request, cancellationToken);
    }
}
