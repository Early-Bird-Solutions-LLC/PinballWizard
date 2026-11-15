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
}
