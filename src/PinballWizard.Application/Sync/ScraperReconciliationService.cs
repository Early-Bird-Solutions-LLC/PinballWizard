using Microsoft.Extensions.Logging;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;
using PinballWizard.Core.Models;

namespace PinballWizard.Application.Sync;

/// <summary>
/// Default <see cref="IScraperReconciliationService"/>. Walks each
/// <see cref="GameRecord"/>, derives its manufacturer key, looks the
/// matching <see cref="Machine"/> up in the repository, merges
/// scraper-owned fields, and upserts. Two-pass match (slug fast path
/// → title-normalize fallback) per ADR 0011.
/// </summary>
public sealed class ScraperReconciliationService : IScraperReconciliationService
{
    private readonly IMachineRepository _repository;
    private readonly TimeProvider _clock;
    private readonly ILogger<ScraperReconciliationService> _logger;

    /// <summary>Initializes a new <see cref="ScraperReconciliationService"/>.</summary>
    public ScraperReconciliationService(
        IMachineRepository repository,
        TimeProvider clock,
        ILogger<ScraperReconciliationService> logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _repository = repository;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ScraperReconciliationResult> ReconcileAsync(
        GameCatalog gameCatalog,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gameCatalog);

        var now = _clock.GetUtcNow();
        var considered = 0;
        var matchedBySlug = 0;
        var matchedByTitle = 0;
        var unmatched = 0;
        var ambiguous = 0;
        var failedMapping = 0;
        var upserts = 0;

        // Cache machines per manufacturer partition for the duration of the
        // run — we'd otherwise re-stream the partition on every record.
        var partitionCache = new Dictionary<string, List<Machine>>(StringComparer.OrdinalIgnoreCase);

        foreach (var game in gameCatalog.Games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            considered++;

            var manufacturer = ScraperManufacturerKey.FromGameId(game.GameId);
            if (manufacturer is null)
            {
                _logger.LogWarning(
                    "Reconciler: GameRecord {GameId} has no recognisable manufacturer prefix; skipping.",
                    game.GameId);
                failedMapping++;
                continue;
            }

            var partition = await GetOrLoadPartitionAsync(manufacturer, partitionCache, cancellationToken)
                .ConfigureAwait(false);

            var (match, matchedVia) = FindMatch(partition, manufacturer, game);

            if (match is null)
            {
                if (matchedVia == MatchOutcome.Ambiguous)
                {
                    ambiguous++;
                }
                else
                {
                    _logger.LogWarning(
                        "Reconciler: no Machine matched scraped {GameId} (slug='{Slug}', title='{Title}', manufacturer='{Manufacturer}'). OPDB may not have this machine yet.",
                        game.GameId, game.Slug, game.Title, manufacturer);
                    unmatched++;
                }
                continue;
            }

            ApplyScraperFields(match, game, manufacturer, now);

            await _repository.UpsertAsync(match, cancellationToken).ConfigureAwait(false);
            upserts++;

            if (matchedVia == MatchOutcome.Slug) matchedBySlug++;
            else matchedByTitle++;
        }

        _logger.LogInformation(
            "Reconciler complete: considered={Considered} slug-matched={Slug} title-matched={Title} unmatched={Unmatched} ambiguous={Ambiguous} failed={Failed} upserts={Upserts}",
            considered, matchedBySlug, matchedByTitle, unmatched, ambiguous, failedMapping, upserts);

        return new ScraperReconciliationResult
        {
            Considered = considered,
            MatchedBySlug = matchedBySlug,
            MatchedByTitle = matchedByTitle,
            Unmatched = unmatched,
            AmbiguousTitle = ambiguous,
            FailedMapping = failedMapping,
            Upserts = upserts,
        };
    }

    private async Task<List<Machine>> GetOrLoadPartitionAsync(
        string manufacturer,
        Dictionary<string, List<Machine>> cache,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(manufacturer, out var cached)) return cached;

