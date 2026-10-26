using Microsoft.Extensions.DependencyInjection;

namespace Quorum.Modules.KnowledgeBases.Storage.PgVector;

internal static class PgVectorStoreRegistration
{
    /// <summary>Registers PostgreSQL with pgvector as a kind of knowledge base: its pools, its connector, its source listing, its lookups and its adapter.</summary>
    public static IServiceCollection AddPgVectorStore(this IServiceCollection services)
    {
        services.AddSingleton<PgVectorDataSources>();
        services.AddSingleton<PgVectorConnector>();
        services.AddSingleton<PgVectorSourceListing>();
        services.AddSingleton<PgVectorLookups>();
        services.AddSingleton<IStoreAdapter, PgVectorStoreAdapter>();
        return services;
    }
}
