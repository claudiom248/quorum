using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Quorum.Shared.Networking;

public static class GuardedHttpClientExtensions
{
    /// <summary>Registers the <see cref="OutboundUrlGuard"/> and its resolver once, however many modules ask for them.</summary>
    public static IServiceCollection AddOutboundUrlGuard(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IHostResolver, DnsHostResolver>();
        services.TryAddSingleton<OutboundUrlGuard>();
        return services;
    }

    /// <summary>
    /// A named HTTP client for servers an administrator registers (MCP servers, knowledge base stores): no redirects (a 3xx is a
    /// failed call), no system proxy (it would connect on the guard's behalf, to an address the guard never saw), and the guard
    /// checks the address the socket is really opened to. The client has no timeout of its own: calls are cancelled through their
    /// own tokens. The factory's request logging is removed: it writes the full URL, and a server's URL may carry a secret.
    /// </summary>
    public static IHttpClientBuilder AddGuardedHttpClient(this IServiceCollection services, string name)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        services.AddOutboundUrlGuard();
        return services.AddHttpClient(name, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(provider => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                ConnectCallback = provider.GetRequiredService<OutboundUrlGuard>().ConnectAsync,
            });
    }
}
