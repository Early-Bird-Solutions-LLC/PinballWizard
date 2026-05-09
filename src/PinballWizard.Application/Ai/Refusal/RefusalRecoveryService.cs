using Microsoft.Extensions.Logging;
using PinballWizard.Application.Ai.Refusal;
using PinballWizard.Application.Ai.Tools;
using PinballWizard.Application.Persistence;

namespace PinballWizard.Application.Ai;

// Wave 2 PR-R2/R3 implementation of IRefusalRecoveryService per ADR-0026 § 4.
//
// PR-R2: Token-overlap algorithm for RelatedMachines:
//   1. Tokenize normalizedQuestion via MachineGroundingTool.TokenizeForOverlap
//      (stop-words filtered, length-1 tokens dropped, lowercased).
//   2. For each meaningful token, query IMachineRepository.QueryByTitleAsync.
//      The repository performs a case-insensitive substring / equality scan;
//      any machines returned are candidate matches.
//   3. Deduplicate candidates by Machine.Id; accumulate an overlap count for
//      each (one increment per token that returned that machine).
//   4. Sort by overlap count descending, take up to MaxRelatedMachines (3).
//   5. Map to RelatedMachine records using Machine.Id, Machine.Title,
//      Machine.OpdbSourceUrl.
//
// PR-R3: Per-category CommunityResources routing from ICommunityResourceLoader:
//   OutOfScope            → marketplace + machine_reference + manufacturer_pages
//   InsufficientGrounding → forums + machine_reference + news_and_culture
//   LowModelConfidence    → forums + machine_reference
//   NoCitation            → forums + machine_reference
//   UpstreamThrottled     → null (transient infra fault; no community routing)
//   CostCeilingHit        → null (operational; no community routing)
//   HarmfulContent        → null (safety block; no community routing)
//
// Up to MaxCommunityCardsTotal community resource cards are returned per
// refusal. Within each included category, cards are alphabetically ordered
// by the loader (no favoritism) and capped at MaxCardsPerCategory.
//
// The service is best-effort: a repository or loader exception logs at Warning
// and returns null so the primary refusal is never blocked.
// OperationCanceledException propagates normally (callers honour cancellation).
public sealed class RefusalRecoveryService : IRefusalRecoveryService
{
    private const int MaxRelatedMachines = 3;

    // Total community card cap per refusal response — enough to give the user
    // actionable choices without overwhelming the RefusalPanel UI.
    private const int MaxCommunityCardsTotal = 5;

    // Per-category cap applied before the total cap to avoid any single category
    // crowding out others (e.g., 6 manufacturer pages vs 2 marketplace entries).
    private const int MaxCardsPerCategory = 3;

    private readonly IMachineRepository _machines;
    private readonly ICommunityResourceLoader _communityResources;
    private readonly ILogger<RefusalRecoveryService> _logger;

    public RefusalRecoveryService(
        IMachineRepository machines,
        ICommunityResourceLoader communityResources,
        ILogger<RefusalRecoveryService> logger)
    {
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentNullException.ThrowIfNull(communityResources);
        ArgumentNullException.ThrowIfNull(logger);
        _machines = machines;
        _communityResources = communityResources;
        _logger = logger;
    }

    // Per-category policy: only categories where related-machine suggestions
    // help the user recover (see IRefusalRecoveryService for rationale).
    private static bool CategorySupportsRelatedMachines(RefusalCategory category) =>
        category is RefusalCategory.OutOfScope
            or RefusalCategory.InsufficientGrounding
            or RefusalCategory.LowModelConfidence
            or RefusalCategory.NoCitation;

