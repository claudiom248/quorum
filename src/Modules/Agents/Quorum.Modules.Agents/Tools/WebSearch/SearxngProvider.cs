using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Quorum.Modules.Agents.Tools.WebSearch;

/// <summary>What one search needs: where to ask, how long to wait for it, and the limits the administrator set on the tool.</summary>
/// <param name="InstanceUrl">The address of the SearXNG instance, as the platform's administrator wrote it.</param>
/// <param name="TimeoutSeconds">How long the whole search may take, retries included.</param>
/// <param name="Text">What to look for.</param>
/// <param name="TopN">How many results to bring back.</param>
/// <param name="Engines">The engines to ask, or <see langword="null"/> for all of the ones the instance enables.</param>
/// <param name="Language">A language code, or <see langword="null"/> to leave it to the instance.</param>
/// <param name="SafeSearch">0 off, 1 moderate, 2 strict.</param>
/// <param name="TimeRange">How far back the results may be ("day", "week", "month", "year"), or <see langword="null"/> for no limit.</param>
internal sealed record WebSearchQuery(
    string InstanceUrl,
    int TimeoutSeconds,
    string Text,
    int TopN,
    IReadOnlyList<string>? Engines,
    string? Language,
    int SafeSearch,
    string? TimeRange);

/// <summary>One result of a search. Its text is what the open web says: untrusted, like a knowledge base chunk (spec 003 D-15).</summary>
/// <param name="Score">What the instance says the result is worth; one engine's number is not another's, so it is shown, never used to order.</param>
internal sealed record WebSearchHit(string Title, string Url, string? Snippet, string? Engine, double? Score = null);

internal enum WebSearchOutcome
{
    /// <summary>The instance answered, with any number of results.</summary>
    Ok,

    /// <summary>Nothing answered within the time the platform allows.</summary>
    TimedOut,

    /// <summary>The instance could not be reached, refused the search, or answered with something that is not a search.</summary>
    Unreachable,
}

/// <param name="EchoedQuery">What the instance says it looked for; the model is told this when nothing was found.</param>
/// <param name="Attempts">How many times the search was tried, for the message the model reads.</param>
internal sealed record WebSearchResponse(
    WebSearchOutcome Outcome,
    IReadOnlyList<WebSearchHit> Hits,
    string? EchoedQuery = null,
    int Attempts = 1);

