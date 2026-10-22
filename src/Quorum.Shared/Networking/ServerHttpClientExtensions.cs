using Microsoft.Extensions.DependencyInjection;

namespace Quorum.Shared.Networking;

public static class ServerHttpClientExtensions
{
    /// <summary>
    /// A named HTTP client for servers an administrator registers (MCP servers, knowledge base stores): no redirects (a 3xx is a
    /// failed call). The address is the administrator's responsibility, so nothing here second-guesses it. The client has no
    /// timeout of its own: calls are cancelled through their own tokens. The factory's request logging is removed: it writes the
    /// full URL, and a server's URL may carry a secret.
    /// </summary>
    public static IHttpClientBuilder AddServerHttpClient(this IServiceCollection services, string name)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return services.AddHttpClient(name, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
    }
}