        var list = new List<Machine>();
        await foreach (var machine in _repository.StreamByManufacturerAsync(manufacturer, cancellationToken)
            .ConfigureAwait(false))
        {
            list.Add(machine);
        }
        cache[manufacturer] = list;
        return list;
    }

    private (Machine? Machine, MatchOutcome Via) FindMatch(
        List<Machine> partition, string manufacturer, GameRecord game)
    {
        // Pass 1: slug fast path.
        foreach (var machine in partition)
        {
            if (machine.ManufacturerSlugs.TryGetValue(manufacturer, out var existingSlug)
                && string.Equals(existingSlug, game.Slug, StringComparison.OrdinalIgnoreCase))
            {
                return (machine, MatchOutcome.Slug);
            }
        }

        // Pass 2: title-normalize fallback. Bootstraps the slug map on the
        // first run, after which Pass 1 always wins.
        var normalizedScraped = NormalizeTitle(game.Title);
        if (normalizedScraped.Length == 0) return (null, MatchOutcome.None);

        Machine? candidate = null;
        var matchCount = 0;
        foreach (var machine in partition)
        {
            if (NormalizeTitle(machine.Title) == normalizedScraped)
            {
                candidate = machine;
                matchCount++;
                if (matchCount > 1) break;
            }
        }

        if (matchCount == 1) return (candidate, MatchOutcome.Title);
        if (matchCount > 1)
        {
            var ambiguousIds = partition
                .Where(m => NormalizeTitle(m.Title) == normalizedScraped)
                .Select(m => m.Id);
            _logger.LogWarning(
                "Reconciler: scraped {GameId} ('{Title}') matches multiple Machines on normalized title; manual triage required. Candidates: {Candidates}",
                game.GameId, game.Title, string.Join(", ", ambiguousIds));
            return (null, MatchOutcome.Ambiguous);
        }
        return (null, MatchOutcome.None);
    }

    private static void ApplyScraperFields(
        Machine machine, GameRecord game, string manufacturer, DateTimeOffset now)
    {
        // ManufacturerSlugs[mfg] is owned by the scraper — populate or refresh.
        machine.ManufacturerSlugs[manufacturer] = game.Slug;

        // Editions are owned by the scraper — replace wholesale (current
        // pricing/availability is fresher on the manufacturer site than
        // on OPDB).
        machine.Editions = game.Editions.Select(MapEdition).ToList();

        machine.LastSeenAt = now;
    }

    private static MachineEdition MapEdition(EditionInfo info) => new()
    {
        Name = info.Name,
        Msrp = info.Msrp,
        Availability = info.Availability,
        Description = info.Description,
        UniqueFeatures = [.. info.UniqueFeatures],
        LimitedQuantity = info.LimitedQuantity,
    };

    // Edition/format decoration tokens that manufacturer pages append but
    // OPDB titles omit — stripped from the END of the normalized title so a
    // scraped "Cactus Canyon Remake" matches the catalog "Cactus Canyon".
    // Trailing-only by design: a leading/internal occurrence is part of the
    // real title (none of these words legitimately start a pinball title).
    private static readonly string[] DecorationWords =
    {
        "remake", "pinball", "gamekit", "deposit", "limitededition",
        "merlinedition", "vaultedition", "standardedition", "edition",
    };

    /// <summary>
    /// Lowercase + strip non-alphanumeric, then remove a trailing edition/
    /// format decoration token. "Stranger Things" / "stranger things" /
    /// "Stranger Things (Pro)" collapse to "strangerthings" / "strangerthings"
    /// / "strangerthingspro"; "Cactus Canyon Remake" → "cactuscanyon". Strict
    /// enough that punctuation drift doesn't break matching, loose enough that
    /// legitimately different titles never collide.
    /// </summary>
    public static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var sb = new System.Text.StringBuilder(title.Length);
        foreach (var c in title)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        var normalized = sb.ToString();
        foreach (var decoration in DecorationWords)
        {
            if (normalized.Length > decoration.Length
                && normalized.EndsWith(decoration, StringComparison.Ordinal))
            {
                normalized = normalized[..^decoration.Length];
                break;
            }
        }
        return normalized;
    }

    private enum MatchOutcome { None, Slug, Title, Ambiguous }
}
