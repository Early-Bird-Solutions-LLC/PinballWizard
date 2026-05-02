using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Scraping.Polite;

/// <summary>
/// Per-host cache of parsed robots.txt. Singleton — fetched once per
/// host per process, refreshed when the cached entry is older than
/// <see cref="PolitenessOptions.RobotsTxtTtlSeconds"/>.
/// </summary>
/// <remarks>
/// The parser implements the subset of the Robots Exclusion Protocol
/// the project actually needs: <c>User-agent</c>, <c>Allow</c>,
/// <c>Disallow</c>, and longest-path-wins matching. <c>Sitemap</c>
/// directives are recorded for future use; <c>Crawl-delay</c> is
/// observed and exposed as <see cref="RobotsTxtRules.CrawlDelay"/>
/// (the gate may choose to use it as an additional floor).
/// </remarks>
public sealed class RobotsTxtCache
{
    private readonly HttpClient _httpClient;
    private readonly PolitenessOptions _options;
    private readonly ILogger<RobotsTxtCache> _logger;
    private readonly ConcurrentDictionary<string, CachedEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new cache.</summary>
    public RobotsTxtCache(
        HttpClient httpClient,
        IOptions<PolitenessOptions> options,
        ILogger<RobotsTxtCache> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Returns the parsed rules for <paramref name="host"/>'s robots.txt.
    /// Fetches and parses on first call per host or after the cache TTL
    /// expires. Network failures resolve to a permissive rule set
    /// (everything allowed) — the policy choice is "if we can't read
    /// robots.txt, don't block legitimate work because of it."
    /// </summary>
    public async Task<RobotsTxtRules> GetRulesAsync(Uri host, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);

        var hostKey = host.GetLeftPart(UriPartial.Authority);
        var now = DateTimeOffset.UtcNow;

        if (_cache.TryGetValue(hostKey, out var cached) &&
            now - cached.FetchedAt < TimeSpan.FromSeconds(_options.RobotsTxtTtlSeconds))
        {
            return cached.Rules;
        }

        var url = new Uri(new Uri(hostKey), _options.RobotsTxtPath);
        var rules = await FetchAsync(url, cancellationToken).ConfigureAwait(false);
        _cache[hostKey] = new CachedEntry(rules, now);
        return rules;
    }

    /// <summary>
    /// Convenience: returns true if the requested URL is permitted by
    /// the host's robots.txt for the configured User-Agent.
    /// </summary>
    public async Task<bool> IsAllowedAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        var rules = await GetRulesAsync(url, cancellationToken).ConfigureAwait(false);
        return rules.IsAllowed(url.AbsolutePath, _options.UserAgent);
    }

    private async Task<RobotsTxtRules> FetchAsync(Uri url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogInformation("No robots.txt at {Url} (404). Treating all paths as allowed.", url);
                return RobotsTxtRules.AllowAll;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("robots.txt fetch returned {StatusCode} from {Url}. Treating all paths as allowed.", (int)response.StatusCode, url);
                return RobotsTxtRules.AllowAll;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return RobotsTxtParser.Parse(body);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "robots.txt fetch failed for {Url}. Treating all paths as allowed.", url);
            return RobotsTxtRules.AllowAll;
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "robots.txt fetch timed out for {Url}. Treating all paths as allowed.", url);
            return RobotsTxtRules.AllowAll;
        }
    }

    private sealed record CachedEntry(RobotsTxtRules Rules, DateTimeOffset FetchedAt);
}

/// <summary>
/// Parsed rules from a host's robots.txt. Immutable.
/// </summary>
public sealed class RobotsTxtRules
{
    private readonly Dictionary<string, AgentRules> _agentRules;

    /// <summary>Sentinel rules object that allows every path for every agent.</summary>
    public static RobotsTxtRules AllowAll { get; } = new(new Dictionary<string, AgentRules>(StringComparer.OrdinalIgnoreCase), null, []);

    /// <summary>Crawl-delay seconds declared for our matching User-Agent block; null if none.</summary>
    public double? CrawlDelay { get; }

    /// <summary>Sitemap URLs declared in robots.txt. Used by future sitemap-preferring scrapers.</summary>
    public IReadOnlyList<string> Sitemaps { get; }

