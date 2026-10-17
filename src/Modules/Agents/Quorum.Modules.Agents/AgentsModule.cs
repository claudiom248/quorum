using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Modules.Agents.Chat;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.Agents.Probing;
using Quorum.Modules.Agents.Provisioning;
using Quorum.Modules.Agents.Secrets;
using Quorum.Modules.Agents.Tools;
using Quorum.Modules.Agents.Tools.Native;
using Quorum.Shared.Persistence;

namespace Quorum.Modules.Agents;

public static class AgentsModule
{
    public static Assembly Assembly => typeof(AgentsModule).Assembly;

    public static IServiceCollection AddAgentsModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<AgentsDbContext>(AgentsDbContext.Schema);
        // After the migrations (registered by the line above): initializers run in registration order.
        services.AddSingleton<IDatabaseInitializer, NativeToolsInitializer>();

        services.AddHttpClient(ModelEndpointProbe.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));
        // The timeout bounds the wait for the model's first byte (small local models can take minutes to load);
        // once the answer streams, it runs for as long as the model needs.
        services.AddHttpClient(OpenAiChatStream.HttpClientName, client => client.Timeout = TimeSpan.FromMinutes(10));

        services.AddScoped<IModelEndpointProbe, ModelEndpointProbe>();
        services.AddScoped<IAssistantProvisioning, AssistantProvisioning>();
        services.AddScoped<AssistantChat>();
        services.AddScoped<IAssistantChat>(provider => provider.GetRequiredService<AssistantChat>());
        services.AddScoped<IAssistantCatalog>(provider => provider.GetRequiredService<AssistantChat>());
        services.AddSingleton<OpenAiChatStream>();
        services.AddScoped<ToolLoop>();
        services.AddScoped<ToolExecutor>();
        services.AddScoped<ToolAccess>();
        services.AddSingleton<INativeTool, CalculatorTool>();
        services.AddSingleton<INativeTool, CurrentDateTimeTool>();
        services.AddSingleton<ApiKeyProtector>();

        return services;
    }
}