    // Per-category community resource routing (PR-R3). Returns the ordered list
    // of CommunityResourceCategory values to include for a given refusal category.
    // Empty array means "no community resources for this category."
    private static CommunityResourceCategory[] CommunityCategoriesToInclude(RefusalCategory category) =>
        category switch
        {
            // OutOfScope: user asked about something we don't have — route to buy/sell,
            // canonical databases, and direct manufacturer pages.
            RefusalCategory.OutOfScope =>
            [
                CommunityResourceCategory.Marketplace,
                CommunityResourceCategory.MachineReference,
                CommunityResourceCategory.ManufacturerPages,
            ],

            // InsufficientGrounding: retrieval found chunks but scored too low — point
            // to forums where humans can answer, reference DBs, and news for context.
            RefusalCategory.InsufficientGrounding =>
            [
                CommunityResourceCategory.Forums,
                CommunityResourceCategory.MachineReference,
                CommunityResourceCategory.NewsAndCulture,
            ],

            // LowModelConfidence: model has data but isn't confident — point to forums
            // and canonical references where the user can corroborate.
            RefusalCategory.LowModelConfidence =>
            [
                CommunityResourceCategory.Forums,
                CommunityResourceCategory.MachineReference,
            ],

            // NoCitation: answer was ungrounded — forums + references so the user can
            // find a grounded answer from the community.
            RefusalCategory.NoCitation =>
            [
                CommunityResourceCategory.Forums,
                CommunityResourceCategory.MachineReference,
            ],

            // UpstreamThrottled / CostCeilingHit / HarmfulContent: operational or
            // safety blocks — no community routing.
            _ => [],
        };

    public async Task<RefusalDetail?> BuildRecoveryAsync(
        string normalizedQuestion,
        RefusalCategory category,
        CancellationToken ct)
    {
        if (!CategorySupportsRelatedMachines(category))
            return null;

        try
        {
            var relatedMachinesTask = FindRelatedMachinesAsync(normalizedQuestion, ct);
            var communityResourcesTask = BuildCommunityResourcesAsync(category, ct);

            await Task.WhenAll(relatedMachinesTask, communityResourcesTask).ConfigureAwait(false);

            return new RefusalDetail(
                Confidence: null,
                RelatedMachines: await relatedMachinesTask,
                CommunityResources: await communityResourcesTask,
                MissingWhat: null,
                SuggestedRephrase: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "RefusalRecoveryService: lookup failed for question '{Question}' / category {Category}. Returning null (best-effort; primary refusal is unaffected).",
                normalizedQuestion, category);
            return null;
        }
    }

    private async Task<IReadOnlyList<CommunityResource>?> BuildCommunityResourcesAsync(
        RefusalCategory category,
        CancellationToken ct)
    {
        var categoriesToInclude = CommunityCategoriesToInclude(category);
        if (categoriesToInclude.Length == 0)
            return null;

        var cards = new List<CommunityResource>(MaxCommunityCardsTotal);

        foreach (var resourceCategory in categoriesToInclude)
        {
            if (cards.Count >= MaxCommunityCardsTotal)
                break;

            var categoryCards = await _communityResources
                .LoadByCategoryAsync(resourceCategory, ct)
                .ConfigureAwait(false);

            // Alphabetical ordering is enforced by the loader; take up to
            // MaxCardsPerCategory per category, then respect the global cap.
            var remaining = MaxCommunityCardsTotal - cards.Count;
            var take = Math.Min(MaxCardsPerCategory, remaining);
            cards.AddRange(categoryCards.Take(take));
        }

        return cards.Count > 0 ? cards.AsReadOnly() : null;
    }

    private async Task<IReadOnlyList<RelatedMachine>> FindRelatedMachinesAsync(
        string normalizedQuestion,
        CancellationToken ct)
    {
        var tokens = MachineGroundingTool.TokenizeForOverlap(normalizedQuestion);
        if (tokens.Count == 0)
            return [];

        // Map machine ID → overlap count.
        var overlapCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        // Cache the first Machine record seen per ID so we don't re-fetch.
        var machineById = new Dictionary<string, (string Title, string? OpdbUrl)>(StringComparer.Ordinal);

        foreach (var token in tokens)
        {
            ct.ThrowIfCancellationRequested();

            await foreach (var machine in _machines.QueryByTitleAsync(token, ct).ConfigureAwait(false))
            {
                if (!overlapCounts.TryGetValue(machine.Id, out var existing))
                {
                    overlapCounts[machine.Id] = 1;
                    machineById[machine.Id] = (machine.Title, machine.OpdbSourceUrl);
                }
                else
                {
                    overlapCounts[machine.Id] = existing + 1;
                }
            }
        }

        if (overlapCounts.Count == 0)
            return [];

        return overlapCounts
            .OrderByDescending(kv => kv.Value)
            .Take(MaxRelatedMachines)
            .Select(kv =>
            {
                var (title, opdbUrl) = machineById[kv.Key];
                return new RelatedMachine(
                    MachineId: kv.Key,
                    Title: title,
                    OpdbUrl: opdbUrl);
            })
            .ToList();
    }
}
