using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Application.Rag.Extraction;

namespace PinballWizard.Infrastructure.Scraping.PinballBrothers.Freshdesk;

// Converts a text-only FreshdeskArticle (no PDF attachment — troubleshooting
// Q&A, "how to" guides, update notes) into Chunk[] ready for AI Search
// indexing. Mirrors TwipNewsletterSynthesizer: builds a single-page
// ExtractedDocument from the article body and passes it to IChunker.
public sealed class PbFreshdeskArticleSynthesizer
{
    private readonly IChunker _chunker;
    private readonly ILogger<PbFreshdeskArticleSynthesizer> _logger;

    public PbFreshdeskArticleSynthesizer(
        IChunker chunker,
        ILogger<PbFreshdeskArticleSynthesizer> logger)
    {
        ArgumentNullException.ThrowIfNull(chunker);
        ArgumentNullException.ThrowIfNull(logger);
        _chunker = chunker;
        _logger = logger;
    }

    // Returns an empty list when the article body is empty or whitespace
    // (logs a warning — no fabrication, per Invariant #17).
    public IReadOnlyList<Chunk> Synthesize(FreshdeskArticle article, ChunkRequest chunkRequest)
    {
        ArgumentNullException.ThrowIfNull(article);
        ArgumentNullException.ThrowIfNull(chunkRequest);

        if (string.IsNullOrWhiteSpace(article.BodyText))
        {
            _logger.LogWarning(
                "PbFreshdeskArticleSynthesizer: article '{Title}' has empty BodyText; skipping.",
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
            "PbFreshdeskArticleSynthesizer: '{Title}' ({Folder}) → {Count} chunk(s) ({Tokens} tokens total).",
            article.Title, article.Folder.FolderName, chunks.Count, chunks.Sum(c => c.TokenCount));

        return chunks;
    }

    private static string BuildAttributedText(FreshdeskArticle article)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# {article.Title}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Pinball Brothers Support — {article.Folder.FolderName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Source: {article.Url}");
        sb.AppendLine();
        sb.Append(article.BodyText);

        return sb.ToString();
    }
}
