namespace PinballWizard.Infrastructure.Scraping.PinballBrothers.Freshdesk;

// One Freshdesk solution folder discovered from the /support/solutions
// category page (e.g. CategoryName="FAQs QUEEN", FolderName="QUEEN - Update").
public sealed record FreshdeskFolder(string CategoryName, string FolderName, string Url);

// One article link discovered from a folder's article-list page (pre-fetch —
// no body content yet).
public sealed record FreshdeskArticleSummary(string Title, string Url, FreshdeskFolder Folder);

// One downloadable attachment on an article page.
public sealed record FreshdeskAttachment(string Url, string FileName);

// A fully-fetched Freshdesk article: title, body text, and any attachments.
// Attachments.Count == 0 means this is a text-only article (routed to the
// synthesizer path); Attachments.Count > 0 means it becomes a normal
// ScrapedItem/DiscoveredLink per attachment (routed to the scraper path).
public sealed record FreshdeskArticle
{
    public required string Title { get; init; }
    public required string Url { get; init; }
    public required FreshdeskFolder Folder { get; init; }
    public required string BodyText { get; init; }
    public IReadOnlyList<FreshdeskAttachment> Attachments { get; init; } = [];
}
