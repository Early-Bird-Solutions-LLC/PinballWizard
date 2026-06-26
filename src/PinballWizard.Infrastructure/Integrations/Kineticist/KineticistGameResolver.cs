using Microsoft.Extensions.Logging;

namespace PinballWizard.Infrastructure.Integrations.Kineticist;

/// <summary>
/// Resolves a Kineticist tutorial's game to its OPDB-keyed editions via the
/// Kineticist API (ADR-0043 Tier A) — the durable replacement for fuzzy
/// title-matching against our own catalog. Strategy: exact slug lookup first
/// (the article slug usually equals the Kineticist game slug), then a guarded
/// title-search fallback for the messy cases (abbreviations, "how-to-play-"
/// prefixes, editorial slugs).
/// </summary>
public interface IKineticistGameResolver
{
    /// <summary>
    /// Resolves the OPDB editions for a tutorial. Returns <see langword="null"/>
    /// when the game cannot be confidently identified (the tutorial is then
    /// skipped + logged — degrade visibly, never mis-link).
    /// </summary>
    Task<KineticistGameMatch?> ResolveAsync(string gameSlug, string articleTitle, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IKineticistGameResolver"/>
public sealed class KineticistGameResolver : IKineticistGameResolver
{
    private readonly IKineticistApiClient _api;
    private readonly ILogger<KineticistGameResolver> _logger;

    // Editorial/filler tokens that appear in tutorial slugs but never in a
    // game's name (e.g. "how-to-play-mata-hari-pinball", "eight-ball-deluxe-
    // rules-strategy"). Stripped before building the search query.
    private static readonly HashSet<string> NoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "how", "to", "play", "learn", "learning", "master", "mastering", "guide",
        "tutorial", "rules", "rule", "strategy", "strategies", "pinball", "the",
        "a", "an", "of", "and", "your", "ultimate", "complete", "detailed",
    };

    public KineticistGameResolver(IKineticistApiClient api, ILogger<KineticistGameResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(logger);
        _api = api;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<KineticistGameMatch?> ResolveAsync(string gameSlug, string articleTitle, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameSlug);

        // 1. Exact slug lookup — the common, safe path.
        var direct = await _api.GetGameBySlugAsync(gameSlug, cancellationToken).ConfigureAwait(false);
        if (direct is not null)
        {
            _logger.LogDebug("Kineticist resolve: slug '{Slug}' -> '{Name}' ({Editions} edition(s)).",
                gameSlug, direct.Name, direct.EditionOpdbIds.Count);
            return direct;
        }

        // 2. Guarded title-search fallback for messy slugs.
        var query = BuildSearchQuery(gameSlug);
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var hits = await _api.SearchGamesAsync(query, limit: 5, cancellationToken).ConfigureAwait(false);
        var queryTokens = Tokenize(query);

        // Accept the first hit that shares a meaningful token with the query —
        // the API's ?q= is relevance-ranked, but the overlap guard prevents a
        // weak top hit from producing a wrong (mis-grounded) link.
        foreach (var hit in hits)
        {
            if (!ShareSignificantToken(queryTokens, Tokenize(hit.Name)))
            {
                continue;
            }

            var resolved = await _api.GetGameBySlugAsync(hit.Slug, cancellationToken).ConfigureAwait(false);
            if (resolved is not null)
            {
                _logger.LogInformation(
                    "Kineticist resolve: slug '{Slug}' missed; search '{Query}' -> '{Name}' (slug '{HitSlug}', {Editions} edition(s)).",
                    gameSlug, query, resolved.Name, hit.Slug, resolved.EditionOpdbIds.Count);
                return resolved;
            }
        }

        _logger.LogDebug("Kineticist resolve: no confident match for slug '{Slug}' (search query '{Query}', {Hits} hit(s)).",
            gameSlug, query, hits.Count);
        return null;
    }

    // Strips noise tokens from a hyphenated slug to form a search query.
    // "how-to-play-mata-hari-pinball" -> "mata hari"; "eight-ball-deluxe-rules-strategy" -> "eight ball deluxe".
    internal static string BuildSearchQuery(string slug)
    {
        var kept = slug
            .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !NoiseTokens.Contains(t));
        return string.Join(' ', kept).Trim();
    }

    private static HashSet<string> Tokenize(string value) =>
        new(value.Split([' ', '-', '/', ':', '’', '\''], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Where(t => !NoiseTokens.Contains(t)),
            StringComparer.OrdinalIgnoreCase);

    // True when the query and candidate share at least one non-noise token of
    // length >= 2 — confirms the search hit is on-topic without blocking short
    // real names like AC/DC ("ac","dc") or T2 ("t2"). Noise/stopwords are
    // already filtered out by Tokenize, so a match here is meaningful.
    private static bool ShareSignificantToken(HashSet<string> queryTokens, HashSet<string> candidateTokens) =>
        queryTokens.Any(t => t.Length >= 2 && candidateTokens.Contains(t));
}
