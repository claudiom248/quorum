using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quorum.Modules.KnowledgeBases.Domain;

namespace Quorum.Modules.KnowledgeBases.Storage;

/// <summary>
/// The adapter of each <see cref="KnowledgeBaseKind"/>. A kind without an adapter is a mistake of the installation, so it fails
/// when the application starts, not when somebody registers a knowledge base of that kind.
/// </summary>
internal sealed class StoreAdapters
{
    private readonly Dictionary<KnowledgeBaseKind, IStoreAdapter> _byKind;

    public StoreAdapters(IEnumerable<IStoreAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        _byKind = adapters.ToDictionary(adapter => adapter.Kind);
        var missing = Enum.GetValues<KnowledgeBaseKind>().Where(kind => !_byKind.ContainsKey(kind)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"No store adapter is registered for the knowledge base kind(s) {string.Join(", ", missing)}.");
        }
    }

    public IReadOnlyCollection<KnowledgeBaseKind> Kinds => _byKind.Keys;

    public IStoreAdapter For(KnowledgeBaseKind kind) => _byKind[kind];

    /// <summary>What a request with a kind that is not supported is told; it names the kinds that are.</summary>
    public string UnsupportedKindMessage()
    {
        var kinds = _byKind.Keys.Order().ToList();
        return kinds.Count == 1
            ? $"The kind must be {kinds[0]}: it is the only storage kind supported."
            : $"The kind must be one of: {string.Join(", ", kinds)}.";
    }
}

/// <summary>Builds <see cref="StoreAdapters"/> as the host starts, so a kind without an adapter stops it with a clear message.</summary>
internal sealed partial class StoreAdaptersStartupCheck(StoreAdapters adapters, ILogger<StoreAdaptersStartupCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        LogKinds(logger, string.Join(", ", adapters.Kinds.Order()));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Knowledge base kinds supported: {Kinds}")]
    private static partial void LogKinds(ILogger logger, string kinds);
}
