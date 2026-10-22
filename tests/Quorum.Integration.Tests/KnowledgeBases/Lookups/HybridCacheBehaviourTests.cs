using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace Quorum.Integration.Tests.KnowledgeBases.Lookups;

/// <summary>
/// The behaviours of HybridCache that the lookup cache relies on (plan 007 step 6), checked against the pinned version so that an
/// upgrade which changes one of them fails here and not in production.
/// </summary>
public sealed class HybridCacheBehaviourTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Value = ["a"];

    private static ServiceProvider Build(Action<HybridCacheOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddHybridCache(options =>
        {
            options.MaximumPayloadBytes = 1024 * 1024;
            configure?.Invoke(options);
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task ConcurrentMissesRunTheFactoryOnce()
    {
        await using var provider = Build();
        var cache = provider.GetRequiredService<HybridCache>();
        var calls = 0;

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => cache.GetOrCreateAsync(
            "k",
            async _ =>
            {
                Interlocked.Increment(ref calls);
                await Task.Delay(50, Ct);
                return Value;
            },
            cancellationToken: Ct).AsTask()));

        Assert.Equal(1, calls);
        Assert.All(results, r => Assert.Equal(["a"], r));
    }

    [Fact]
    public async Task RemovingATagDropsEveryEntryCarryingIt()
    {
        await using var provider = Build();
        var cache = provider.GetRequiredService<HybridCache>();
        var calls = 0;
        ValueTask<int> Create(CancellationToken _) => ValueTask.FromResult(Interlocked.Increment(ref calls));

        await cache.GetOrCreateAsync("one", Create, tags: ["kb:1"], cancellationToken: Ct);
        await cache.GetOrCreateAsync("two", Create, tags: ["kb:1", "lookup:2"], cancellationToken: Ct);
        await cache.GetOrCreateAsync("other", Create, tags: ["kb:9"], cancellationToken: Ct);

        await cache.RemoveByTagAsync("kb:1", Ct);

        Assert.Equal(4, await cache.GetOrCreateAsync("one", Create, cancellationToken: Ct));
        Assert.Equal(5, await cache.GetOrCreateAsync("two", Create, cancellationToken: Ct));
        Assert.Equal(3, await cache.GetOrCreateAsync("other", Create, cancellationToken: Ct));
    }

    [Fact]
    public async Task APayloadOverTheLimitIsStillCachedInMemoryWhenThereIsNoDistributedCache()
    {
        await using var provider = Build(o => o.MaximumPayloadBytes = 64);
        var cache = provider.GetRequiredService<HybridCache>();
        var calls = 0;
        ValueTask<string> Create(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult(new string('x', 1000));
        }

        Assert.Equal(1000, (await cache.GetOrCreateAsync("big", Create, cancellationToken: Ct)).Length);
        Assert.Equal(1000, (await cache.GetOrCreateAsync("big", Create, cancellationToken: Ct)).Length);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AThrowingFactoryReachesTheCallerAndIsNotCached()
    {
        await using var provider = Build();
        var cache = provider.GetRequiredService<HybridCache>();
        var calls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await cache.GetOrCreateAsync<int>(
            "k",
            _ =>
            {
                Interlocked.Increment(ref calls);
                throw new InvalidOperationException("boom");
            },
            cancellationToken: Ct));

        Assert.Equal(7, await cache.GetOrCreateAsync("k", _ => ValueTask.FromResult(7), cancellationToken: Ct));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void NoDistributedCacheIsRegisteredByDefault()
    {
        using var provider = Build();

        Assert.Null(provider.GetService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>());
    }
}
