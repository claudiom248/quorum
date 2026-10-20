using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Modules.KnowledgeBases.Features.Inspect;
using Quorum.Modules.KnowledgeBases.Features.Registry;
using Quorum.Modules.KnowledgeBases.Features.Verification;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.Modules.KnowledgeBases.Storage.Qdrant;
using Quorum.Shared.Networking;
using Quorum.Shared.Persistence;

namespace Quorum.Modules.KnowledgeBases;

public static class KnowledgeBasesModule
{
    public static Assembly Assembly => typeof(KnowledgeBasesModule).Assembly;

    public static IServiceCollection AddKnowledgeBasesModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<KnowledgeBasesDbContext>(KnowledgeBasesDbContext.Schema);

        services.AddSingleton<QdrantConnector>();
        services.AddScoped<StoreProbe>();
        services.AddScoped<KnowledgeBaseInspector>();
        services.AddScoped<KnowledgeBaseRegistrar>();
        services.AddSingleton<KnowledgeBaseSecretProtector>();

        // Every connection to a Qdrant server goes through this client: the URL is whatever an administrator typed (D-6).
        services.AddGuardedHttpClient(QdrantConnector.HttpClientName);

        return services;
    }
}
