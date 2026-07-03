using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Core.Configuration;

// Configuration for the Pinball Brothers Freshdesk support-portal scraper.
// pinballbrothers.freshdesk.com is a separate host from pinballbrothers.com —
// robots.txt (verified 2026-07-03) allows /support/solutions/* and explicitly
// carves out Allow: /helpdesk/attachments from the broader Disallow: /helpdesk/.
public sealed class FreshdeskOptions
{
    public const string SectionName = "PinballBrothersFreshdesk";

    [Required, Url]
    public string BaseUrl { get; set; } = "https://pinballbrothers.freshdesk.com";

    public string SolutionsHomePath { get; set; } = "/support/solutions";
}
