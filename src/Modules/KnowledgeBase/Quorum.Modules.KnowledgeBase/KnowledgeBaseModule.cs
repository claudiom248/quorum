using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Modules.KnowledgeBase.Features.Inspect;
using Quorum.Modules.KnowledgeBase.Persistence;
using Quorum.Modules.KnowledgeBase.Storage.Qdrant;
using Quorum.Shared.Networking;
using Quorum.Shared.Persistence;

namespace Quorum.Modules.KnowledgeBase;

public static class KnowledgeBaseModule
{
    public static Assembly Assembly => typeof(KnowledgeBaseModule).Assembly;

    public static IServiceCollection AddKnowledgeBaseModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<KnowledgeBaseDbContext>(KnowledgeBaseDbContext.Schema);

        services.AddSingleton<QdrantConnector>();
        services.AddScoped<KnowledgeBaseInspector>();

        // Every connection to a Qdrant server goes through this client: the URL is whatever an administrator typed (D-6).
        services.AddGuardedHttpClient(QdrantConnector.HttpClientName);

        return services;
    }
}
