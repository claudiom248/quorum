using Microsoft.Extensions.DependencyInjection;
using Quorum.Shared.Networking;

namespace Quorum.Modules.KnowledgeBases.Storage.Qdrant;

internal static class QdrantStoreRegistration
{
    /// <summary>Registers Qdrant as a kind of knowledge base: its connector, its adapter and the HTTP client every connection goes through.</summary>
    public static IServiceCollection AddQdrantStore(this IServiceCollection services)
    {
        services.AddSingleton<QdrantConnector>();
        services.AddSingleton<QdrantLookups>();
        services.AddSingleton<IStoreAdapter, QdrantStoreAdapter>();

        // Every connection to a Qdrant server goes through this client: the URL is whatever an administrator typed (D-6).
        services.AddServerHttpClient(QdrantConnector.HttpClientName);
        return services;
    }
}