    internal RobotsTxtRules(Dictionary<string, AgentRules> agentRules, double? crawlDelay, IReadOnlyList<string> sitemaps)
    {
        _agentRules = agentRules;
        CrawlDelay = crawlDelay;
        Sitemaps = sitemaps;
    }

    /// <summary>
    /// Returns true if <paramref name="path"/> is allowed for
    /// <paramref name="userAgent"/>. Matches the most specific
    /// User-agent block (substring match against the configured UA),
    /// then applies Allow/Disallow rules with longest-path-wins
    /// semantics. Falls back to <c>*</c> if no specific match.
    /// </summary>
    public bool IsAllowed(string path, string userAgent)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(userAgent);

        var agentBlock = FindMatchingAgentBlock(userAgent);
        if (agentBlock is null)
        {
            return true;
        }

        // Longest-match rule: pick the most-specific Allow / Disallow whose pattern matches the path.
        Rule? best = null;
        foreach (var rule in agentBlock.Rules)
        {
            if (PatternMatches(rule.Pattern, path) &&
                (best is null || rule.Pattern.Length > best.Pattern.Length))
            {
                best = rule;
            }
        }

        return best?.Allow ?? true;
    }

    private AgentRules? FindMatchingAgentBlock(string userAgent)
    {
        foreach (var (agent, rules) in _agentRules)
        {
            if (agent != "*" && userAgent.Contains(agent, StringComparison.OrdinalIgnoreCase))
            {
                return rules;
            }
        }
        return _agentRules.TryGetValue("*", out var wildcard) ? wildcard : null;
    }

    private static bool PatternMatches(string pattern, string path)
    {
        // robots.txt patterns: '*' = any sequence, '$' = end-of-path. Otherwise literal prefix.
        if (pattern.Length == 0)
        {
            return false;
        }

        if (!pattern.Contains('*') && !pattern.Contains('$'))
        {
            return path.StartsWith(pattern, StringComparison.Ordinal);
        }

        // Build a small regex translation for the wildcard syntax.
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace(@"\*", ".*", StringComparison.Ordinal)
            .Replace(@"\$", "$", StringComparison.Ordinal);
        return System.Text.RegularExpressions.Regex.IsMatch(path, regex);
    }

    internal sealed record Rule(string Pattern, bool Allow);

    internal sealed class AgentRules
    {
        public List<Rule> Rules { get; } = [];
        public double? CrawlDelay { get; set; }
    }
}

/// <summary>Tiny robots.txt parser implementing the subset the project needs.</summary>
public static class RobotsTxtParser
{
    public static RobotsTxtRules Parse(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var agentRules = new Dictionary<string, RobotsTxtRules.AgentRules>(StringComparer.OrdinalIgnoreCase);
        var sitemaps = new List<string>();
        RobotsTxtRules.AgentRules? currentBlock = null;
        double? globalCrawlDelay = null;

        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine;
            var commentStart = line.IndexOf('#', StringComparison.Ordinal);
            if (commentStart >= 0)
            {
                line = line[..commentStart];
            }
            line = line.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var colonIndex = line.IndexOf(':', StringComparison.Ordinal);
            if (colonIndex <= 0)
            {
                continue;
            }

            var directive = line[..colonIndex].Trim();
            var value = line[(colonIndex + 1)..].Trim();

            switch (directive.ToLowerInvariant())
            {
                case "user-agent":
                    if (!agentRules.TryGetValue(value, out currentBlock))
                    {
                        currentBlock = new RobotsTxtRules.AgentRules();
                        agentRules[value] = currentBlock;
                    }
                    break;

                case "allow":
                    currentBlock?.Rules.Add(new RobotsTxtRules.Rule(value, Allow: true));
                    break;

                case "disallow":
                    if (value.Length > 0)
                    {
                        currentBlock?.Rules.Add(new RobotsTxtRules.Rule(value, Allow: false));
                    }
                    break;

                case "crawl-delay":
                    if (double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var delay))
                    {
                        if (currentBlock is not null)
                        {
                            currentBlock.CrawlDelay = delay;
                        }
                        else
                        {
                            globalCrawlDelay = delay;
                        }
                    }
                    break;

                case "sitemap":
                    if (value.Length > 0)
                    {
                        sitemaps.Add(value);
                    }
                    break;
            }
        }

        return new RobotsTxtRules(agentRules, globalCrawlDelay, sitemaps);
    }
}
