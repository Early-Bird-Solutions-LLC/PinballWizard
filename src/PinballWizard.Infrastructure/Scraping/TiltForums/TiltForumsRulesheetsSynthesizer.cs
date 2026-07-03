using System.Globalization;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Application.Rag.Extraction;

namespace PinballWizard.Infrastructure.Scraping.TiltForums;

/// <summary>
/// Converts a <see cref="TiltForumsRulesheetArticle"/> into a list of
/// <see cref="Chunk"/> objects ready for indexing by <c>IRagIndexer</c>.
/// </summary>
/// <remarks>
/// Mirrors <c>KineticistTutorialsSynthesizer</c> exactly: the wiki OP text is
/// already clean, heading-structured content, so this wraps it as a
/// single-page <see cref="ExtractedDocument"/> and hands it to
/// <see cref="IChunker"/> (<c>HybridChunker</c>) — no PDF extraction, no
/// Cosmos write. Called from the <c>--sync-tiltforums-rulesheets</c> CLI
/// verb, which then calls <c>IRagIndexer.UpsertAsync</c> directly.
/// </remarks>
public sealed class TiltForumsRulesheetsSynthesizer
{
    private readonly IChunker _chunker;
    private readonly ILogger<TiltForumsRulesheetsSynthesizer> _logger;

    public TiltForumsRulesheetsSynthesizer(IChunker chunker, ILogger<TiltForumsRulesheetsSynthesizer> logger)
    {
        ArgumentNullException.ThrowIfNull(chunker);
        ArgumentNullException.ThrowIfNull(logger);
        _chunker = chunker;
        _logger = logger;
    }

    /// <summary>
    /// Synthesizes chunks from a <see cref="TiltForumsRulesheetArticle"/>.
    /// Returns an empty list when the article has no usable content.
    /// </summary>
    public IReadOnlyList<Chunk> Synthesize(TiltForumsRulesheetArticle article, ChunkRequest chunkRequest)
    {
        ArgumentNullException.ThrowIfNull(article);
        ArgumentNullException.ThrowIfNull(chunkRequest);

        if (string.IsNullOrWhiteSpace(article.BodyText))
        {
            _logger.LogWarning(
                "TiltForumsRulesheetsSynthesizer: article '{Title}' has empty BodyText; skipping.",
                article.GameTitle);
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
            "TiltForumsRulesheetsSynthesizer: '{Title}' -> {Count} chunk(s) ({Tokens} tokens total).",
            article.GameTitle, chunks.Count, chunks.Sum(c => c.TokenCount));

        return chunks;
    }

    private static string BuildAttributedText(TiltForumsRulesheetArticle article)
    {
        var lines = new System.Text.StringBuilder();
        lines.AppendLine(CultureInfo.InvariantCulture, $"# {article.GameTitle} — Rulesheet");
        lines.Append("Community wiki rulesheet");
        if (!string.IsNullOrWhiteSpace(article.CodeRevision))
        {
            lines.Append(CultureInfo.InvariantCulture, $" (code rev {article.CodeRevision})");
        }
        lines.AppendLine(CultureInfo.InvariantCulture, $". Source: Tilt Forums, {article.TopicUrl}");
        lines.AppendLine();
        lines.Append(article.BodyText);
        return lines.ToString();
    }
}
