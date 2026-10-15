using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.Agents.Probing;
using Quorum.Modules.Agents.Provisioning;
using Quorum.Modules.Agents.Secrets;
using Quorum.Shared.Persistence;

namespace Quorum.Modules.Agents;

public static class AgentsModule
{
    public static Assembly Assembly => typeof(AgentsModule).Assembly;

    public static IServiceCollection AddAgentsModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<AgentsDbContext>(AgentsDbContext.Schema);

        services.AddHttpClient(ModelEndpointProbe.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));
        services.AddScoped<IModelEndpointProbe, ModelEndpointProbe>();
        services.AddScoped<IAssistantProvisioning, AssistantProvisioning>();
        services.AddSingleton<ApiKeyProtector>();

        return services;
    }
}
