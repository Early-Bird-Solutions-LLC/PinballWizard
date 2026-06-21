using Microsoft.Extensions.Logging;
using PinballWizard.Application.Ai.Refusal;
using PinballWizard.Application.Ai.Tools;
using PinballWizard.Application.Observability;
using PinballWizard.Application.Persistence;

namespace PinballWizard.Application.Ai;

// Wave 2 PR-R2/R3/R4 implementation of IRefusalRecoveryService per ADR-0026 § 4.
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
// PR-R4: Per-category MissingWhat + SuggestedRephrase text strategies.
//   Every content-miss category (OutOfScope, InsufficientGrounding,
//   LowModelConfidence, NoCitation) gets both fields populated with helpful,
//   honest, non-blaming prose per ADR-0026 § 5.
//   UpstreamThrottled gets MissingWhat (system-state) but null SuggestedRephrase
//   (transient; rephrase wouldn't help — retry after the rate-limit clears).
//   CostCeilingHit gets neither (operational; user shouldn't act on this).
//   HarmfulContent gets neither (safety block; adding suggestions would
//   undermine the refusal posture).
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

    // ──────────────────────────────────────────────────────────────────────────
    // PR-R4: Per-category MissingWhat / SuggestedRephrase const strings.
    //
    // Tone requirements (ADR-0026 § 5 + feedback_community_resource_posture.md):
    //   - Specific: tell the user WHAT is missing, not just "something went wrong."
    //   - Honest: do not over-promise; if an answer isn't available, say so plainly.
    //   - Polite: do not blame the user for asking.
    //   - Action-oriented: where possible, suggest the next step.
    //   - Brief: ≤ 2 sentences per field.
    //
    // The OutOfScope topic list is sourced from the canonical Wizard agent prompt
    // (src/PinballWizard.Application/Ai/Agents/Wizard.md): pinball machine rules,
    // repair, and valuations — grounded via manufacturer documentation and the
    // OPDB catalog.
    //
    // `internal` so the test project (InternalsVisibleTo) can verify the exact
    // strings in per-category behavioral tests without reflection.
    // ──────────────────────────────────────────────────────────────────────────

    internal const string MissingWhat_OutOfScope =
        "PinballWizard covers pinball machine rules, repair procedures, and valuations, grounded in manufacturer documentation and the OPDB catalog. " +
        "This question goes outside that scope.";

    internal const string SuggestedRephrase_OutOfScope =
        "Try asking about a specific machine — for example: \"What are the rules for Godzilla multiball?\" or \"How do I fix a flipping coil on Medieval Madness?\"";

    internal const string MissingWhat_InsufficientGrounding =
        "The indexed manuals and service bulletins don't contain enough detail to answer this confidently. " +
        "The answer may exist in sources not yet in the corpus.";

    internal const string SuggestedRephrase_InsufficientGrounding =
        "Try naming a specific manufacturer and machine — for example: \"Stern Godzilla multiball rules\" or \"Jersey Jack Wonka ramp shot.\"";

    internal const string MissingWhat_LowModelConfidence =
        "The available sources have relevant content, but confidence in a reliable answer is too low to share it. " +
        "Naming the exact machine and topic may help.";

    internal const string SuggestedRephrase_LowModelConfidence =
        "Try a more specific question such as \"How does [machine name] score the [specific mode]?\" so the answer can be grounded precisely.";

    internal const string MissingWhat_NoCitation =
        "No indexed source could be linked to back up an answer here. " +
        "A citationless answer could mislead — the community forums below may have a grounded response.";

    internal const string SuggestedRephrase_NoCitation =
        "Try asking about a topic that appears in manufacturer documentation — rules, repair procedures, or machine specifications are most reliably sourced.";

    internal const string MissingWhat_UpstreamThrottled =
        "The AI service is temporarily rate-limited. This is a transient condition — no content is missing from the corpus.";

    // SuggestedRephrase is intentionally null for UpstreamThrottled: the right
    // action is to wait and retry, not to rephrase. Surfacing a rephrase
    // suggestion would imply the question was the problem, which it wasn't.

    // MissingWhat and SuggestedRephrase are both null for CostCeilingHit and
    // HarmfulContent: operational / safety blocks where user action is not
    // meaningful. CostCeilingHit is an infrastructure limit; HarmfulContent is
    // a safety decision. Neither benefits from user-facing guidance here.

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

    // PR-R4: Per-category text strategy. Returns (MissingWhat, SuggestedRephrase)
    // for the given category. Either field may be null when no user-actionable
    // text applies (UpstreamThrottled, CostCeilingHit, HarmfulContent).
    private static (string? MissingWhat, string? SuggestedRephrase) BuildText(RefusalCategory category) =>
        category switch
        {
            RefusalCategory.OutOfScope =>
                (MissingWhat_OutOfScope, SuggestedRephrase_OutOfScope),

            RefusalCategory.InsufficientGrounding =>
                (MissingWhat_InsufficientGrounding, SuggestedRephrase_InsufficientGrounding),

            RefusalCategory.LowModelConfidence =>
                (MissingWhat_LowModelConfidence, SuggestedRephrase_LowModelConfidence),

            RefusalCategory.NoCitation =>
                (MissingWhat_NoCitation, SuggestedRephrase_NoCitation),

            // Transient rate-limit: system-state explanation is helpful,
            // but rephrase is not (the question itself isn't the issue).
            RefusalCategory.UpstreamThrottled =>
                (MissingWhat_UpstreamThrottled, null),

            // Operational / safety blocks: no user-facing guidance.
            _ => (null, null),
        };

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
        // PR-R4: Compute per-category text first. Even categories that don't
        // support machine lookups (UpstreamThrottled) may have a MissingWhat
        // explanation worth surfacing. CostCeilingHit and HarmfulContent return
        // (null, null) so the whole detail remains null — no recovery value added.
        var (missingWhat, suggestedRephrase) = BuildText(category);

        var supportsLookups = CategorySupportsRelatedMachines(category);

        // If there's nothing to populate at all (no text, no lookups), return
        // null so the caller emits the bare refusal without an empty recovery shell.
        if (!supportsLookups && missingWhat is null)
            return null;

        if (!supportsLookups)
        {
            // Text-only recovery (e.g., UpstreamThrottled). No machine lookups
            // or community resource routing — those would be misleading for a
            // transient infrastructure failure.
            return new RefusalDetail(
                Confidence: null,
                RelatedMachines: null,
                CommunityResources: null,
                MissingWhat: missingWhat,
                SuggestedRephrase: suggestedRephrase);
        }

        try
        {
            var relatedMachinesTask = FindRelatedMachinesAsync(normalizedQuestion, ct);
            var communityResourcesTask = BuildCommunityResourcesAsync(category, ct);

            await Task.WhenAll(relatedMachinesTask, communityResourcesTask).ConfigureAwait(false);

            return new RefusalDetail(
                Confidence: null,
                RelatedMachines: await relatedMachinesTask,
                CommunityResources: await communityResourcesTask,
                MissingWhat: missingWhat,
                SuggestedRephrase: suggestedRephrase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // OBS-01 / invariant #17: degrade visibly — log at Error (not Warning)
            // so operators know community CTAs are absent. The primary refusal is
            // never blocked (best-effort posture per the class-level comment), but
            // this failure is not routine and warrants an alert.
            var reason = ex switch
            {
                FileNotFoundException => "FileNotFoundException",
                InvalidOperationException => "InvalidOperationException",
                _ => "other",
            };

            PinballWizardTelemetry.AiCommunityResourcesLoadErrors.Add(1,
                new KeyValuePair<string, object?>("reason", reason));

            _logger.LogError(ex,
                "RefusalRecoveryService: community-resource or machine lookup failed for question '{Question}' / category {Category}. " +
                "Refusal panel will render without community CTAs (pinwiz.ai.community_resources_load_errors_total incremented). " +
                "Primary refusal is unaffected — this is best-effort enrichment.",
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
