using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.ML.Tokenizers;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Application.Rag.Extraction;
using PinballWizard.Core.Models;

namespace PinballWizard.Infrastructure.Scraping.Kineticist;

/// <summary>
/// Converts a <see cref="KineticistTutorialArticle"/> into a list of
/// <see cref="Chunk"/> objects ready for indexing by <c>IRagIndexer</c>.
/// </summary>
/// <remarks>
/// <para>
/// Kineticist tutorial Markdown is already clean, well-structured text —
/// no PDF extraction needed. The synthesizer constructs a single-page
/// <see cref="ExtractedDocument"/> from the Markdown body and passes it
/// to <see cref="IChunker"/> (HybridChunker) for token-budgeted chunking.
/// This mirrors the MetadataCard / GameOverview synthesis paths (CLI verb
/// → synthesizer → <c>IRagIndexer.UpsertAsync</c>) and intentionally
/// bypasses the PDF change-feed pipeline.
/// </para>
/// <para>
/// Author attribution is prepended to the Markdown text so every chunk
/// carries authorship context. The canonical URL lives in
/// <see cref="ChunkRequest.DocumentUrl"/> and flows into the AI Search
/// index as the citation field — this is what the Wizard surfaces on
/// every answer grounded in a Kineticist guide.
/// </para>
/// </remarks>
public sealed class KineticistTutorialsSynthesizer
{
    private readonly IChunker _chunker;
    private readonly TiktokenTokenizer _tokenizer;
    private readonly ILogger<KineticistTutorialsSynthesizer> _logger;

    public KineticistTutorialsSynthesizer(
        IChunker chunker,
        ILogger<KineticistTutorialsSynthesizer> logger)
    {
        ArgumentNullException.ThrowIfNull(chunker);
        ArgumentNullException.ThrowIfNull(logger);
        _chunker = chunker;
        _logger = logger;

        // Match HybridChunker's encoding so token counts are consistent.
        _tokenizer = TiktokenTokenizer.CreateForEncoding("cl100k_base");
    }

    /// <summary>
    /// Synthesizes chunks from a <see cref="KineticistTutorialArticle"/>.
    /// Returns an empty list when the article has no usable content.
    /// </summary>
    public IReadOnlyList<Chunk> Synthesize(KineticistTutorialArticle article, ChunkRequest chunkRequest)
    {
        ArgumentNullException.ThrowIfNull(article);
        ArgumentNullException.ThrowIfNull(chunkRequest);

        if (string.IsNullOrWhiteSpace(article.MarkdownContent))
        {
            _logger.LogWarning(
                "KineticistTutorialsSynthesizer: article '{Title}' has empty MarkdownContent; skipping.",
                article.Title);
            return [];
        }

        // Prepend attribution header so every chunk carries author context.
        // The Markdown already contains "by Author · Date" but prepending a
        // clean attribution line as the document lead ensures it survives
        // chunking and appears in retrieved snippets.
        var attributedText = BuildAttributedText(article);

        // Wrap the Markdown as a single-page ExtractedDocument so HybridChunker
        // can apply its token-budgeted section chunking. PageNumber 1 so
        // page-anchored citations work; outline is empty (chunker falls back to
        // token-window chunking within the single section).
        var extracted = new ExtractedDocument(
            Status: ExtractionStatus.Success,
            Text: attributedText,
            Pages: [new ExtractedPage(PageNumber: 1, Text: attributedText)],
            Outline: [],
            Error: null);

        var chunks = _chunker.Chunk(extracted, chunkRequest);

        _logger.LogDebug(
            "KineticistTutorialsSynthesizer: '{Title}' by {Author} → {Count} chunk(s) ({Tokens} tokens total).",
            article.Title,
            article.Author,
            chunks.Count,
            chunks.Sum(c => c.TokenCount));

        return chunks;
    }

    private static string BuildAttributedText(KineticistTutorialArticle article)
    {
        // Lead with the editorial title (H1) and author attribution so every
        // chunk that reaches the retriever carries the source context.
        // The rest of the Markdown body follows as-is.
        var lines = new System.Text.StringBuilder();
        lines.AppendLine(CultureInfo.InvariantCulture, $"# {article.Title}");
        lines.Append(CultureInfo.InvariantCulture, $"Tutorial by {article.Author}");
        if (article.PublishedAt.HasValue)
        {
            lines.Append(CultureInfo.InvariantCulture, $" ({article.PublishedAt.Value:MMMM d, yyyy})");
        }
        lines.AppendLine(CultureInfo.InvariantCulture, $". Source: {article.CanonicalUrl}");
        lines.AppendLine();

        // Append the full body, stripping the duplicate H1 from the .md body
        // to avoid an awkward doubled title.
        var body = article.MarkdownContent;
        var h1Line = $"# {article.Title}";
        var firstLine = body.AsSpan();
        var newlineIdx = firstLine.IndexOf('\n');
        if (newlineIdx > 0)
        {
            var firstRealLine = firstLine[..newlineIdx].Trim().ToString();
            if (firstRealLine.Equals(h1Line, StringComparison.OrdinalIgnoreCase))
            {
                body = body[(newlineIdx + 1)..].TrimStart('\r', '\n');
            }
        }

        lines.Append(body);
        return lines.ToString();
    }
}
