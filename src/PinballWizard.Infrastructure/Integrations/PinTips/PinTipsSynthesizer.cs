using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Application.Rag.Extraction;
using PinballWizard.Core.Models;

namespace PinballWizard.Infrastructure.Integrations.PinTips;

public sealed class PinTipsSynthesizer
{
    private readonly IChunker _chunker;
    private readonly ILogger<PinTipsSynthesizer> _logger;

    public PinTipsSynthesizer(IChunker chunker, ILogger<PinTipsSynthesizer> logger)
    {
        ArgumentNullException.ThrowIfNull(chunker);
        ArgumentNullException.ThrowIfNull(logger);
        _chunker = chunker;
        _logger = logger;
    }

    public IReadOnlyList<Chunk> Synthesize(PinTip tip, string sourceUrl, ChunkRequest chunkRequest)
    {
        ArgumentNullException.ThrowIfNull(tip);
        ArgumentNullException.ThrowIfNull(chunkRequest);

        if (string.IsNullOrWhiteSpace(tip.Text))
        {
            _logger.LogWarning("PinTipsSynthesizer: tip {TipId} has empty text; skipping.", tip.TipId);
            return [];
        }

        var text = BuildText(tip, sourceUrl);
        var extracted = new ExtractedDocument(
            Status: ExtractionStatus.Success,
            Text: text,
            Pages: [new ExtractedPage(PageNumber: 1, Text: text)],
            Outline: [],
            Error: null);

        return _chunker.Chunk(extracted, chunkRequest);
    }

    public static string TitleFor(PinTip tip) =>
        string.Create(CultureInfo.InvariantCulture, $"PinTips {tip.Category} ({tip.OpdbId})");

    private static string BuildText(PinTip tip, string sourceUrl)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# {TitleFor(tip)}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Play tip for OPDB {tip.OpdbId}, category {tip.Category}, {tip.VoteTotal} votes.");
        sb.AppendLine();
        sb.AppendLine(tip.Text);
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Source: PinTips tip {tip.TipId}. Export: {sourceUrl}");
        return sb.ToString();
    }
}
