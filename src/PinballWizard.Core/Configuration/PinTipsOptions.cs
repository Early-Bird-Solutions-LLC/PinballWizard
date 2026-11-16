using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Core.Configuration;

// Match Play publishes the PinTips corpus as a daily JSON file.
// https://docs.matchplay.events/data-exports
public sealed class PinTipsOptions
{
    public const string SectionName = "PinTips";

    [Required]
    [Url]
    public string ExportUrl { get; set; } =
        "https://mp-data.sfo3.cdn.digitaloceanspaces.com/latest-pintips.json";

    // Tips in flight. Each tip is one short embedding call, then a search
    // upload per machine. 8 overlaps those round-trips without the fan-out
    // a backfill document has (that path stays at 4 because each document
    // embeds many chunks). Internal Azure calls only — the export download
    // is the one request that goes through the politeness gate.
    [Range(1, 16)]
    public int SyncConcurrency { get; set; } = 8;
}
