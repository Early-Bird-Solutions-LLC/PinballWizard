using System.Text.RegularExpressions;
using PinballWizard.Core.Models;

namespace PinballWizard.Application.Documents;

// Factory for DocumentRecord instances backed by synthesized (non-scraped) content sources.
//
// Synthesized sources — Kineticist tutorials, Tilt Forums rulesheets, TWIP newsletters — are
// indexed into AI Search so they appear in RAG answers and get cited in Wizard responses.
// Before this fix they had no corresponding scraped_documents_raw Cosmos record, which meant
// every citation to a synthesized source resolved to "Document not found" at /documents/{id}.
//
// This factory produces the DocumentRecord that is persisted to Cosmos immediately after each
// successful AI Search upsert, making synthesized sources first-class documents in the
// provenance store so their citations resolve at /documents/{id} exactly like scraped docs.
public static partial class SynthesizedDocumentRecordFactory
{
    public static DocumentRecord Create(
        string documentId,
        string title,
        string sourceUrl,
        string discoveryContext,
        DocumentType documentType,
        string fileFormat,
        string manufacturer,
        string? gameTitle,
        string? gameSlug,
        DateTimeOffset synthesizedAt)
    {
        var at = synthesizedAt.UtcDateTime;

        return new DocumentRecord
        {
            DocumentId = documentId,
            Source = new SourceInfo
            {
                DiscoveryUrl = sourceUrl,
                DiscoveryContext = discoveryContext,
                FileUrl = sourceUrl,
                LinkText = title,
                ActionType = ActionType.ExternalLink,
                SourceType = SourceType.SynthesizedArticle,
                ScrapedAt = at,
            },
            Classification = new ClassificationInfo
            {
                DocumentType = documentType,
                FileFormat = fileFormat,
            },
            Game = gameTitle is null
                ? null
                : new GameReference
                {
                    Title = gameTitle,
                    Slug = string.IsNullOrWhiteSpace(gameSlug) ? Slugify(gameTitle) : gameSlug,
                    GamePageUrl = sourceUrl,
                },
            Timeline = new TimelineInfo
            {
                FirstDiscoveredAt = at,
                LastDownloadedAt = at,
            },
            Manufacturer = manufacturer,
        };
    }

    // Converts a display title to a URL-safe slug: lowercase, spaces to hyphens,
    // non-alphanumeric/hyphen characters stripped.
    private static string Slugify(string title)
    {
        var lower = title.ToLowerInvariant();
        var hyphened = lower.Replace(' ', '-');
        return NonSlugCharsRegex().Replace(hyphened, string.Empty);
    }

    [GeneratedRegex(@"[^a-z0-9\-]")]
    private static partial Regex NonSlugCharsRegex();
}
