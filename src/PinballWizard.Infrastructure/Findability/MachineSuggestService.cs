using Microsoft.Extensions.Logging;
using PinballWizard.Application.Findability;

namespace PinballWizard.Infrastructure.Findability;

// ADR-0049 phase 3: typeahead suggest service backed by the AI Search machine
// findability index.
//
// Registered unconditionally (FindabilityServiceCollectionExtensions.AddMachineSuggestService).
// IMachineSearchIndex? is injected as null by .NET DI when AI Search is not configured —
// the service returns empty rather than failing, honoring the "degrade, don't 500" contract.
//
// EDITION COLLAPSE
//   The machine index contains one document per OPDB machine entry. A popular title like
//   "Medieval Madness" appears as Original (1998), Remake (2016), Remake Premium, Remake
//   Limited Edition, etc. Showing six identical titles in a typeahead is confusing. Collapse:
//     - hit.GroupId not null → deduplicate by GroupId (keep first / highest-scored hit).
//     - hit.GroupId null    → deduplicate by (Title, ManufacturerDisplayName) pair.
//   AI Search already returns hits in descending-score order so the first hit per group
//   is the best-ranked edition — no secondary sort needed.
//
//   Raw-hit overfetch: we request up to `top * 4` (capped at 80) raw hits from the index
//   so that after edition collapse we still fill the requested `top` suggestions in the
//   common case. This does NOT increase AI Search billing cost — one query call, one page.
//
// RANKING
//   No engagement or popularity signals — ordering is purely index-intrinsic:
//   BM25 relevance over title / title_prefix / title_phonetic + the machine-content-intrinsic
//   scoring profile (completeness magnitude + freshness). This is search, not venue routing.
internal sealed class MachineSuggestService : IMachineSuggestService
{
    // Minimum non-whitespace character count required before querying the index.
    internal const int MinNonWhitespaceChars = 2;

    // Raw-hit overfetch multiplier. In the common case (one entry per distinct machine)
    // 4× provides ample headroom; if every hit collapses (all same group, degenerate
    // query) we still return zero — the cap is correctness, not a guarantee.
    internal const int OverfetchMultiplier = 4;

    // Hard ceiling on raw hits requested per call.
    internal const int MaxRawHits = 80;

    // Separator char between title and manufacturer in the ungrouped dedup key.
    // Unit-separator (U+001F) is outside normal text, ensuring cross-field isolation.
    private const char DedupeFieldSeparator = '';

    private readonly IMachineSearchIndex? _index;
    private readonly ILogger<MachineSuggestService> _logger;

    public MachineSuggestService(
        IMachineSearchIndex? index,
        ILogger<MachineSuggestService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _index = index;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MachineSuggestion>> SuggestAsync(
        string query,
        int top,
        CancellationToken cancellationToken)
    {
        // Short-query guard: fewer than 2 non-whitespace chars → honest empty, no I/O.
        var nonWsCount = query.Count(static c => !char.IsWhiteSpace(c));
        if (nonWsCount < MinNonWhitespaceChars)
            return [];

        // Index not configured → degrade honestly to empty (invariant #17).
        if (_index is null)
        {
            _logger.LogDebug(
                "MachineSuggestService: IMachineSearchIndex is not configured — " +
                "returning empty suggestions for query '{Query}'",
                query);
            return [];
        }

        var rawTop = Math.Min(top * OverfetchMultiplier, MaxRawHits);
        var hits = await _index.SearchAsync(query, rawTop, cancellationToken).ConfigureAwait(false);

        return CollapseEditions(hits, top);
    }

    // Groups ranked hits into one suggestion per distinct machine, preserving rank order.
    // Exposed internal static so MachineSuggestServiceTests can pin the collapse logic
    // independently of the async plumbing.
    internal static IReadOnlyList<MachineSuggestion> CollapseEditions(
        IReadOnlyList<MachineSearchHit> hits,
        int top)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<MachineSuggestion>(capacity: Math.Min(hits.Count, top));

        foreach (var hit in hits)
        {
            if (results.Count >= top)
                break;

            // GroupId-first dedup: machines sharing an OPDB group (editions of the
            // same title) collapse to one entry. Ungrouped machines dedup by title
            // + manufacturer to handle OPDB entries that lack a group assignment.
            var dedupeKey = hit.GroupId is not null
                ? $"group:{hit.GroupId}"
                : $"machine:{hit.Title}{DedupeFieldSeparator}{hit.ManufacturerDisplayName}";

            if (!seen.Add(dedupeKey))
                continue;

            results.Add(new MachineSuggestion(
                OpdbId: hit.OpdbId,
                Title: hit.Title,
                Manufacturer: hit.ManufacturerDisplayName,
                Year: hit.Year));
        }

        return results;
    }
}
