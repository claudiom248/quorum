using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Modules.Agents.Chat;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Agents.Embeddings;
using Quorum.Modules.Agents.Features.Tools;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.Agents.Probing;
using Quorum.Modules.Agents.Provisioning;
using Quorum.Modules.Agents.Secrets;
using Quorum.Modules.Agents.Tools;
using Quorum.Modules.Agents.Tools.Mcp;
using Quorum.Modules.Agents.Tools.Rag;
using Quorum.Modules.Agents.Tools.Builtin;
using Quorum.Shared.Networking;
using Quorum.Shared.Persistence;

namespace Quorum.Modules.Agents;

public static class AgentsModule
{
    public static Assembly Assembly => typeof(AgentsModule).Assembly;

    public static IServiceCollection AddAgentsModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<AgentsDbContext>(AgentsDbContext.Schema);
        // After the migrations (registered by the line above): initializers run in registration order.
        services.AddSingleton<IDatabaseInitializer, BuiltinToolsInitializer>();
        services.AddHostedService<AuditRetentionService>();

        services.AddHttpClient(ModelEndpointProbe.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));
        // The timeout bounds the wait for the model's first byte (small local models can take minutes to load);
        // once the answer streams, it runs for as long as the model needs.
        services.AddHttpClient(OpenAiChatClient.HttpClientName, client => client.Timeout = TimeSpan.FromMinutes(10));

        // Embedding a query waits for the model to load like a first chat byte does; callers bound the call with their own token.
        services.AddHttpClient(OpenAiEmbeddings.HttpClientName, client => client.Timeout = TimeSpan.FromMinutes(2));

        services.AddScoped<IEmbeddings, OpenAiEmbeddings>();
        services.AddScoped<IRagToolUsage, RagToolUsage>();
        services.AddScoped<IAssistantDirectory, AssistantDirectory>();
        services.AddMemoryCache();
        services.AddScoped<RagToolResolver>();
        services.AddScoped<RagToolValidation>();
        services.AddScoped<IModelEndpointProbe, ModelEndpointProbe>();
        services.AddScoped<IAssistantProvisioning, AssistantProvisioning>();
        services.AddScoped<AssistantChat>();
        services.AddScoped<IAssistantChat>(provider => provider.GetRequiredService<AssistantChat>());
        services.AddScoped<IAssistantCatalog>(provider => provider.GetRequiredService<AssistantChat>());
        services.AddSingleton<QuorumToolFactory>();
        services.AddScoped<AnswerPipeline>();
        services.AddScoped<ToolCallInvoker>();
        services.AddScoped<ToolAccess>();
        services.AddSingleton<PendingInteractions>();
        services.AddScoped<AuditRetention>();
        services.AddScoped<IToolInteractions, ToolInteractions>();
        services.AddSingleton<IBuiltinTool, CalculatorTool>();
        services.AddSingleton<IBuiltinTool, CurrentDateTimeTool>();
        services.AddSingleton<ApiKeyProtector>();
        services.AddSingleton<McpHeadersProtector>();

        services.AddSingleton<McpServerClient>();

        // Every connection to an MCP server goes through this client (D-18): no redirects, no timeout of its own.
        services.AddServerHttpClient(McpServerClient.HttpClientName);

        return services;
    }
}
