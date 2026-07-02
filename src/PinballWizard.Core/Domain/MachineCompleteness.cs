namespace PinballWizard.Core.Domain;

// Content-intrinsic record-quality score for a Machine. Used by
// MachineGroundingTool (ADR-0049 Phase 1) to break score ties without
// resorting to insertion order (OPDB-sync write order). Phase 2 of the
// findability program may reuse this as a catalog-health signal.
//
// Counts populated fields that are intrinsic to the machine record — not
// derived from query context or relationship data. The scale is 0..6;
// higher means richer. Every point is a field that meaningfully improves
// the Wizard's answer quality when present.
public static class MachineCompleteness
{
    // Returns a completeness score (0..6) for a single Machine record.
    // Deterministic and side-effect-free — safe to call from any layer.
    //
    // Fields scored:
    //   Year                    > 0            → +1  (known release year)
    //   Themes                  non-empty      → +1  (OPDB theme tags)
    //   Designers               non-empty      → +1  (credited designer(s))
    //   Editions                non-empty      → +1  (edition metadata)
    //   OpdbSourceUrl           non-null/empty → +1  (OPDB source link)
    //   ManufacturerDisplayName non-null/empty → +1  (human-readable name)
    //
    // ManufacturerDisplayName is a required field on Machine, so it
    // contributes a point for every well-formed record. Year is the most
    // common source of differentiation between older sparse entries (missing
    // themes/designers) and richer modern catalog entries.
    public static int Score(Machine machine)
    {
        ArgumentNullException.ThrowIfNull(machine);

        var score = 0;

        if (machine.Year > 0)
            score++;

        if (machine.Themes.Count > 0)
            score++;

        if (machine.Designers.Count > 0)
            score++;

        if (machine.Editions.Count > 0)
            score++;

        if (!string.IsNullOrEmpty(machine.OpdbSourceUrl))
            score++;

        if (!string.IsNullOrEmpty(machine.ManufacturerDisplayName))
            score++;

        return score;
    }
}
