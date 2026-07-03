using System.Net;
using AngleSharp.Html.Parser;

namespace PinballWizard.Infrastructure.Scraping.PinballBrothers.Freshdesk;

// Extracts title, body text, and attachment links from a single Freshdesk
// support-article page. Pure — no I/O. Selectors verified against real
// pinballbrothers.freshdesk.com markup on 2026-07-03:
//   - Title:      h2.heading (contains a nested #print-article icon anchor
//                 that must be stripped before reading TextContent)
//   - Body:       article.article-body (present on every article; empty
//                 string when the article has no prose, e.g. attachment-only)
//   - Attachment: .attachments a.filename[href] — href is a relative
//                 /helpdesk/attachments/{id} path (robots.txt explicitly
//                 Allows this path); the filename lives in the title=
//                 attribute, not the anchor text (which Freshdesk truncates
//                 with "...").
public static class FreshdeskArticleExtractor
{
    private const string BaseUrl = "https://pinballbrothers.freshdesk.com";

    private static readonly HtmlParser Parser = new();

    public sealed record ExtractedArticleContent(
        string Title,
        string BodyText,
        IReadOnlyList<FreshdeskAttachment> Attachments);

    public static ExtractedArticleContent? Extract(string html, string articleUrl)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentException.ThrowIfNullOrWhiteSpace(articleUrl);

        if (string.IsNullOrWhiteSpace(html)) return null;

        using var doc = Parser.ParseDocument(html);

        var titleEl = doc.QuerySelector("h2.heading");
        if (titleEl is null) return null;

        // Strip the nested "Print this Article" icon anchor before reading
        // TextContent so it doesn't leak "Print" into the title.
        titleEl.QuerySelector("#print-article")?.Remove();
        var title = titleEl.TextContent.Trim();
        if (string.IsNullOrWhiteSpace(title)) return null;

        var bodyEl = doc.QuerySelector("article.article-body");
        var bodyText = NormalizeWhitespace(bodyEl?.TextContent ?? string.Empty);

        var attachments = new List<FreshdeskAttachment>();
        foreach (var anchor in doc.QuerySelectorAll(".attachments a.filename[href]"))
        {
            var href = anchor.GetAttribute("href");
            var fileName = anchor.GetAttribute("title");
            if (string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(fileName)) continue;

            var absoluteUrl = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? href
                : BaseUrl + href;

            attachments.Add(new FreshdeskAttachment(absoluteUrl, WebUtility.HtmlDecode(fileName)));
        }

        return new ExtractedArticleContent(title, bodyText, attachments);
    }

    private static string NormalizeWhitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
