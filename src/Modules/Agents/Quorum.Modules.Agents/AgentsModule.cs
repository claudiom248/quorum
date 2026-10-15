using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Modules.Agents.Chat;
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
        // Answers stream for as long as the model needs; the OpenAI client's network timeout bounds each request.
        services.AddHttpClient(ChatClientFactory.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);

        services.AddScoped<IModelEndpointProbe, ModelEndpointProbe>();
        services.AddScoped<IAssistantProvisioning, AssistantProvisioning>();
        services.AddScoped<AssistantChat>();
        services.AddScoped<IAssistantChat>(provider => provider.GetRequiredService<AssistantChat>());
        services.AddScoped<IAssistantCatalog>(provider => provider.GetRequiredService<AssistantChat>());
        services.AddSingleton<ChatClientFactory>();
        services.AddSingleton<ApiKeyProtector>();

        return services;
    }
}
