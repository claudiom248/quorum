using System.Text.RegularExpressions;
using Quorum.SampleKnowledgeBase;
using Testcontainers.Qdrant;

namespace Quorum.Integration.Tests.Fixtures;

/// <summary>
/// One Qdrant container for the whole test assembly, started the first time a test asks for it so runs that never touch a
/// knowledge base do not pay for it. The image is the one the dev compose pins, so the tests run against what developers run.
/// </summary>
public sealed partial class QdrantFixture : IAsyncLifetime
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, QdrantContainer> _containers = new(StringComparer.Ordinal);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var container in _containers.Values)
        {
            await container.DisposeAsync();
        }

        _gate.Dispose();
    }

    /// <summary>The gRPC address of the shared Qdrant (no API key): the one a knowledge base is registered with.</summary>
    public Task<Uri> GetUrlAsync() => GetUrlAsync(apiKey: null);

    /// <summary>The gRPC address of a Qdrant that refuses requests without <paramref name="apiKey"/>; one container per key.</summary>
    public Task<Uri> GetUrlAsync(string? apiKey) => AddressAsync(apiKey, container => container.GetGrpcConnectionString());

    /// <summary>The REST address of the shared Qdrant, for tests that look at the store the way a data owner does.</summary>
    public Task<Uri> GetRestUrlAsync() => AddressAsync(apiKey: null, container => container.GetHttpConnectionString());

    private async Task<Uri> AddressAsync(string? apiKey, Func<QdrantContainer, string> address)
    {
        await _gate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            var key = apiKey ?? string.Empty;
            if (!_containers.TryGetValue(key, out var container))
            {
                var builder = new QdrantBuilder(PinnedImage());
                if (apiKey is not null)
                {
                    builder = builder.WithEnvironment("QDRANT__SERVICE__API_KEY", apiKey);
                }

                container = builder.Build();
                await container.StartAsync(TestContext.Current.CancellationToken);
                _containers[key] = container;
            }

            return new Uri(address(container));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A prefix no other test uses, so tests share the container without seeing each other's collections.</summary>
    public static string NewPrefix() => $"t{Guid.NewGuid():N}"[..9] + "_";

    /// <summary>Writes datasets into the Qdrant that needs <paramref name="apiKey"/> (or none), the way a data owner's ingestion would.</summary>
    public async Task SeedAsync(IEnumerable<SampleDataset> datasets, string? apiKey = null)
    {
        ArgumentNullException.ThrowIfNull(datasets);

        var url = await AddressAsync(apiKey, container => container.GetHttpConnectionString());
        using var http = new HttpClient { BaseAddress = url };
        if (apiKey is not null)
        {
            http.DefaultRequestHeaders.Add("api-key", apiKey);
        }

        await new QdrantSeeder(http).SeedAsync(datasets, TestContext.Current.CancellationToken);
    }

    private static string PinnedImage()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var compose = Path.Combine(directory.FullName, "deploy", "docker-compose.dev.yml");
            if (File.Exists(compose))
            {
                var match = ImageLine().Match(File.ReadAllText(compose));
                return match.Success ? match.Groups["image"].Value : throw new InvalidOperationException("No qdrant image in the dev compose file.");
            }
        }

        throw new InvalidOperationException("deploy/docker-compose.dev.yml not found above the test output directory.");
    }

    [GeneratedRegex(@"image:\s*(?<image>qdrant/qdrant:\S+)")]
    private static partial Regex ImageLine();
}
