using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace Quorum.Modules.Agents.Tools.WebFetch;

/// <summary>
/// The pages the platform has already read (spec 006, AC-8). A search that opens the same article three times pays one wait,
/// not three, and the site is asked once. HybridCache gives the stampede protection for free: the first caller loads, the others
/// wait on the same load and read what it produced.
/// </summary>
/// <remarks>
/// The cache sits behind the provider, in front of the site. What it remembers is the answer of one URL — a failure included,
/// because a page that returned 404 a minute ago is likely to do it again. The tool that asks is the one that knows the platform's
/// current cache policy; the cache does not read the database, so the tool passes the lifetime to every call.
/// </remarks>
internal sealed partial class WebFetchCache(HybridCache cache, ILogger<WebFetchCache> logger)
{
    public const string Tag = "web-fetch";

    /// <summary>
    /// What the site said, or what the load produced: either from the memory of one hour back, or fresh. A cache that fails
    /// answers as if it were empty, so a page is never hidden by the cache being broken.
    /// </summary>
    public async Task<WebFetchResponse> GetOrCreateAsync(
        string url,
        TimeSpan ttl,
        Func<CancellationToken, Task<WebFetchResponse>> load,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        ArgumentNullException.ThrowIfNull(load);

        var options = new HybridCacheEntryOptions
        {
            Expiration = ttl,
            LocalCacheExpiration = ttl,
        };

        try
        {
            // HybridCache hands the state to a ValueTask-returning callback, so the tool's Task-returning load is wrapped.
            return await cache.GetOrCreateAsync(
                KeyFor(url),
                load,
                static async (callback, token) => await callback(token),
                options,
                [Tag],
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogCacheFailed(logger, exception.GetType().Name);
            return await load(cancellationToken);
        }
    }

    /// <summary>Drops every remembered page, as the platform's cache policy changes (spec 006, AC-14).</summary>
    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        try
        {
            await cache.RemoveByTagAsync(Tag, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogCacheFailed(logger, exception.GetType().Name);
        }
    }

    /// <summary>Same URL, same key: the hash keeps it safe for characters a URL can carry that a cache key cannot.</summary>
    internal static string KeyFor(string url) => $"web-fetch:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)))}";

    [LoggerMessage(Level = LogLevel.Warning, Message = "The web fetch cache failed ({ExceptionType}); the site is asked again")]
    private static partial void LogCacheFailed(ILogger logger, string exceptionType);
}
