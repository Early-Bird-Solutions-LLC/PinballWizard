using Microsoft.Extensions.Logging;
using PinballWizard.Application.Ai.Tools;
using PinballWizard.Application.Persistence;

namespace PinballWizard.Application.Ai;

// Wave 2 PR-R2 implementation of IRefusalRecoveryService per ADR-0026 § 4.
//
// Token-overlap algorithm:
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
// The service is best-effort: a repository exception logs at Warning and
// returns null so the primary refusal is never blocked. OperationCanceledException
// propagates normally (callers honour cancellation).
public sealed class RefusalRecoveryService : IRefusalRecoveryService
{
    private const int MaxRelatedMachines = 3;

    private readonly IMachineRepository _machines;
    private readonly ILogger<RefusalRecoveryService> _logger;

    public RefusalRecoveryService(
        IMachineRepository machines,
        ILogger<RefusalRecoveryService> logger)
    {
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentNullException.ThrowIfNull(logger);
        _machines = machines;
        _logger = logger;
    }

    // Per-category policy: only categories where related-machine suggestions
    // help the user recover (see IRefusalRecoveryService for rationale).
    private static bool CategorySupportsRelatedMachines(RefusalCategory category) =>
        category is RefusalCategory.OutOfScope
            or RefusalCategory.InsufficientGrounding
            or RefusalCategory.LowModelConfidence
            or RefusalCategory.NoCitation;

    public async Task<RefusalDetail?> BuildRecoveryAsync(
        string normalizedQuestion,
        RefusalCategory category,
        CancellationToken ct)
    {
        if (!CategorySupportsRelatedMachines(category))
            return null;

        try
        {
            var relatedMachines = await FindRelatedMachinesAsync(normalizedQuestion, ct)
                .ConfigureAwait(false);

            return new RefusalDetail(
                Confidence: null,
                RelatedMachines: relatedMachines,
                CommunityResources: null,
                MissingWhat: null,
                SuggestedRephrase: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "RefusalRecoveryService: repository lookup failed for question '{Question}' / category {Category}. Returning null (best-effort; primary refusal is unaffected).",
                normalizedQuestion, category);
            return null;
        }
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
