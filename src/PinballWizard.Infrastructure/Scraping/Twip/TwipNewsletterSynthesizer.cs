using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Application.Rag.Extraction;
using PinballWizard.Core.Models;

namespace PinballWizard.Infrastructure.Scraping.Twip;

// Converts a TwipNewsletterArticle into Chunk[] ready for AI Search indexing.
// Mirrors KineticistTutorialsSynthesizer: builds a single-page ExtractedDocument
// from the article body and passes it to IChunker (HybridChunker) for
// token-budgeted chunking. Author attribution and source URL are prepended so
// every retrieved chunk carries provenance context (provenance invariant #1).
public sealed class TwipNewsletterSynthesizer
{
    private readonly IChunker _chunker;
    private readonly ILogger<TwipNewsletterSynthesizer> _logger;

    public TwipNewsletterSynthesizer(
        IChunker chunker,
        ILogger<TwipNewsletterSynthesizer> logger)
    {
        ArgumentNullException.ThrowIfNull(chunker);
        ArgumentNullException.ThrowIfNull(logger);
        _chunker = chunker;
        _logger = logger;
    }

    // Synthesizes chunks from a TwipNewsletterArticle.
    // Returns an empty list when the article body is empty or whitespace
    // (logs a warning — no fabrication, per Invariant #17).
    public IReadOnlyList<Chunk> Synthesize(TwipNewsletterArticle article, ChunkRequest chunkRequest)
    {
        ArgumentNullException.ThrowIfNull(article);
        ArgumentNullException.ThrowIfNull(chunkRequest);

        if (string.IsNullOrWhiteSpace(article.BodyText))
        {
            _logger.LogWarning(
                "TwipNewsletterSynthesizer: article '{Title}' has empty BodyText; skipping.",
                article.Title);
            return [];
        }

        var attributedText = BuildAttributedText(article);

        var extracted = new ExtractedDocument(
            Status: ExtractionStatus.Success,
            Text: attributedText,
            Pages: [new ExtractedPage(PageNumber: 1, Text: attributedText)],
            Outline: [],
            Error: null);

        var chunks = _chunker.Chunk(extracted, chunkRequest);

        _logger.LogDebug(
            "TwipNewsletterSynthesizer: '{Title}' by {Author} → {Count} chunk(s) ({Tokens} tokens total).",
            article.Title,
            article.Author,
            chunks.Count,
            chunks.Sum(c => c.TokenCount));

        return chunks;
    }

    private static string BuildAttributedText(TwipNewsletterArticle article)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# {article.Title}");

        // Lead attribution line: omit the date clause when PublishedAt is null
        // so we never emit "Weekly pinball news by Colin Alsheimer,." (bare comma).
        sb.Append(CultureInfo.InvariantCulture, $"Weekly pinball news by {article.Author}");
        if (article.PublishedAt.HasValue)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $", published {article.PublishedAt.Value:MMMM d, yyyy}");
        }
        sb.AppendLine(".");

        // Optional description block — omit entirely when null/whitespace.
        if (!string.IsNullOrWhiteSpace(article.Description))
        {
            sb.AppendLine(article.Description);
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"Source: {article.CanonicalUrl}");
        sb.AppendLine();
        sb.Append(article.BodyText);

        return sb.ToString();
    }
}
