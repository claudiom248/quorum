using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Modules.Models.Contracts;
using Quorum.Modules.Models.Embeddings;
using Quorum.Modules.Models.Features.Models;
using Quorum.Modules.Models.Persistence;
using Quorum.Modules.Models.Probing;
using Quorum.Modules.Models.Secrets;
using Quorum.Shared.Persistence;

namespace Quorum.Modules.Models;

public static class ModelsModule
{
    public static Assembly Assembly => typeof(ModelsModule).Assembly;

    public static IServiceCollection AddModelsModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<ModelsDbContext>(ModelsDbContext.Schema);

        services.AddScoped<ApiKeyProtector>();
        services.AddScoped<IModelProbe, ModelProbe>();
        services.AddScoped<IModelEmbeddings, ModelEmbeddings>();
        services.AddScoped<IModelCatalog, ModelCatalog>();
        services.AddScoped<ModelUsageGuard>();

        services.AddHttpClient(ModelProbe.HttpClientName);
        services.AddHttpClient(ModelEmbeddings.HttpClientName);

        return services;
    }
}