/// <summary>
/// Searching the open web (spec 005). A provider sits behind this interface because the platform means to answer with more than one;
/// for now SearXNG is the only one, and the only one an administrator can configure.
/// </summary>
internal interface IWebSearchProvider
{
    /// <summary>Never throws for what the web did: an answer that is not there is told with <see cref="WebSearchResponse.Outcome"/>.</summary>
    Task<WebSearchResponse> SearchAsync(WebSearchQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// SearXNG's own API: <c>GET /search?q=...&amp;format=json</c>. The instance has to enable JSON output (its <c>format</c> setting),
/// which a self-hosted one does by configuration. A failure that could be a passing moment is tried again with a growing wait, and the
/// retries are inside the time the platform allows, so a search never costs more than that. What the results say is never trusted.
/// </summary>
internal sealed partial class SearxngProvider(IHttpClientFactory httpClientFactory, TimeProvider clock, ILogger<SearxngProvider> logger) : IWebSearchProvider
{
    /// <summary>The client every web search goes through: no timeout of its own, because the search bounds its own wait.</summary>
    public const string HttpClientName = "quorum-searxng";

    private const int MaxAttempts = 3;

    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

    public async Task<WebSearchResponse> SearchAsync(WebSearchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(query.TimeoutSeconds), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        var asked = Ask(query.Text);

        var attempts = 1;
        while (true)
        {
            var (response, retry) = await TryAsync(query, asked, linked.Token);
            if (response is not null)
            {
                return response with { Attempts = attempts };
            }

            if (!retry || budget.IsCancellationRequested || attempts == MaxAttempts)
            {
                break;
            }

            LogRetry(logger, attempts);
            attempts++;
            try
            {
                await Task.Delay(Backoff[attempts - 2], clock, linked.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return new WebSearchResponse(
            budget.IsCancellationRequested ? WebSearchOutcome.TimedOut : WebSearchOutcome.Unreachable,
            [],
            asked,
            attempts);
    }

    /// <returns>The answer to give the model, or <see langword="null"/> with whether the search may be tried again.</returns>
    private async Task<(WebSearchResponse? Response, bool Retry)> TryAsync(WebSearchQuery query, string asked, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        try
        {
            using var response = await client.GetAsync(Address(query, asked), cancellationToken);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                return (Parse(query, json, asked), false);
            }

            // Too many requests and a server failing on purpose are worth another try; anything else will not change by asking again.
            LogRefused(logger, (int)response.StatusCode);
            var transient = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
            return (transient ? null : new WebSearchResponse(WebSearchOutcome.Unreachable, [], asked), transient);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The client's own timeout dropped the request; the search itself still has time.
            return (null, true);
        }
        catch (OperationCanceledException)
        {
            return (null, false);
        }
        catch (HttpRequestException exception)
        {
            LogUnreachable(logger, exception.Message);
            return (null, true);
        }
        catch (UriFormatException exception)
        {
            // The address the administrator wrote is only checked for its shape, so it can still fail to become one.
            LogUnreachable(logger, exception.Message);
            return (null, false);
        }
        catch (JsonException exception)
        {
            // An instance that answers with HTML (its JSON format is off) will keep answering that way: asking again changes nothing.
            LogNotJson(logger, exception.Message);
            return (new WebSearchResponse(WebSearchOutcome.Unreachable, [], asked), false);
        }
    }

    /// <summary>What the instance is asked for: the trimmed query, cut to the length it answers with (spec 005, edge 7).</summary>
    private static string Ask(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= WebSearchToolDefinition.MaxQueryCharacters ? trimmed : trimmed[..WebSearchToolDefinition.MaxQueryCharacters];
    }

    private static Uri Address(WebSearchQuery query, string asked)
    {
        var parameters = new List<string>
        {
            "q=" + Uri.EscapeDataString(asked),
            "format=json",
            "safesearch=" + query.SafeSearch.ToString(CultureInfo.InvariantCulture),
        };

        if (query.Engines is { Count: > 0 })
        {
            parameters.Add("engines=" + Uri.EscapeDataString(string.Join(",", query.Engines)));
        }

        if (query.Language is { Length: > 0 })
        {
            parameters.Add("language=" + Uri.EscapeDataString(query.Language));
        }

        if (query.TimeRange is { Length: > 0 })
        {
            parameters.Add("time_range=" + Uri.EscapeDataString(query.TimeRange));
        }

        var address = new UriBuilder(new Uri(new Uri(query.InstanceUrl.TrimEnd('/') + "/", UriKind.Absolute), "search"))
        {
            Query = string.Join("&", parameters),
        };
        return address.Uri;
    }

    private static WebSearchResponse Parse(WebSearchQuery query, string json, string asked)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array)
        {
            return new WebSearchResponse(WebSearchOutcome.Unreachable, [], asked);
        }

        var hits = new List<WebSearchHit>(query.TopN);
        foreach (var item in results.EnumerateArray())
        {
            if (hits.Count == query.TopN)
            {
                break;
            }

            var url = Text(item, "url");
            var title = Text(item, "title");

            // A result with no address or no title is nothing the model could cite or read.
            if (url is null || title is null)
            {
                continue;
            }

            hits.Add(new WebSearchHit(title, url, Text(item, "content"), Text(item, "engine"), Number(item, "score")));
        }

        return new WebSearchResponse(WebSearchOutcome.Ok, hits, Text(document.RootElement, "query") ?? asked);
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static double? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
            ? number
            : null;

    // What was searched for is user data and stays out of the log.
    [LoggerMessage(Level = LogLevel.Debug, Message = "Web search retried after a failed attempt (attempt {Attempt})")]
    private static partial void LogRetry(ILogger logger, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SearXNG refused the search with status {Status}")]
    private static partial void LogRefused(ILogger logger, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SearXNG could not be reached: {Reason}")]
    private static partial void LogUnreachable(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SearXNG did not answer with JSON: {Reason}")]
    private static partial void LogNotJson(ILogger logger, string reason);
}
