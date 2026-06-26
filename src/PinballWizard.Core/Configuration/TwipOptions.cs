using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Core.Configuration;

/// <summary>
/// Configuration for the TWIP (This Week in Pinball) newsletter scraper.
/// TWIP is published by Kineticist at twip.kineticist.com (Beehiiv-hosted).
/// Colin Alsheimer / Kineticist granted permission to index content per ADR-0043.
/// robots.txt (verified 2026-06-26) allows all crawlers on /p/* paths.
/// No API key required — TWIP is publicly accessible.
/// </summary>
public sealed class TwipOptions
{
    public const string SectionName = "Twip";

    [Required, Url]
    public string BaseUrl { get; set; } = "https://twip.kineticist.com";

    public string SitemapPath { get; set; } = "/sitemap.xml";

    [Range(1, 365)]
    public int DefaultLookbackDays { get; set; } = 14;

    [Range(1, 2000)]
    public int MaxArticlesToFetch { get; set; } = 500;
}
