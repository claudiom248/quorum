using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;

namespace Quorum.Modules.Agents.Tools.WebFetch;

/// <summary>
/// What the platform is allowed to read on each site it visits (spec 006, AC-9). A site says it in a <c>robots.txt</c> at its root;
/// reading that file for every URL would slow every answer, so the platform remembers it per origin for one hour.
/// </summary>
/// <remarks>
/// The parser follows Google's robots.txt specification with the 2025 changes: rules are matched by the length of the line that
/// won, wildcards <c>*</c> match anything between them, and a line ending with <c>$</c> has to match the URL to its very end.
/// Any failure — no <c>robots.txt</c>, an unreadable one, a timeout — is treated as permission (fail-open): the platform is
/// polite, not a gatekeeper, and a site's broken file must not hide a page from the model.
/// </remarks>
internal sealed class RobotsCache(IMemoryCache cache, IHttpClientFactory clients)
{
    /// <summary>How long a site's file is remembered: enough that a search that reads the same site does not re-ask for each page.</summary>
    private static readonly TimeSpan EntryTtl = TimeSpan.FromHours(1);

    /// <summary>How long the platform waits for a site's robots.txt before it treats the site as permitting everything.</summary>
    private static readonly TimeSpan RobotsTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Whether the platform may read the given URL under the site's own file; the answer for any failure is <see langword="true"/>.</summary>
    public async Task<bool> IsAllowedAsync(string url, string userAgent, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return true;
        }

        var rules = await RulesForAsync(parsed, userAgent, cancellationToken);
        return rules.Allows(parsed);
    }

    private async Task<RobotsRules> RulesForAsync(Uri url, string userAgent, CancellationToken cancellationToken)
    {
        var origin = $"{url.Scheme}://{url.Authority}";
        if (cache.TryGetValue(origin, out RobotsRules? cached) && cached is not null)
        {
            return cached;
        }

        var rules = await LoadFromSiteAsync(origin, url.Host, cancellationToken);
        cache.Set(origin, rules, EntryTtl);
        return rules;
    }

    private async Task<RobotsRules> LoadFromSiteAsync(string origin, string host, CancellationToken cancellationToken)
    {
        var client = clients.CreateClient(HttpWebFetchProvider.HttpClientName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RobotsTimeout);

        try
        {
            using var response = await client.GetAsync($"{origin}/robots.txt", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                // 404 (or 500, or anything else): the site does not say it forbids anything.
                return RobotsRules.AllowAll;
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            return RobotsRules.Parse(body, host);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            return RobotsRules.AllowAll;
        }
    }
}

/// <summary>The lines of one site's robots.txt that speak for this platform.</summary>
internal sealed class RobotsRules
{
    public static RobotsRules AllowAll { get; } = new([]);

    private readonly List<RobotsRule> _rules;

    private RobotsRules(List<RobotsRule> rules)
    {
        _rules = rules;
    }

    public bool Allows(Uri url)
    {
        if (_rules.Count == 0)
        {
            return true;
        }

        var path = url.PathAndQuery + url.Fragment;
        RobotsRule? match = null;
        foreach (var rule in _rules)
        {
            if (!rule.Matches(path))
            {
                continue;
            }

            // Google's rule: the longest line that matches wins; with two lines of the same length, Allow beats Disallow.
            if (match is null
                || rule.PatternLength > match.PatternLength
                || (rule.PatternLength == match.PatternLength && rule.Allowed && !match.Allowed))
            {
                match = rule;
            }
        }

        return match?.Allowed ?? true;
    }

    public static RobotsRules Parse(string content, string host)
    {
        ArgumentNullException.ThrowIfNull(content);

        var groups = new List<RobotsGroup>();
        RobotsGroup? current = null;
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            // Comments may be anywhere, they run to the end of the line.
            var comment = line.IndexOf('#');
            if (comment >= 0)
            {
                line = line[..comment].TrimEnd();
            }

            if (line.Length == 0)
            {
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            var field = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            switch (field)
            {
                case "user-agent":
                    if (current is null || current.Rules.Count > 0)
                    {
                        current = new RobotsGroup();
                        groups.Add(current);
                    }

                    current.UserAgents.Add(value.ToLowerInvariant());
                    break;
                case "allow" when value.Length > 0:
                    current?.Rules.Add(new RobotsRule(value, Allowed: true));
                    break;
                case "disallow":
                    // An empty Disallow means the whole site is fair game.
                    if (value.Length == 0)
                    {
                        current?.Rules.Clear();
                    }
                    else
                    {
                        current?.Rules.Add(new RobotsRule(value, Allowed: false));
                    }

                    break;
                default:
                    break;
            }
        }

        var wildcard = groups.FirstOrDefault(g => g.UserAgents.Contains("*"));
        var named = groups.FirstOrDefault(g => g.UserAgents.Any(agent => Matches(agent, host)));
        var chosen = named ?? wildcard;
        return chosen is null || chosen.Rules.Count == 0 ? AllowAll : new RobotsRules(chosen.Rules);
    }

    /// <summary>A file's group names the platform with, in the plain sense: the same host, or a wildcard inside the agent name.</summary>
    private static bool Matches(string agent, string host) =>
        agent.Length > 0 && (agent.Contains('*') ? WildcardMatch(agent, host) : host.Contains(agent, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether <paramref name="pattern"/> covers <paramref name="value"/>. <c>*</c> means anything in between.</summary>
    private static bool WildcardMatch(string pattern, string value)
    {
        var parts = pattern.Split('*', StringSplitOptions.None);
        var index = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0)
            {
                continue;
            }

            var found = value.IndexOf(part, index, StringComparison.Ordinal);
            if (found < 0)
            {
                return false;
            }

            if (i == 0 && found != 0)
            {
                return false;
            }

            index = found + part.Length;
        }

        return true;
    }
}

internal sealed record RobotsGroup
{
    public List<string> UserAgents { get; } = [];

    public List<RobotsRule> Rules { get; } = [];
}

internal sealed record RobotsRule(string Pattern, bool Allowed)
{
    /// <summary>The Google specification counts how long the rule's path is, not what it says: that is the tiebreaker.</summary>
    public int PatternLength => Pattern.Length;

    /// <summary>The rule's pattern covers the URL path: wildcards match anything, a trailing <c>$</c> anchors it to the end.</summary>
    public bool Matches(string path)
    {
        if (Pattern.Length == 0)
        {
            return false;
        }

        var anchored = Pattern.EndsWith('$');
        var pattern = Pattern.TrimEnd('$');

        // A plain prefix: no wildcards, no $ — the classic rule.
        if (!pattern.Contains('*') && !anchored)
        {
            return path.StartsWith(pattern, StringComparison.Ordinal);
        }

        var parts = pattern.Split('*', StringSplitOptions.None);
        var index = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0)
            {
                continue;
            }

            var found = path.IndexOf(part, index, StringComparison.Ordinal);
            if (found < 0)
            {
                return false;
            }

            if (i == 0 && found != 0)
            {
                return false;
            }

            index = found + part.Length;
        }

        return anchored ? index == path.Length : true;
    }
}
