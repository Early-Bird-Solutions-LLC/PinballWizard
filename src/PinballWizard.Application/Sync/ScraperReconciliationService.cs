using Microsoft.Extensions.Logging;
using PinballWizard.Application.Linking;
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
        var matchedByGroup = 0;
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

            var (matches, matchedVia) = FindMatch(partition, manufacturer, game);

            if (matches.Count == 0)
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

            // Apply to every matched base machine. For Slug/Title this is one
            // machine; for Group it is the whole edition family.
            foreach (var match in matches)
            {
                ApplyScraperFields(match, game, manufacturer, now);
                await _repository.UpsertAsync(match, cancellationToken).ConfigureAwait(false);
                upserts++;
            }

            switch (matchedVia)
            {
                case MatchOutcome.Slug: matchedBySlug++; break;
                case MatchOutcome.Title: matchedByTitle++; break;
                case MatchOutcome.Group: matchedByGroup++; break;
            }
        }

        _logger.LogInformation(
            "Reconciler complete: considered={Considered} slug-matched={Slug} title-matched={Title} group-matched={Group} unmatched={Unmatched} ambiguous={Ambiguous} failed={Failed} upserts={Upserts}",
            considered, matchedBySlug, matchedByTitle, matchedByGroup, unmatched, ambiguous, failedMapping, upserts);

        return new ScraperReconciliationResult
        {
            Considered = considered,
            MatchedBySlug = matchedBySlug,
            MatchedByTitle = matchedByTitle,
            MatchedByGroup = matchedByGroup,
            Unmatched = unmatched,
            AmbiguousTitle = ambiguous,
            FailedMapping = failedMapping,
            Upserts = upserts,
        };
    }

    /// <inheritdoc />
    public async Task<SlugBackfillResult> BackfillSlugsFromCrossReferencesAsync(
        IAsyncEnumerable<RawDocumentRecord> rawDocuments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rawDocuments);

        // Distinct (manufacturer, slug) candidates from provenance already
        // captured in scraped_documents_raw — no scraping, no HTTP calls.
        var candidates = new HashSet<(string Manufacturer, string Slug)>();

        await foreach (var raw in rawDocuments.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var mfrKey = LinkingUtilities.InferManufacturerKey(raw.Source);
            if (mfrKey is null) continue;

            foreach (var xref in raw.CrossReferences)
            {
                var slug = LinkingUtilities.ExtractGameSlugFromUrl(xref.AlsoFoundAt);
                if (slug is { Length: > 0 })
                {
                    candidates.Add((mfrKey, slug));
                }
            }
        }

        var partitionCache = new Dictionary<string, List<Machine>>(StringComparer.OrdinalIgnoreCase);
        var alreadyPresent = 0;
        var matchedSingle = 0;
        var matchedGroup = 0;
        var unmatched = 0;
        var ambiguous = 0;
        var upserts = 0;

        foreach (var (manufacturer, slug) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var partition = await GetOrLoadPartitionAsync(manufacturer, partitionCache, cancellationToken)
                .ConfigureAwait(false);

            if (partition.Any(m => m.ManufacturerSlugs.TryGetValue(manufacturer, out var existing)
                && string.Equals(existing, slug, StringComparison.OrdinalIgnoreCase)))
            {
                alreadyPresent++;
                continue;
            }

            var target = NormalizeFranchiseTitle(slug);
            var matches = target.Length == 0
                ? []
                : partition.Where(m => NormalizeFranchiseTitle(m.Title) == target).ToList();

            if (matches.Count == 0)
            {
                _logger.LogInformation(
                    "SlugBackfill: cross-reference slug '{Slug}' (manufacturer={Manufacturer}) matches no known Machine title; OPDB may not have this machine yet.",
                    slug, manufacturer);
                unmatched++;
                continue;
            }

            if (matches.Count == 1)
            {
                matches[0].ManufacturerSlugs[manufacturer] = slug;
                await _repository.UpsertAsync(matches[0], cancellationToken).ConfigureAwait(false);
                matchedSingle++;
                upserts++;
                continue;
            }

            if (IsEditionFamilyByGroup(matches))
            {
                foreach (var machine in matches)
                {
                    machine.ManufacturerSlugs[manufacturer] = slug;
                    await _repository.UpsertAsync(machine, cancellationToken).ConfigureAwait(false);
                    upserts++;
                }
                matchedGroup++;
                continue;
            }

            _logger.LogWarning(
                "SlugBackfill: cross-reference slug '{Slug}' (manufacturer={Manufacturer}) matches multiple Machines that are NOT a single edition family; manual triage required. Candidates: {Candidates}",
                slug, manufacturer,
                string.Join(", ", matches.Select(m => $"{m.Id}(group={m.GroupId ?? "null"})")));
            ambiguous++;
        }

        _logger.LogInformation(
            "SlugBackfill complete: candidates={Candidates} alreadyPresent={AlreadyPresent} matchedSingle={MatchedSingle} matchedGroup={MatchedGroup} unmatched={Unmatched} ambiguous={Ambiguous} upserts={Upserts}",
            candidates.Count, alreadyPresent, matchedSingle, matchedGroup, unmatched, ambiguous, upserts);

        return new SlugBackfillResult
        {
            CandidatesConsidered = candidates.Count,
            AlreadyPresent = alreadyPresent,
            MatchedSingle = matchedSingle,
            MatchedGroup = matchedGroup,
            Unmatched = unmatched,
            Ambiguous = ambiguous,
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

    // Returns the matched machine(s) and how they were matched:
    //   single via slug fast path        → ([m], Slug)
    //   single via title-normalize       → ([m], Title)
    //   multiple sharing one GroupId      → (all, Group)   — an edition family
    //   multiple across different groups  → ([],  Ambiguous) — genuinely unrelated
    private (List<Machine> Machines, MatchOutcome Via) FindMatch(
        List<Machine> partition, string manufacturer, GameRecord game)
    {
        // Pass 1: slug fast path (single machine).
        foreach (var machine in partition)
        {
            if (machine.ManufacturerSlugs.TryGetValue(manufacturer, out var existingSlug)
                && string.Equals(existingSlug, game.Slug, StringComparison.OrdinalIgnoreCase))
            {
                return ([machine], MatchOutcome.Slug);
            }
        }

        // Pass 2: franchise-title match. The scraped game title is the bare
        // franchise ("Godzilla"); OPDB base titles are edition-qualified
        // ("Godzilla (Pro)", "Godzilla (Premium/LE)"). Compare on the FRANCHISE
        // title — the normalized title with any trailing "(…)" edition
        // parenthetical removed — so the scraped game matches every edition base.
        // Bootstraps the slug map on the first run; Pass 1 wins thereafter.
        var scrapedFranchise = NormalizeFranchiseTitle(game.Title);
        if (scrapedFranchise.Length == 0) return ([], MatchOutcome.None);

        var matches = partition
            .Where(m => NormalizeFranchiseTitle(m.Title) == scrapedFranchise)
            .ToList();

        if (matches.Count == 0) return ([], MatchOutcome.None);
        if (matches.Count == 1) return (matches, MatchOutcome.Title);

        // Multiple franchise-title matches. This is an EDITION FAMILY — not a
        // true ambiguity — when the matches all share one OPDB group segment.
        // Example: Godzilla Pro (GweeP-MW95j) + Premium/LE (GweeP-Ml9pZ),
        // both group "GweeP". The group segment is the OPDB family identifier
        // within a manufacturer partition, reliably capturing all editions of
        // the same franchise.
        //
        // Year is intentionally NOT part of this check for the reconciler.
        // The scraper's slug is franchise-level (e.g. "medieval-madness" covers
        // every CGC Medieval Madness edition regardless of release year), so
        // same-GroupId machines from different years (2015 Remake vs 2021 Cosmic
        // Edition) SHOULD all receive the same slug. A year guard here caused
        // those cross-year families to be classified as Ambiguous and left with
        // empty ManufacturerSlugs (issue #655 Gap 1).
        //
        // Genuine "same title, different franchise" cases (e.g. Big Ben 1954
        // vs 1975) have DIFFERENT OPDB group segments ("G5QBX" vs "GRBo3") so
        // the segment count check rejects them correctly without needing the
        // year to discriminate. DocumentLinker.IsEditionFamily used to retain a
        // year guard for document-linking's edition resolution, but that only
        // blocked EditionResolver from ever running against cross-year families
        // (e.g. AC/DC 2012 vs. its 2017 Vault Edition reissue) — it now also
        // uses this same GroupId-only check (issue #677).
        if (IsEditionFamilyByGroup(matches))
        {
            return (matches, MatchOutcome.Group);
        }

        _logger.LogWarning(
            "Reconciler: scraped {GameId} ('{Title}') matches multiple Machines that are NOT a single edition family (segments/years differ); manual triage required. Candidates: {Candidates}",
            game.GameId, game.Title,
            string.Join(", ", matches.Select(m => $"{m.Id}(group={m.GroupId ?? "null"},year={m.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"})")));
        return ([], MatchOutcome.Ambiguous);
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

        // Overview prose + its provenance URL, trailer, and accessories are
        // scraper-owned game-page content (the manufacturer page is fresher
        // and richer than OPDB for these). Replace wholesale.
        machine.OverviewProse = game.OverviewProse;
        machine.OverviewSourceUrl = string.IsNullOrWhiteSpace(game.OverviewProse) ? null : game.GamePageUrl;
        machine.TrailerUrl = game.TrailerUrl;
        machine.Accessories = game.Accessories
            .Select(a => new MachineAccessory { Name = a.Name, Price = a.Price, ProductUrl = a.ProductUrl, ImageUrl = a.ImageUrl })
            .ToList();

        machine.LastSeenAt = now;

        // Enrich year from scraper when OPDB did not provide one (most post-2019 Stern machines).
        // ReleaseYear comes from JSON-LD datePublished; we prefer it over null, not over OPDB data.
        if (machine.Year is null && game.ReleaseYear is not null)
            machine.Year = game.ReleaseYear;
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

    /// <summary>
    /// Franchise title for cross-record matching: the title with any trailing
    /// "(…)" edition parenthetical removed, then <see cref="NormalizeTitle"/>
    /// applied. "Godzilla (Pro)" and "Godzilla (Premium/LE)" both reduce to
    /// "godzilla", so a scraped bare-franchise game ("Godzilla") matches every
    /// edition base. A title with no parenthetical is normalized unchanged.
    /// </summary>
    public static string NormalizeFranchiseTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var trimmed = title.TrimEnd();
        // Strip a single trailing parenthetical group (the edition marker).
        var open = trimmed.LastIndexOf('(');
        if (open > 0 && trimmed.EndsWith(')'))
        {
            trimmed = trimmed[..open];
        }
        return NormalizeTitle(trimmed);
    }

    // Shared by FindMatch's Pass 2 and BackfillSlugsFromCrossReferencesAsync:
    // multiple same-franchise-title matches are an edition family (not a true
    // ambiguity) when they all share one non-null OPDB group segment. See the
    // year-guard rationale in FindMatch (issue #655 Gap 1).
    private static bool IsEditionFamilyByGroup(List<Machine> matches) => EditionFamily.IsEditionFamilyByGroup(matches);

    private enum MatchOutcome { None, Slug, Title, Group, Ambiguous }
}
