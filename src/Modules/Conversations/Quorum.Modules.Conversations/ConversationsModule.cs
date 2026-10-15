using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Modules.Conversations.Persistence;
using Quorum.Modules.Conversations.Streaming;
using Quorum.Shared.Persistence;

namespace Quorum.Modules.Conversations;

public static class ConversationsModule
{
    public static Assembly Assembly => typeof(ConversationsModule).Assembly;

    public static IServiceCollection AddConversationsModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<ConversationsDbContext>(ConversationsDbContext.Schema);
        services.AddSingleton<ConversationLocks>();

        return services;
    }
}
