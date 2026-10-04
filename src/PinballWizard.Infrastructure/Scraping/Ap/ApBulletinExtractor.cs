using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using PinballWizard.Core.Models;

namespace PinballWizard.Infrastructure.Scraping.Ap;

// What one per-game support page yielded. PostCount is every WordPress post
// card on the page, BulletinPostCount the cards in a bulletin category, and
// PostsWithoutDocument the bulletin cards that carried no PDF at all (AP
// publishes some bulletins as YouTube videos). RejectedHosts names the hosts
// of bulletin PDFs that were dropped because they are not AP document hosts.
public sealed record ApBulletinExtraction(
    IReadOnlyList<DiscoveredLink> Links,
    int PostCount,
    int BulletinPostCount,
    int PostsWithoutDocument,
    IReadOnlyCollection<string> RejectedHosts);

// Extracts service-bulletin PDFs from an AP per-game support page
// (/support/{game-slug}/). Pure functions — no I/O.
//
// Each document on the page is an Elementor loop card rendering one WordPress
// post. The card carries WordPress's own post_class output
// ("type-post category-service-bulletin tag-houdini"), so bulletins are
// selected by category — the site's machine-readable classification — rather
// than by section headings. Manuals, code updates, and flyers on the same page
// belong to the game-page scraper and are not bulletins.
public static class ApBulletinExtractor
{
    private static readonly HtmlParser Parser = new();

    public const string DiscoveryContext = "American Pinball Support Page";

    private const string PostCardSelector = ".type-post";
    private const string CategoryClassPrefix = "category-";
    private const string TagClassPrefix = "tag-";

    public static ApBulletinExtraction ExtractBulletins(
        string html,
        Uri supportPageUrl,
        string gameSlug,
        IReadOnlyCollection<string> bulletinCategorySlugs,
        IReadOnlySet<string> gameSlugs)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(supportPageUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameSlug);
        ArgumentNullException.ThrowIfNull(bulletinCategorySlugs);
        ArgumentNullException.ThrowIfNull(gameSlugs);

        var bulletinClasses = bulletinCategorySlugs
            .Where(slug => !string.IsNullOrWhiteSpace(slug))
            .Select(slug => CategoryClassPrefix + slug.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        using var doc = Parser.ParseDocument(html);
        var links = new List<DiscoveredLink>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rejectedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var postCount = 0;
        var bulletinPostCount = 0;
        var postsWithoutDocument = 0;

        foreach (var card in doc.QuerySelectorAll(PostCardSelector))
        {
            postCount++;
            if (!card.ClassList.Any(bulletinClasses.Contains)) continue;

            bulletinPostCount++;
            var title = CardTitle(card);
            var cardGameSlug = CardGameSlug(card, gameSlug, gameSlugs);
            var cardHadPdf = false;

            foreach (var absolute in PdfLinks(card, supportPageUrl))
            {
                cardHadPdf = true;
                if (!ApHosts.IsAllowedDocumentHost(absolute.Host))
                {
                    rejectedHosts.Add(absolute.Host);
                    continue;
                }

                // AbsoluteUri keeps the percent-encoding the page published;
                // HubSpot file names contain spaces.
                var url = absolute.AbsoluteUri;
                if (!seenUrls.Add(url)) continue;

                links.Add(new DiscoveredLink
                {
                    FileUrl = url,
                    LinkText = title,
                    DiscoveryContext = DiscoveryContext,
                    GameSlug = cardGameSlug,
                });
            }

            if (!cardHadPdf) postsWithoutDocument++;
        }

        return new ApBulletinExtraction(links, postCount, bulletinPostCount, postsWithoutDocument, rejectedHosts);
    }

    private static IEnumerable<Uri> PdfLinks(IElement card, Uri pageUrl) =>
        card.QuerySelectorAll("a[href]")
            .Select(anchor => anchor.GetAttribute("href"))
            .Where(href => !string.IsNullOrWhiteSpace(href))
            .Select(href => Uri.TryCreate(pageUrl, href, out var absolute) ? absolute : null)
            .OfType<Uri>()
            .Where(uri => uri.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));

    // A card tagged for several games (the "General" USB-formatting bulletin
    // carries tag-hot-wheels tag-houdini tag-oktoberfest), or for a game other
    // than this hub's, is not this hub's game alone. Binding it to whichever
    // hub was read first would be a guess, so it gets no game slug; the
    // orchestrator records every hub it appeared on as a cross-reference.
    // Only tags that name a game count: AP also has topic tags
    // (announcements, houdini-news).
    private static string? CardGameSlug(IElement card, string hubGameSlug, IReadOnlySet<string> gameSlugs)
    {
        var tags = card.ClassList
            .Where(c => c.StartsWith(TagClassPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(c => c[TagClassPrefix.Length..])
            .Where(gameSlugs.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (tags.Count == 0) return hubGameSlug;
        return tags.Count == 1 && string.Equals(tags[0], hubGameSlug, StringComparison.OrdinalIgnoreCase)
            ? hubGameSlug
            : null;
    }

    // The card's headings are the tag label ("Houdini", or "General" for a
    // bulletin shared across games) followed by the bulletin title. The
    // anchors themselves only say "Download".
    private static string? CardTitle(IElement card)
    {
        var parts = card.QuerySelectorAll("h1, h2")
            .Select(h => h.TextContent.Trim())
            .Where(t => t.Length > 0)
            .ToList();
        return parts.Count == 0 ? null : string.Join(" - ", parts);
    }
}
