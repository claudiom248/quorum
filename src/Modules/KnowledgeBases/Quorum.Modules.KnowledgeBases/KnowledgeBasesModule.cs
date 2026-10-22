using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Modules.KnowledgeBases.Contracts;
using Quorum.Modules.KnowledgeBases.Features.Inspect;
using Quorum.Modules.KnowledgeBases.Features.Registry;
using Quorum.Modules.KnowledgeBases.Features.Search;
using Quorum.Modules.KnowledgeBases.Features.Verification;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.Modules.KnowledgeBases.Storage;
using Quorum.Modules.KnowledgeBases.Storage.Qdrant;
using Quorum.Shared.Persistence;

namespace Quorum.Modules.KnowledgeBases;

public static class KnowledgeBasesModule
{
    public static Assembly Assembly => typeof(KnowledgeBasesModule).Assembly;

    public static IServiceCollection AddKnowledgeBasesModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<KnowledgeBasesDbContext>(KnowledgeBasesDbContext.Schema);

        services.AddQdrantStore();
        services.AddSingleton<StoreAdapters>();
        services.AddHostedService<StoreAdaptersStartupCheck>();
        services.AddScoped<StoreProbe>();
        services.AddScoped<KnowledgeBaseInspector>();
        services.AddScoped<KnowledgeBaseRegistrar>();
        services.AddScoped<KnowledgeBaseSearchEngine>();
        services.AddScoped<IKnowledgeBaseSearch, KnowledgeBaseSearch>();
        services.AddScoped<IKnowledgeBaseCatalog, KnowledgeBaseCatalog>();
        services.AddSingleton<KnowledgeBaseSecretProtector>();

        return services;
    }
}
