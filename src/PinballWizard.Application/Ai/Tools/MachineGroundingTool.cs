using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Observability;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;

namespace PinballWizard.Application.Ai.Tools;

// Foundry function tool exposed to all four Wave-2 agents per ADR-0014.
// The Microsoft Agent Framework's AIFunctionFactory.Create wraps the
// GetMachineByTitleAsync method into an AIFunction that the agent can
// invoke on demand; [Description] attributes flow into the JSON-Schema
// the model sees, so the agent prompt does not need to repeat the
// argument shape.
//
// PR 5 of the Cosmos for User Delight track (ADR-0025 § 4) replaced
// the cross-partition `STRINGEQUALS` query in this tool with a two-
// point-read path: first the `machine_title_lookups` materialized
// view to resolve title → (opdb_id, manufacturer), then the `machines`
// container to fetch the actual record. The original cross-partition
// query survives as a logged-warning fallback for the unmigrated-
// lookup case (post-deploy backfill pending, transient lookup-write
// failure during OPDB sync, etc.) so the tool degrades gracefully
// rather than refusing to answer.
public sealed class MachineGroundingTool
{
    private readonly IMachineRepository _machines;
    private readonly IMachineTitleLookupRepository _titleLookups;
    private readonly ILogger<MachineGroundingTool> _logger;

    public MachineGroundingTool(
        IMachineRepository machines,
        IMachineTitleLookupRepository titleLookups,
        ILogger<MachineGroundingTool> logger)
    {
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentNullException.ThrowIfNull(titleLookups);
        ArgumentNullException.ThrowIfNull(logger);
        _machines = machines;
        _titleLookups = titleLookups;
        _logger = logger;
    }

    // Tool tag value emitted on `pinwiz.ai.tool_duration_ms` and (when the
    // tool gains a catch boundary) `pinwiz.ai.tool_errors_total`. Matches
    // the JSON-Schema function name the Microsoft Agent Framework derives
    // from this method, so dashboards and prompts agree on the label.
    internal const string ToolTagValue = "getMachineByTitle";

    [Description("Look up a pinball machine by its title (case-insensitive). Returns the manufacturer, year, themes, designers, editions, and OPDB source URL — everything you need to ground an answer about that machine. Returns null if no machine matches the title.")]
    public async Task<MachineGroundingDto?> GetMachineByTitleAsync(
        [Description("The pinball-machine title to look up, case-insensitive (for example: 'Foo Fighters', 'Stranger Things', 'Godzilla').")] string title,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            // Point-read path per ADR-0025 § 4 — `machine_title_lookups`
            // resolves title → (opdb_id, manufacturer), then `machines`
            // is point-read by (id, partitionKey). Two ~5ms reads vs the
            // pre-PR-5 ~50-150ms cross-partition query.
            var lookup = await _titleLookups.GetByTitleAsync(title, cancellationToken).ConfigureAwait(false);
            Machine? match = null;
            var lookupHit = lookup is not null && lookup.OpdbIds.Count > 0;

            if (lookupHit)
            {
                // First entry on the lookup row matches the existing
                // first-OPDB-ordered-hit semantics. Title collisions
                // (multiple machines with the same normalized title)
                // are resolved by insertion order; PR 6 of the
                // delight-or-future-track may surface ambiguity to the
                // agent if eval data warrants.
                var opdbId = lookup!.OpdbIds[0];
                var manufacturer = lookup.Manufacturers[0];
                match = await _machines.GetByOpdbIdAsync(opdbId, manufacturer, cancellationToken).ConfigureAwait(false);

                if (match is null)
                {
                    // Stale lookup — the row points at a machine that no
                    // longer exists in the `machines` container. Falls
                    // through to the cross-partition fallback below;
                    // logged at warning so the gap surfaces. Self-corrects
                    // on the next OPDB sync (the writer either updates
                    // or removes the lookup row when it processes the
                    // missing machine's id).
                    _logger.LogWarning(
                        "MachineGroundingTool: lookup '{Title}' pointed at opdb_id '{OpdbId}' / manufacturer '{Manufacturer}' but the machine row is missing. Falling back to cross-partition QueryByTitleAsync. Stale lookup will self-correct on the next OPDB sync.",
                        title, opdbId, manufacturer);
                }
            }

            if (match is null)
            {
                // Cross-partition fallback — the pre-PR-5 path. Retained
                // so the tool degrades gracefully when:
                //   • The lookup container hasn't been backfilled yet
                //     (post-deploy, before the first OPDB sync runs).
                //   • A transient lookup-write failure during OPDB sync
                //     left a gap.
                //   • A title-rename happened between the lookup read
                //     and the machine read (rare race).
                // If a fallback fires AND the lookup row was missing
                // entirely (not stale), log a warning so operators see
                // the gap and can decide whether to re-run the OPDB
                // sync. A stale lookup (lookupHit=true but machine row
                // gone) was already logged above — don't double-log.
                await foreach (var machine in _machines.QueryByTitleAsync(title, cancellationToken).ConfigureAwait(false))
                {
                    match = machine;
                    break;
                }

                if (match is not null && !lookupHit)
                {
                    _logger.LogWarning(
                        "MachineGroundingTool: title '{Title}' resolved via fallback cross-partition query because the lookup row is missing. Operator action: confirm OPDB sync has run since the last deploy. The lookup will populate on the next sync.",
                        title);
                }
            }

            if (match is null)
            {
                _logger.LogDebug("MachineGroundingTool: no match for title '{Title}'.", title);
                return null;
            }

            var editions = match.Editions
                .Select(e => new MachineEditionGroundingDto(
                    Name: e.Name,
                    Msrp: e.Msrp,
                    Availability: e.Availability,
                    Description: e.Description))
                .ToList();

            return new MachineGroundingDto(
                OpdbId: match.Id,
                Title: match.Title,
                Manufacturer: match.ManufacturerDisplayName,
                Year: match.Year,
                Themes: match.Themes.AsReadOnly(),
                Designers: match.Designers.AsReadOnly(),
                OpdbSourceUrl: match.OpdbSourceUrl,
                Editions: editions);
        }
        finally
        {
            stopwatch.Stop();
            PinballWizardTelemetry.AiToolDurationMs.Record(
                stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("tool", ToolTagValue));
        }
    }
}
