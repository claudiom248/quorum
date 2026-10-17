using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Modules.KnowledgeBase.Persistence;
using Quorum.Shared.Persistence;

namespace Quorum.Modules.KnowledgeBase;

public static class KnowledgeBaseModule
{
    public static Assembly Assembly => typeof(KnowledgeBaseModule).Assembly;

    public static IServiceCollection AddKnowledgeBaseModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<KnowledgeBaseDbContext>(KnowledgeBaseDbContext.Schema);

        return services;
    }
}
