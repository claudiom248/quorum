using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Quorum.Modules.KnowledgeBases.Contracts;
using Quorum.Modules.KnowledgeBases.Features.Inspect;
using Quorum.Modules.KnowledgeBases.Features.Lookups;
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

        services.AddHybridCache();
        services.AddOptions<LookupOptions>().BindConfiguration(LookupOptions.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<LookupOptions>, LookupOptionsValidator>();
        services.AddSingleton<LookupCache>();
        services.AddSingleton<LookupRefreshSignal>();
        services.AddSingleton<LookupRefresher>();
        services.AddScoped<LookupStore>();
        services.AddSingleton<ILookupComputer, UnavailableLookupComputer>();
        services.AddHostedService<LookupRefreshService>();
        services.AddHostedService<LookupCacheWarmup>();

        return services;
    }
}
